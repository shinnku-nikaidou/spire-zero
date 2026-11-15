using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>A beam state as a turn's search needs it: the actions from the decision to it, and the record of enemy
/// moves up to it, for the leaves.</summary>
public sealed record PlanJob(IReadOnlyList<CombatAction> Path, IReadOnlyList<EnemyTurns> History);

/// <summary>Searches one turn from each job's state (<see cref="SlPlanner.SearchTurn"/>), in job order, wherever the
/// world lives (this process, or a worker), each with <paramref name="nodes"/> nodes, keeping
/// <paramref name="keep"/> states.</summary>
public delegate IReadOnlyList<TurnSearch> TurnSearcher(IReadOnlyList<PlanJob> jobs, int nodes, int keep);

/// <summary>The SL planner's answer, and how its search went. A losing line's plan stops at the end of its
/// <see cref="Budget.Replan"/>th turn, unless the loss is proved.</summary>
/// <param name="Value">The value of the line's end: the leaf at the end of the combat, or at the last state searched
/// when the line stops before it.</param>
/// <param name="End">How the line ends: won, lost, or still in progress where the search stopped.</param>
/// <param name="Nodes">The actions simulated, over all the turns' searches.</param>
/// <param name="Turns">How many turns the beams searched, the dive's included.</param>
/// <param name="Proved">Whether the line is the best there is: the full beam left nothing out, with no turn's search out
/// of nodes or keeping fewer states than it found, no state dropped from the beam, and every line at the end of the
/// combat.</param>
public sealed record SlPlan(CombatDecisionResult Result, double Value, CombatStatus End, int Nodes, int Turns, bool Proved);

/// <summary>
/// The SL planner as a combat route plays it, one per run: a plan is followed step by step with a
/// <see cref="TurnPlan"/>, and planned again only where it does not hold or has run out. An unproved losing plan runs out
/// after its <see cref="Budget.Replan"/> turns; after <see cref="Budget.LossPlans"/> of them in a row, the last is played
/// to its end. A combat ends with its first plan that is not an unproved loss, or with the run, so the plans in a row are
/// the combat's. Each plan has the budget of the combat's room (<see cref="Budget.For"/>).
/// </summary>
public sealed class SlRoute(Budget budget)
{
    private readonly TurnPlan _plan = new();
    private int _losses;

    /// <param name="plan">Plans the rest of the combat at the decision with the budget it is given.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<Budget, SlPlan> plan) =>
        _plan.Decide(observation, legal, () =>
        {
            if (PimcBrain.Forced(observation, legal) is { } forced) return PimcBrain.Only(forced);
            var fight = budget.For(observation.Room);
            var made = plan(fight.LossPlans > 0 && _losses + 1 >= fight.LossPlans ? fight with { Replan = 0 } : fight);
            _losses = made.End == CombatStatus.Lost && !made.Proved ? _losses + 1 : 0;
            return made.Result;
        });
}

