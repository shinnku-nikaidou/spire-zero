using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>A beam state as a turn's search needs it: the actions from the decision to it, and the record of enemy
/// moves up to it, for the leaves.</summary>
public sealed record PlanJob(IReadOnlyList<CombatAction> Path, IReadOnlyList<EnemyTurns> History);

/// <summary>Searches one turn from each job's state (<see cref="SlPlanner.SearchTurn"/>), in job order, wherever the
/// world lives (this process, or a worker), each with <paramref name="nodes"/> nodes, keeping
/// <paramref name="keep"/> states, none that cannot beat <paramref name="floor"/>.</summary>
public delegate IReadOnlyList<TurnSearch> TurnSearcher(IReadOnlyList<PlanJob> jobs, int nodes, int keep, double? floor);

/// <summary>The SL planner's answer, and how its search went. A losing line's plan stops at the end of its first turn.</summary>
/// <param name="Value">The value of the line's end: the leaf at the end of the combat, or at the last state searched
/// when the line stops before it.</param>
/// <param name="End">How the line ends: won, lost, or still in progress where the search stopped.</param>
/// <param name="Nodes">The actions simulated, over all the turns' searches.</param>
/// <param name="Turns">How many turns the beam went on.</param>
/// <param name="Exhaustive">Whether nothing was left out: no turn's search ran out of nodes or kept fewer states than
/// it found, no beam dropped a state, and every line reached the end of the combat or was pruned. The line is then the
/// best there is, as far as the bound holds (<see cref="TurnEnd.Bound"/>).</param>
public sealed record SlPlan(CombatDecisionResult Result, double Value, CombatStatus End, int Nodes, int Turns, bool Exhaustive);

/// <summary>
/// The SL combat brain (docs: combat-brain §3): deterministic planning over the whole combat in the one exact world,
/// the node's clone. Nothing is hidden there and nothing is random, so the best line is the answer, with no average
/// over worlds; it is planned once and then played as planned (Harness's route follows it with a <see cref="TurnPlan"/>).
///
/// A beam over turns. Each state kept is searched to the end of its turn (<see cref="SearchTurn"/>): a depth-first
/// search over the turn's actions, ending the turn an option at every step, with transpositions merged by the
/// observation and the world's full hidden state (<see cref="ICombatWorld.HiddenKey"/>). Each distinct state where the
/// turn ends, after the enemies' turn, is scored by the leaf (<see cref="ICombatLeaf"/>, which reads the observation
/// alone); the best <see cref="Budget.Beam"/> of all the states' ends, distinct by key, are the next turn's states. A
/// line that ends the combat is scored by the leaf at its end, and the best such line so far prunes every state whose
/// bound, a win with the HP and the potions it has, cannot beat it. The beam stops when no state is left, or after
/// <see cref="MaxTurns"/> turns. Every loss is worth the same, so among losing lines the one that died from the turn
/// start the leaf valued highest is the pick; and a losing plan is played for its first turn only, then the next turn
/// is planned again, from where a beam reaches deeper.
///
/// Only one turn's transpositions are merged, so each state kept is the end of a concrete line of actions, and the
/// plan replays exactly in the world it was searched in. Card prompts are answered by <see cref="PromptBrain"/>, as in
/// the run. The turns' searches are independent, so a turn is one <see cref="TurnSearcher"/> call that can run them
/// apart: <see cref="Plan"/> runs them here, Harness's pooled planner in workers.
/// </summary>
public sealed class SlPlanner(ICombatLeaf? leaf = null) : ICombatBrain
{
    /// <summary>How many turns a plan may look ahead; a line still going after them ends there, and is planned again.</summary>
    public const int MaxTurns = 40;

