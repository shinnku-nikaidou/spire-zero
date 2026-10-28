using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>A beam state as a turn's search needs it: the actions from the decision to it, and the record of enemy
/// moves up to it, for the leaves.</summary>
public sealed record PlanJob(IReadOnlyList<CombatAction> Path, IReadOnlyList<EnemyTurns> History);

/// <summary>Searches one turn from each job's state (<see cref="SlPlanner.SearchTurn"/>), in job order, wherever the
/// world lives (this process, or a worker), each with <paramref name="nodes"/> nodes, keeping
/// <paramref name="keep"/> states, none that cannot beat <paramref name="floor"/>.</summary>
public delegate IReadOnlyList<TurnSearch> TurnSearcher(IReadOnlyList<PlanJob> jobs, int nodes, int keep, double? floor);

/// <summary>The SL planner's answer, and how its search went. A losing line's plan stops at the end of its
/// <see cref="Budget.Replan"/>th turn, unless the loss is proved.</summary>
/// <param name="Value">The value of the line's end: the leaf at the end of the combat, or at the last state searched
/// when the line stops before it.</param>
/// <param name="End">How the line ends: won, lost, or still in progress where the search stopped.</param>
/// <param name="Nodes">The actions simulated, over all the turns' searches.</param>
/// <param name="Turns">How many turns the beams searched, the dive's included.</param>
/// <param name="Proved">Whether the line is the best there is, as far as the bound holds (<see cref="TurnEnd.Bound"/>):
/// it reaches the decision's bound (a win that loses no HP and no potion), or the full beam left nothing out, with no
/// turn's search out of nodes or keeping fewer states than it found, no state dropped from the beam, and every line at
/// the end of the combat or pruned.</param>
public sealed record SlPlan(CombatDecisionResult Result, double Value, CombatStatus End, int Nodes, int Turns, bool Proved);

/// <summary>
/// The SL planner as a combat route plays it (docs: combat-brain §3), one per run: a plan is followed step by step with a
/// <see cref="TurnPlan"/>, and planned again only where it does not hold or has run out. An unproved losing plan runs out
/// after its <see cref="Budget.Replan"/> turns; after <see cref="Budget.LossPlans"/> of them in a row, the last is played
/// to its end. A combat ends with its first plan that is not an unproved loss, or with the run, so the plans in a row are
/// the combat's.
/// </summary>
public sealed class SlRoute(Budget budget)
{
    private readonly TurnPlan _plan = new();
    private int _losses;