/// <summary>
/// The SL combat brain: deterministic planning over the whole combat in the one exact world,
/// the node's clone. Nothing is hidden there and nothing is random, so the best line is the answer, with no average
/// over worlds; it is planned once and then played as planned (Harness's route follows it with a <see cref="TurnPlan"/>).
///
/// A beam over turns. Each state kept is searched to the end of its turn (<see cref="SearchTurn"/>): a depth-first
/// search over the turn's actions, ending the turn an option at every step, one action of each that are the same and
/// transpositions merged by the state as search tells it (<see cref="ICombatWorld.StateKey"/>: the same cards in another
/// order, or another copy of one, are one state) and the world's full hidden state (<see cref="ICombatWorld.HiddenKey"/>).
/// Each distinct state where the turn ends, after the enemies' turn, is scored by the leaf (<see cref="ICombatLeaf"/>,
/// which reads the observation alone); the best <see cref="Budget.Beam"/> of all the states' ends, distinct by key, are the next turn's states. A
/// line that ends the combat is scored by the leaf at its end. The beam stops when no state is left, or after
/// <see cref="MaxTurns"/> turns. Nothing else prunes it: the leaf prices a win by V on the run the combat leaves, which a
/// line can still raise past winning now with what the state holds (max HP or gold gained, a card grown, a potion drunk
/// that V rates below an empty slot), so no state's value caps its lines'. A greedy dive, a beam of one, goes first.
/// Every loss is worth the same, so among losing lines the one that died from the turn
/// start the leaf valued highest is the pick. Among wins worth exactly the same, the one with the fewest turns ended
/// is: a combat no line loses HP in has many, and the dive, which goes first, often finds a slow one. A losing plan
/// that is not proved is played for its first <see cref="Budget.Replan"/> turns only, then planned again, from where a beam reaches deeper (or to its end, at 0).
///
/// Only one turn's transpositions are merged, so each state kept is the end of a concrete line of actions, and the
/// plan replays exactly in the world it was searched in. A card prompt is a state like any other, and its answers are
/// its actions (<see cref="PromptAnswers"/>): every prompt of a line is a branch, and a line ends only where the player
/// can play again. A cycle goes to <see cref="LoopSearch"/>. The turns' searches are
/// independent, so a turn is one <see cref="TurnSearcher"/> call that can run them apart: <see cref="Plan"/> runs them
/// here, Harness's pooled planner in workers.
/// </summary>
public sealed class SlPlanner(ICombatLeaf leaf)
{
    /// <summary>How many turns a plan may look ahead; a line still going after them ends there, and is planned again.</summary>
    public const int MaxTurns = 40;

    /// <summary>Plans the rest of the combat in <paramref name="world"/>, the SL world at the decision, searching here.</summary>
    public SlPlan Plan(ICombatWorld world, Budget budget, IReadOnlyList<EnemyTurns>? history = null)
    {
        var root = world.Snapshot();
        var plan = Search(budget, history ?? [], (jobs, nodes, keep) => [.. jobs.Select(job =>
        {
            world.Restore(root);
            foreach (var action in job.Path) world.Execute(action);
            return SearchTurn(world, job.History, nodes, keep);
        })]);
        world.Forget(root);
        return plan;
    }

    /// <summary>
    /// The beam over turns, from the decision's state. A greedy dive comes first, a beam of one, each of its turns
    /// searched in full and kept for the full beam, which takes them from there. When the dive wins, the full beam keeps
    /// <see cref="Budget.EasyBeam"/> states, if set: it only improves a win already found.
    /// </summary>
    public static SlPlan Search(Budget budget, IReadOnlyList<EnemyTurns> history, TurnSearcher search)
    {
        var beam = new Beam(budget.NodesPerWorld, history, search);
        if (budget.Beam > 1) beam.Run(1);
        if (!beam.Proved) beam.Run(beam.Wins && budget.EasyBeam > 0 ? budget.EasyBeam : budget.Beam);
        return beam.Result(budget.Replan);
    }

    private sealed class Beam(int nodesPerState, IReadOnlyList<EnemyTurns> history, TurnSearcher search)
    {
        /// <summary>The dive's turns, each searched in full (every end), by the path to the turn's state.</summary>
        private readonly Dictionary<string, TurnSearch> _searched = [];
        private readonly Dictionary<CombatAction, TurnEnd> _byFirst = [];
        private TurnEnd? _best;
        private double _bestFrom = double.NegativeInfinity;
        private int _bestEnds;
        private List<TurnEnd> _left = [];
        private int _nodes, _turns;
        private bool _complete;

        /// <summary>Whether the best line is the best there is: the last beam left nothing out.</summary>
        public bool Proved => _best != null && _complete;

        /// <summary>Whether the best line so far wins the combat.</summary>
        public bool Wins => _best?.Status == CombatStatus.Won;