    private readonly ICombatLeaf _leaf = leaf ?? new HandLeaf();

    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns>? history = null)
    {
        if (worlds.Environment != PlayEnvironment.SL) throw new InvalidOperationException($"the SL planner in {worlds.Environment}");
        return legal.Count == 1 ? PimcBrain.Only(legal[0]) : Plan(observation, worlds.Worlds(1, 0)[0], budget, history).Result;
    }

    /// <summary>Plans the rest of the combat in <paramref name="world"/>, the SL world at the decision, searching here.</summary>
    public SlPlan Plan(CombatObservation observation, ICombatWorld world, Budget budget, IReadOnlyList<EnemyTurns>? history = null)
    {
        var root = world.Snapshot();
        return Search(budget.For(observation.Room), history ?? [], (jobs, nodes, keep, floor) => jobs.Select(job =>
        {
            world.Restore(root);
            foreach (var action in job.Path) world.Execute(action);
            return SearchTurn(world, job.History, nodes, keep, floor);
        }).ToList());
    }

    /// <summary>The beam over turns, from the decision's state, with the budget of its room (<see cref="Budget.For"/>).</summary>
    public static SlPlan Search(Budget budget, IReadOnlyList<EnemyTurns> history, TurnSearcher search)
    {
        int beam = Math.Max(1, budget.Beam);
        var states = new List<TurnEnd> { new([], [], CombatStatus.InProgress, 0, double.PositiveInfinity, 0, history) };
        TurnEnd? best = null;
        double bestFrom = double.NegativeInfinity;
        var byFirst = new Dictionary<CombatAction, double>();
        int nodes = 0, turns = 0;
        bool exhaustive = true;
        while (states.Count > 0 && turns < MaxTurns)
        {
            var found = search(states.Select(s => new PlanJob(s.Actions, s.History)).ToList(), budget.NodesPerWorld, beam, best?.Value);
            turns++;
            var next = new List<TurnEnd>();
            for (int i = 0; i < states.Count; i++)
            {
                nodes += found[i].Nodes;
                exhaustive &= !found[i].Cut;
                foreach (var end in found[i].Ends)
                {
                    var line = end with { Actions = [.. states[i].Actions, .. end.Actions], Hashes = [.. states[i].Hashes, .. end.Hashes] };
                    if (line.Status == CombatStatus.InProgress)
                    {
                        next.Add(line);
                        continue;
                    }
                    Note(byFirst, line);
                    // A loss is 0 however it comes: then the turn start it came from decides.
                    double from = states[i].Value;
                    if (best == null || line.Value > best.Value || (line.Value == best.Value && line.Status == CombatStatus.Lost && from > bestFrom))
                        (best, bestFrom) = (line, from);
                }
            }
            var seen = new HashSet<ulong>();
            states = [];
            // A stable sort: ties stay in the order the states were searched in, so the beam does not depend on timing.
            foreach (var line in next.Where(l => best == null || l.Bound > best.Value).OrderByDescending(l => l.Value))
            {
                if (!seen.Add(line.Key)) continue;
                if (states.Count == beam)
                {
                    exhaustive = false;
                    break;
                }
                states.Add(line);
            }
        }
        exhaustive &= states.Count == 0;
        foreach (var state in states) Note(byFirst, state);
        // The best end of the combat, unless the search stopped first at a state the leaf values higher.
        var pick = (states.Count > 0 && (best == null || states[0].Value > best.Value) ? states[0] : best)
            ?? throw new InvalidOperationException("the planner found no line");
        var candidates = byFirst.OrderByDescending(kv => kv.Key == pick.Actions[0]).ThenByDescending(kv => kv.Value)
            .Select(kv => new Candidate(kv.Key.ToString(), kv.Value, 0, 1, [Math.Round(kv.Value, 4)])).ToList();
        int turnEnd = pick.Actions.ToList().FindIndex(a => a.Kind == CombatActionKind.EndTurn);
        int played = pick.Status == CombatStatus.Lost && turnEnd >= 0 ? turnEnd + 1 : pick.Actions.Count;
        var result = new CombatDecisionResult(pick.Actions[0], pick.Actions.Take(played).Skip(1).ToList(), candidates, pick.Hashes.Take(played).Skip(1).ToList());
        return new(result, pick.Value, pick.Status, nodes, turns, exhaustive);
    }

    /// <summary>Keeps the best value of the lines that start with each first action, for the candidates.</summary>
    private static void Note(Dictionary<CombatAction, double> byFirst, TurnEnd line)
    {
        if (!byFirst.TryGetValue(line.Actions[0], out var value) || line.Value > value) byFirst[line.Actions[0]] = line.Value;
    }

    /// <summary>
    /// Searches the rest of the turn from the world's state, with <paramref name="nodes"/> nodes: the best end of the
    /// combat the turn can reach, if any, and the best <paramref name="keep"/> distinct states at the next turn's start.
    /// None whose bound cannot beat <paramref name="floor"/>.
    /// </summary>
    /// <param name="history">The record of enemy moves up to the world's state; each end's goes on from it.</param>
    public TurnSearch SearchTurn(ICombatWorld world, IReadOnlyList<EnemyTurns> history, int nodes, int keep, double? floor) =>
        new TurnDfs(world, _leaf, history, nodes, floor).Run(keep);

    private sealed class TurnDfs(ICombatWorld world, ICombatLeaf leaf, IReadOnlyList<EnemyTurns> history, int nodes, double? floor)
    {
        private readonly HashSet<(ulong Obs, ulong Hidden)> _seen = [];
        private readonly HashSet<ulong> _keys = [];
        private readonly List<TurnEnd> _ends = [];
        private readonly List<CombatAction> _actions = [];
        private readonly List<ulong> _hashes = [];
        private int _nodes, _round;
        private bool _cut;

        public TurnSearch Run(int keep)
        {
            var obs = world.Observe();
            _round = obs.Round;
            Visit(obs);
            var over = _ends.Where(e => e.Status != CombatStatus.InProgress).MaxBy(e => e.Value);
            var going = _ends.Where(e => e.Status == CombatStatus.InProgress).OrderByDescending(e => e.Value).ToList();
            if (going.Count > keep) _cut = true;
            return new([.. over == null ? [] : new[] { over }, .. going.Take(keep)], _nodes, _cut);
        }

        /// <summary>Tries every action from the current state, where the player is still on this turn, once per state.</summary>
        private void Visit(CombatObservation obs)
        {
            ulong hash = ProtocolJson.Hash(obs);
            if (!_seen.Add((hash, world.HiddenKey()))) return;
            var here = world.Snapshot();
            bool moved = false;
            foreach (var action in PimcBrain.Ordered(world.Legal()))
            {
                if (moved && _nodes >= nodes)
                {
                    _cut = true;
                    break;
                }
                if (moved) world.Restore(here);
                moved = true;
                _nodes++;
                world.Execute(action);
                _actions.Add(action);
                _hashes.Add(hash);
                var status = world.Status;
                var after = world.Observe();
                if (status != CombatStatus.InProgress || action.Kind == CombatActionKind.EndTurn || after.Round != _round) End(status, after);
                else Visit(after);
                _actions.RemoveAt(_actions.Count - 1);
                _hashes.RemoveAt(_hashes.Count - 1);
            }
        }

        /// <summary>The turn ended here, or the combat did.</summary>
        private void End(CombatStatus status, CombatObservation obs)
        {
            bool going = status == CombatStatus.InProgress;
            var record = new MoveRecord(history);
            if (going) record.Witness(obs);
            var turns = record.History;
            double value = leaf.Value(status, obs, turns);
            double bound = going ? leaf.Value(CombatStatus.Won, obs, turns) : value;
            if (bound <= floor) return;
            ulong key = going ? Mix(ProtocolJson.Hash(obs), world.HiddenKey()) : 0;
            if (going && !_keys.Add(key)) return;
            _ends.Add(new([.. _actions], [.. _hashes], status, value, bound, key, turns));
        }

        private static ulong Mix(ulong a, ulong b)
        {
            ulong z = a ^ (b + 0x9E3779B97F4A7C15 + (a << 6) + (a >> 2));
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }
}