    /// <param name="plan">Plans the rest of the combat at the decision with the budget it is given.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<Budget, SlPlan> plan) =>
        _plan.Decide(observation, legal, () =>
        {
            if (legal.Count == 1) return PimcBrain.Only(legal[0]);
            var made = plan(budget.LossPlans > 0 && _losses + 1 >= budget.LossPlans ? budget with { Replan = 0 } : budget);
            _losses = made.End == CombatStatus.Lost && !made.Proved ? _losses + 1 : 0;
            return made.Result;
        });
}

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
/// <see cref="MaxTurns"/> turns. A greedy dive, a beam of one, goes first, so the full beam is pruned from its start.
/// Every loss is worth the same, so among losing lines the one that died from the turn
/// start the leaf valued highest is the pick; and a losing plan that is not proved is played for its first
/// <see cref="Budget.Replan"/> turns only, then planned again, from where a beam reaches deeper (or to its end, at 0).
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

    /// <summary>
    /// The beam over turns, from the decision's state, with the budget of its room (<see cref="Budget.For"/>). A greedy
    /// dive comes first, a beam of one: its line, often a win, is the floor that prunes the full beam from its first
    /// turn on, and when it already reaches the decision's bound the full beam is not needed.
    /// </summary>
    public static SlPlan Search(Budget budget, IReadOnlyList<EnemyTurns> history, TurnSearcher search)
    {
        var beam = new Beam(budget.NodesPerWorld, history, search);
        if (budget.Beam > 1) beam.Run(1);
        if (!beam.Proved) beam.Run(budget.Beam);
        return beam.Result(budget.Replan);
    }

    private sealed class Beam(int nodesPerState, IReadOnlyList<EnemyTurns> history, TurnSearcher search)
    {
        private readonly Dictionary<CombatAction, double> _byFirst = [];
        private TurnEnd? _best;
        private double _bestFrom = double.NegativeInfinity, _bound = double.PositiveInfinity;
        private List<TurnEnd> _left = [];
        private int _nodes, _turns;
        private bool _complete;

        /// <summary>Whether the best line is the best there is: the last beam left nothing out, or the line reaches the
        /// decision's bound, which nothing can beat.</summary>
        public bool Proved => _best != null && (_complete || _best.Value >= _bound);

        /// <summary>A beam of <paramref name="width"/> states from the decision, pruned by the best line so far.</summary>
        public void Run(int width)
        {
            var states = new List<TurnEnd> { new([], [], CombatStatus.InProgress, 0, double.PositiveInfinity, 0, history) };
            _complete = true;
            for (int turn = 0; states.Count > 0 && turn < MaxTurns; turn++)
            {
                var found = search(states.Select(s => new PlanJob(s.Actions, s.History)).ToList(), nodesPerState, width, _best?.Value);
                _turns++;
                if (turn == 0) _bound = found[0].Bound;
                var next = new List<TurnEnd>();
                for (int i = 0; i < states.Count; i++)
                {
                    _nodes += found[i].Nodes;
                    _complete &= !found[i].Cut;
                    foreach (var end in found[i].Ends)
                    {
                        var line = end with { Actions = [.. states[i].Actions, .. end.Actions], Hashes = [.. states[i].Hashes, .. end.Hashes] };
                        if (line.Status == CombatStatus.InProgress)
                        {
                            next.Add(line);
                            continue;
                        }
                        Note(_byFirst, line);
                        // A loss is 0 however it comes: then the turn start it came from decides.
                        double from = states[i].Value;
                        if (_best == null || line.Value > _best.Value || (line.Value == _best.Value && line.Status == CombatStatus.Lost && from > _bestFrom))
                            (_best, _bestFrom) = (line, from);
                    }
                }
                var seen = new HashSet<ulong>();
                states = [];
                // A stable sort: ties stay in the order the states were searched in, so the beam does not depend on timing.
                foreach (var line in next.Where(l => _best == null || l.Bound > _best.Value).OrderByDescending(l => l.Value))
                {
                    if (!seen.Add(line.Key)) continue;
                    if (states.Count == width)
                    {
                        _complete = false;
                        break;
                    }
                    states.Add(line);
                }
            }
            _complete &= states.Count == 0;
            _left = states;
        }

        /// <param name="replan">The turns of an unproved losing line played before planning again; 0 plays it to its end.</param>
        public SlPlan Result(int replan)
        {
            foreach (var state in _left) Note(_byFirst, state);
            // The best end of the combat, unless the search stopped first at a state the leaf values higher.
            var pick = (_left.Count > 0 && (_best == null || _left[0].Value > _best.Value) ? _left[0] : _best)
                ?? throw new InvalidOperationException("the planner found no line");
            var candidates = _byFirst.OrderByDescending(kv => kv.Key == pick.Actions[0]).ThenByDescending(kv => kv.Value)
                .Select(kv => new Candidate(kv.Key.ToString(), kv.Value, 0, 1, [Math.Round(kv.Value, 4)])).ToList();
            // A loss the search may have been wrong about is played replan turns at a time; a proved one has nothing better.
            int played = pick.Actions.Count;
            if (pick.Status == CombatStatus.Lost && !Proved && replan > 0)
                for (int i = 0, ends = 0; i < pick.Actions.Count; i++)
                    if (pick.Actions[i].Kind == CombatActionKind.EndTurn && ++ends == replan)
                    {
                        played = i + 1;
                        break;
                    }
            var result = new CombatDecisionResult(pick.Actions[0], pick.Actions.Take(played).Skip(1).ToList(), candidates,
                pick.Hashes.Take(played).Skip(1).ToList());
            return new(result, pick.Value, pick.Status, _nodes, _turns, Proved);
        }
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
        private int _nodes, _round, _limit;
        private bool _cut;

        public TurnSearch Run(int keep)
        {
            var obs = world.Observe();
            _round = obs.Round;
            Visit(obs, root: true);
            var over = _ends.Where(e => e.Status != CombatStatus.InProgress).MaxBy(e => e.Value);
            var going = _ends.Where(e => e.Status == CombatStatus.InProgress).OrderByDescending(e => e.Value).ToList();
            if (going.Count > keep) _cut = true;
            return new([.. over == null ? [] : new[] { over }, .. going.Take(keep)], _nodes, _cut, leaf.Value(CombatStatus.Won, obs, history));
        }

        /// <summary>
        /// Tries every action from the current state, where the player is still on this turn, once per state. At the
        /// turn's start the nodes are shared over the first actions, each with an even share plus what the ones before it
        /// left, so every first action is looked at however many states the turn has; below, a subtree spends its share
        /// depth first.
        /// </summary>
        private void Visit(CombatObservation obs, bool root = false)
        {
            ulong hash = ProtocolJson.Hash(obs);
            if (!_seen.Add((hash, world.HiddenKey()))) return;
            var here = world.Snapshot();
            bool moved = false;
            var legal = PimcBrain.Ordered(world.Legal());
            for (int i = 0; i < legal.Count; i++)
            {
                var action = legal[i];
                if (root) _limit = Math.Max(_nodes + 1, (int)((long)nodes * (i + 1) / legal.Count));
                else if (moved && _nodes >= _limit)
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