        /// <summary>A beam of <paramref name="width"/> states from the decision.</summary>
        public void Run(int width)
        {
            var states = new List<TurnEnd> { new([], [], CombatStatus.InProgress, 0, 0, history) };
            _complete = true;
            for (int turn = 0; states.Count > 0 && turn < MaxTurns; turn++)
            {
                var found = Search(states, width);
                _turns++;
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
                        // A loss is 0 however it comes: then the turn start it came from decides. Wins worth exactly the
                        // same: the one with fewer turns ended.
                        double from = states[i].Value;
                        int ends = line.Actions.Count(a => a.Kind == CombatActionKind.EndTurn);
                        if (_best == null || line.Value > _best.Value || (line.Value == _best.Value && (line.Status == CombatStatus.Lost
                                ? from > _bestFrom : line.Status == CombatStatus.Won && ends < _bestEnds)))
                            (_best, _bestFrom, _bestEnds) = (line, from, ends);
                    }
                }
                var seen = new HashSet<ulong>();
                states = [];
                // A stable sort: ties stay in the order the states were searched in, so the beam does not depend on timing.
                foreach (var line in next.OrderByDescending(l => l.Value))
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

        /// <summary>
        /// The turn's search from each state, as <see cref="SearchTurn"/> finds it with this width. The dive asks for
        /// each turn in full and keeps it, and a later beam that comes to the same
        /// state takes its ends from there (<see cref="Narrow"/>) instead of searching the turn again: the full beam's
        /// first turn always, and the dive's later states when it keeps them.
        /// </summary>
        private List<TurnSearch> Search(List<TurnEnd> states, int width)
        {
            bool dive = width == 1;
            var found = new TurnSearch[states.Count];
            var asked = new List<int>();
            for (int i = 0; i < states.Count; i++)
                if (_searched.TryGetValue(PathKey(states[i].Actions), out var full)) found[i] = Narrow(full, width) with { Nodes = 0 };
                else asked.Add(i);
            if (asked.Count == 0) return [.. found];
            var replies = search([.. asked.Select(i => new PlanJob(states[i].Actions, states[i].History))], nodesPerState,
                dive ? int.MaxValue : width);
            for (int k = 0; k < asked.Count; k++)
            {
                int i = asked[k];
                if (dive) _searched[PathKey(states[i].Actions)] = replies[k];
                found[i] = dive ? Narrow(replies[k], width) : replies[k];
            }
            return [.. found];
        }

        private static string PathKey(IReadOnlyList<CombatAction> path) => string.Join(' ', path);

        /// <param name="replan">The turns of an unproved losing line played before planning again; 0 plays it to its end.</param>
        public SlPlan Result(int replan)
        {
            foreach (var state in _left) Note(_byFirst, state);
            // The best end of the combat, unless the search stopped first at a state the leaf values higher.
            var pick = (_left.Count > 0 && (_best == null || _left[0].Value > _best.Value) ? _left[0] : _best)
                ?? throw new InvalidOperationException("the planner found no line");
            var candidates = _byFirst.OrderByDescending(kv => kv.Key == pick.Actions[0]).ThenByDescending(kv => kv.Value.Value)
                .Select(kv => new Candidate(kv.Key.ToString(), kv.Value.Value, 0, 1, [Math.Round(kv.Value.Value, 4)],
                    Math.Round(kv.Value.Win, 4), Math.Round(kv.Value.Hp, 1))).ToList();
            // A loss the search may have been wrong about is played replan turns at a time; a proved one has nothing better.
            int played = pick.Actions.Count;
            if (pick.Status == CombatStatus.Lost && !Proved && replan > 0)
                for (int i = 0, ends = 0; i < pick.Actions.Count; i++)
                    if (pick.Actions[i].Kind == CombatActionKind.EndTurn && ++ends == replan)
                    {
                        played = i + 1;
                        break;
                    }
            var result = new CombatDecisionResult(pick.Actions[0], [.. pick.Actions.Take(played).Skip(1)], candidates,
                [.. pick.Hashes.Take(played).Skip(1)], Nodes: _nodes, Turns: _turns);
            return new(result, pick.Value, pick.Status, _nodes, _turns, Proved);
        }
    }

    /// <summary>Keeps the best of the lines that start with each first action, for the candidates.</summary>
    private static void Note(Dictionary<CombatAction, TurnEnd> byFirst, TurnEnd line)
    {
        if (!byFirst.TryGetValue(line.Actions[0], out var best) || line.Value > best.Value) byFirst[line.Actions[0]] = line;
    }

    /// <summary>
    /// What <see cref="SearchTurn"/> finds with <paramref name="keep"/>, from what it found with every end kept: the kept
    /// states are the best, in the same order.
    /// </summary>
    public static TurnSearch Narrow(TurnSearch full, int keep)
    {
        var over = full.Ends.Where(e => e.Status != CombatStatus.InProgress).ToList();
        var going = full.Ends.Where(e => e.Status == CombatStatus.InProgress).ToList();
        return full with { Ends = [.. over, .. going.Take(keep)], Cut = full.Cut || going.Count > keep };
    }

    /// <summary>
    /// Searches the rest of the turn from the world's state, with <paramref name="nodes"/> nodes: the best end of the
    /// combat the turn can reach, if any, and the best <paramref name="keep"/> distinct states at the next turn's start.
    /// </summary>
    /// <param name="history">The record of enemy moves up to the world's state; each end's goes on from it.</param>
    public TurnSearch SearchTurn(ICombatWorld world, IReadOnlyList<EnemyTurns> history, int nodes, int keep) =>
        new TurnDfs(world, leaf, history, nodes).Run(keep);

    private sealed class TurnDfs(ICombatWorld world, ICombatLeaf leaf, IReadOnlyList<EnemyTurns> history, int nodes)
    {
        private readonly HashSet<(ulong Obs, ulong Hidden)> _seen = [];
        private readonly HashSet<ulong> _keys = [];
        private readonly List<TurnEnd> _ends = [];
        /// <summary>The lines the search has ended, in the order found, until the leaf is asked about them all.</summary>
        private readonly List<Ended> _ended = [];
        private readonly List<CombatAction> _actions = [];
        private readonly List<ulong> _hashes = [];
        private readonly List<LoopPoint> _path = [];
        /// <summary>Snapshots of states the search has left that a cycle found there starts from: kept until the cycles are run.</summary>
        private readonly List<WorldSnapshot> _kept = [];
        private int _nodes, _round, _limit;
        private bool _cut;
        private LoopSearch _loops = null!;

        public TurnSearch Run(int keep)
        {
            var obs = world.Observe();
            _round = obs.Round;
            _loops = new(world, _round, End);
            Visit(obs, nodes);
            Value();
            foreach (var end in _loops.RunFound()) Add(end);
            foreach (var snapshot in _kept) world.Forget(snapshot);
            _cut |= _loops.Found;
            var over = _ends.Where(e => e.Status != CombatStatus.InProgress).MaxBy(e => e.Value);
            var going = _ends.Where(e => e.Status == CombatStatus.InProgress).OrderByDescending(e => e.Value).ToList();
            if (going.Count > keep) _cut = true;
            return new([.. over == null ? [] : new[] { over }, .. going.Take(keep)], _nodes, _cut);
        }

        /// <summary>
        /// Tries every action from the current state, once per state: the plays where the player is on this turn
        /// (<see cref="PimcBrain.Ordered"/>), the answers where a card prompt waits. At the turn's start the nodes are
        /// shared over the actions, each with an even share plus what the ones before it left, so every first action is
        /// looked at however many states the turn has; a prompt that one of them raises shares that action's nodes over
        /// its answers the same way. Below, a subtree spends its share depth first. A line ends with the combat, or where
        /// the player can play again after the turn's end: a prompt the end of the turn or the next turn's start raises
        /// is answered first, so the leaf reads a state the combat net knows. A move back to a state on the path with
        /// something gained is a cycle, which would spend every node on its own repeats: it goes to
        /// <see cref="LoopSearch"/> instead of down.
        /// </summary>
        /// <param name="shareTo">Where the state's share of the nodes ends, when its actions share it evenly; null where
        /// they are tried depth first, up to the limit of the share they are in.</param>
        private void Visit(CombatObservation obs, int? shareTo = null)
        {
            if (!_seen.Add((world.StateKey(obs), world.HiddenKey()))) return;
            ulong hash = world.Hash(obs);
            var here = world.Snapshot();
            // A prompt stands in the middle of an action: a cycle is a move between states where the player can play.
            bool plays = obs.Prompt == null;
            if (plays) _path.Add(new(obs, _actions.Count, here));
            bool moved = false;
            var todo = PimcBrain.Actions(world, obs, (shareTo ?? _limit) - _nodes, out bool left);
            _cut |= left;
            int from = _nodes;
            for (int i = 0; i < todo.Count; i++)
            {
                var action = todo[i];
                int? to = null;
                if (shareTo is { } end) _limit = (to = Math.Max(_nodes + 1, from + (int)((long)(end - from) * (i + 1) / todo.Count))).Value;
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
                if (status != CombatStatus.InProgress) Ends(status, after);
                else if (after.Prompt != null) Visit(after, to);
                else if (action.Kind == CombatActionKind.EndTurn || after.Round != _round) Ends(status, after);
                else if (!_loops.Intercept(_path, after, _actions, _hashes)) Visit(after);
                _actions.RemoveAt(_actions.Count - 1);
                _hashes.RemoveAt(_hashes.Count - 1);
            }
            if (plays) _path.RemoveAt(_path.Count - 1);
            // The search is done with this state: the world lets go of it, unless a cycle found below starts here.
            if (_loops.StartsAt(here)) _kept.Add(here);
            else world.Forget(here);
        }

        /// <summary>A line that ended the turn or the combat, as the world stood there, before the leaf has valued it.</summary>
        private sealed record Ended(LeafState State, IReadOnlyList<CombatAction> Actions, IReadOnlyList<ulong> Hashes, ulong Key)
        {
            public TurnEnd Valued(LeafOutcome outcome) => new(Actions, Hashes, State.Status, outcome.Value, Key, State.History, outcome.Win, outcome.Hp);
        }

        /// <summary>The key of a state at the next turn's start, by which a turn's ends and the beam's states are told
        /// apart; 0 once the combat is over.</summary>
        private ulong KeyOf(CombatStatus status, CombatObservation obs) =>
            status == CombatStatus.InProgress ? Mix(world.StateKey(obs), world.HiddenKey()) : 0;

        /// <summary>The turn ended here, or the combat did: what the leaf is asked about, with the line that led here.</summary>
        private Ended Reached(CombatStatus status, CombatObservation obs, IReadOnlyList<CombatAction> actions, IReadOnlyList<ulong> hashes, ulong key)
        {
            var record = new MoveRecord(history);
            if (status == CombatStatus.InProgress) record.Witness(obs);
            return new(new(status, obs, world.Run(), record.History), [.. actions], [.. hashes], key);
        }

        /// <summary>The search's line ended here. The search needs no end's value to go on, so the leaf is asked about
        /// all of a turn's ends together (<see cref="Value"/>); a state another line already ended at is not asked about again.</summary>
        private void Ends(CombatStatus status, CombatObservation obs)
        {
            ulong key = KeyOf(status, obs);
            if (status == CombatStatus.InProgress && !_keys.Add(key)) return;
            _ended.Add(Reached(status, obs, _actions, _hashes, key));
        }

        /// <summary>Asks the leaf about the ends the search found, in the order found.</summary>
        private void Value()
        {
            var outcomes = leaf.Outcomes([.. _ended.Select(e => e.State)]);
            for (int i = 0; i < _ended.Count; i++) _ends.Add(_ended[i].Valued(outcomes[i]));
            _ended.Clear();
        }

        /// <summary>A line's end valued at once: the cycles' cash-outs compare the values as they go.</summary>
        private TurnEnd End(CombatStatus status, CombatObservation obs, IReadOnlyList<CombatAction> actions, IReadOnlyList<ulong> hashes)
        {
            var ended = Reached(status, obs, actions, hashes, KeyOf(status, obs));
            return ended.Valued(leaf.Outcome(status, obs, ended.State.Run, ended.State.History));
        }

        private void Add(TurnEnd end)
        {
            if (end.Status == CombatStatus.InProgress && !_keys.Add(end.Key)) return;
            _ends.Add(end);
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
