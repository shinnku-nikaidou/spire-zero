using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>How much search one decision may use (docs: harness/routing §4): worlds, and actions simulated per world.</summary>
/// <param name="NodesPerWorld">PIMC: the actions simulated in each world's search of the turn. The SL planner: in each
/// search of one turn from one state.</param>
/// <param name="Beam">The SL planner's states kept from one turn to the next (<see cref="SlPlanner"/>); 0 for PIMC,
/// which does not read it. The planner has one world, and ignores <paramref name="Worlds"/>.</param>
/// <param name="Replan">The SL planner's turns of an unproved losing plan played before planning again; 0 plays it to its end.</param>
/// <param name="LossPlans">The most unproved losing plans the SL planner makes in a row, that is in one combat
/// (<see cref="SlRoute"/>); the last is played to its end. 0: no limit.</param>
/// <param name="EasyBeam">The SL planner's states kept when its greedy dive already wins the combat; 0:
/// <paramref name="Beam"/>.</param>
public sealed record Budget(int Worlds = 8, int NodesPerWorld = 300, int Beam = 0, int Replan = 1, int LossPlans = 0, int EasyBeam = 0)
{
    /// <summary>The planner's budget at an effort level (docs: sl-planner/budget §1): beam, nodes per state and turn, the
    /// easy beam, the turns of an unproved losing plan played before planning again (0: to its end), and the losing
    /// plans in a row before the last is played to its end.</summary>
    public static Budget Effort(string level) => level switch
    {
        "low" => new(NodesPerWorld: 300, Beam: 8, EasyBeam: 2, LossPlans: 3),
        "medium" => new(NodesPerWorld: 300, Beam: 8, EasyBeam: 4, LossPlans: 3),
        "high" => new(NodesPerWorld: 300, Beam: 8, Replan: 1),
        "max" => new(NodesPerWorld: 300, Beam: 16, Replan: 1),
        _ => throw new ArgumentException($"no effort {level}: low, medium, high or max"),
    };

    /// <summary>The effort levels, cheapest first.</summary>
    public static readonly IReadOnlyList<string> Efforts = ["low", "medium", "high", "max"];

    /// <summary>As the champion's name shows it: 8x300 (beam 16x300 for the planner), then the planner's easy beam,
    /// replan when not 1 and loss plans when limited.</summary>
    public override string ToString() => FormattableString.Invariant(
        $"{(Beam > 0 ? $"beam {Beam}" : $"{Worlds}")}x{NodesPerWorld}{(Beam > 0 && EasyBeam > 0 ? $" easy {EasyBeam}" : "")}{(Beam > 0 && Replan != 1 ? $" replan {Replan}" : "")}{(Beam > 0 && LossPlans > 0 ? $" loss plans {LossPlans}" : "")}");
}

/// <summary>Searches every world from the decision's state: the best line after each of <paramref name="first"/>, in
/// that order, per world in world order, each first action with <paramref name="nodesPerFirst"/> nodes.</summary>
public delegate IReadOnlyList<IReadOnlyList<PlannedLine>> RootSearch(IReadOnlyList<CombatAction> first, int nodesPerFirst);

/// <param name="First">One of the legal actions, with <see cref="CombatAction.Pick"/> if search chose its prompt's answer.</param>
/// <param name="Plan">The rest of the turn after <paramref name="First"/>, in the first world (for narration and highlight).</param>
/// <param name="Candidates">Every first action, best first.</param>
/// <param name="PlanHashes">When every world planned the same <paramref name="Plan"/> and expected the same observation
/// before each of its steps, the hashes of those observations; otherwise null.</param>
/// <param name="Replanned">Whether this search replaced a step of a plan that the combat did not show
/// (<see cref="TurnPlan"/>). In SL that means the clone differs from the real combat.</param>
/// <param name="Repeats">Whether the line repeats a cycle (<see cref="LoopSearch"/>): PIMC follows such a plan, since
/// searching again before each of hundreds of repeats would only find it again.</param>
public sealed record CombatDecisionResult(CombatAction First, IReadOnlyList<CombatAction> Plan, IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<ulong>? PlanHashes = null, bool Replanned = false, bool Repeats = false);

/// <summary>
/// The rest of the plan that a search made, played without searching again (docs: pimc/budget §2, sl-planner §4). A step is
/// played only while the combat shows the observation the plan expected before it. PIMC's plan is the rest of a turn
/// that every world planned the same: anything that comes to light (a draw, a random result) ends it, and the decision
/// is searched. The SL planner's plan is the rest of the combat in its one exact world, so it ends only with the
/// combat; a step that does not hold there means the world differs from the real combat. One per combat route.
/// </summary>
/// <param name="all">Whether to keep every plan a search makes (the SL planner), or only one that repeats a cycle
/// (<see cref="CombatDecisionResult.Repeats"/>; PIMC).</param>
public sealed class TurnPlan(bool all = true)
{
    private readonly Queue<(CombatAction Action, ulong Hash)> _steps = new();

    /// <summary>The plan's next step if it still holds here, else <paramref name="search"/>, whose plan is kept; marked
    /// replanned if a step was left that did not hold.</summary>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<CombatDecisionResult> search)
    {
        bool pending = _steps.TryDequeue(out var step);
        if (pending && step.Hash == ProtocolJson.Hash(observation) && legal.Contains(step.Action.Plain))
            return new(step.Action, _steps.Select(s => s.Action).ToList(), [], _steps.Select(s => s.Hash).ToList());
        _steps.Clear();
        var result = search();
        if (pending) result = result with { Replanned = true };
        if ((all || result.Repeats) && result.PlanHashes is { } hashes)
            foreach (var s in result.Plan.Zip(hashes)) _steps.Enqueue(s);
        return result;
    }
}

/// <summary>
/// Combat brain v0 (docs: pimc/search §1): PIMC over K worlds. In each world, a depth-first search over this turn's
/// action sequences, with ending the turn an option at every step; states are merged by their observation and the
/// world's hidden state (<see cref="ICombatWorld.HiddenKey"/>), so each transposition is expanded once, and two lines
/// that look the same but drew or rolled differently are not. A line ends when the turn ends (after the enemies'
/// turn) or the combat does, and is scored by the leaf (<see cref="ICombatLeaf"/>, the combat net). Each first action is scored
/// by its best line in each world, then averaged over the worlds; never the max across worlds, which is strategy fusion. Only the first action is played: the
/// caller asks again after it. In SL there is one exact world, so this is plain planning.
///
/// Each world's nodes go to the first actions evenly, so each gets a fair look (<see cref="Root"/>); inside a subtree
/// the search stops expanding when its share is spent, and what is left is scored
/// as ending the turn there (that option is tried first). A state cut short so is not kept for its transpositions: its
/// value is only a lower bound, and another first action reaching it searches it with its own share.
///
/// The worlds are independent, so a decision is three steps that can run apart: <see cref="Ordered"/> first actions, then
/// <see cref="SearchWorld"/> for each world wherever that world lives (this process, or a worker), then merging by
/// <see cref="Root"/>. <see cref="Decide"/> runs them all here.
/// </summary>
public sealed class PimcBrain(ICombatLeaf leaf)
{
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>), for the leaves.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns>? history = null)
    {
        if (legal.Count == 1) return Only(legal[0]);
        var live = worlds.Worlds(budget.Worlds, seed);
        var roots = live.Select(w => w.Snapshot()).ToList();
        return Root(Ordered(legal), budget, (first, nodes) => live.Select((w, k) =>
        {
            w.Restore(roots[k]);
            return SearchWorld(w, observation.Round, first, nodes, history);
        }).ToList());
    }

    /// <summary>The answer when there is nothing to choose: no search, so no candidates.</summary>
    public static CombatDecisionResult Only(CombatAction action) => new(action, [], []);

    /// <summary>One world's part of a decision: the best line after each of <paramref name="first"/>, in that order.</summary>
    /// <param name="history">The combat's record of enemy moves up to the decision, which every leaf of this turn follows.</param>
    public IReadOnlyList<PlannedLine> SearchWorld(ICombatWorld world, int round, IReadOnlyList<CombatAction> first, int nodesPerFirst,
        IReadOnlyList<EnemyTurns>? history = null) =>
        new WorldSearch(world, round, leaf, history ?? [], nodesPerFirst).Root(first);

    /// <summary>Shares each world's nodes evenly over the first actions and merges the worlds' lines.</summary>
    public static CombatDecisionResult Root(IReadOnlyList<CombatAction> first, Budget budget, RootSearch search) =>
        Merge(first, search(first, Math.Max(1, budget.NodesPerWorld / first.Count)));

    /// <summary>Scores each first action by its mean over the worlds; the plan is the best action's line in the first
    /// world. The candidates are listed best first.</summary>
    private static CombatDecisionResult Merge(IReadOnlyList<CombatAction> first, IReadOnlyList<IReadOnlyList<PlannedLine>> worlds)
    {
        var candidates = first.Select((action, a) => Summarize(action.ToString(), worlds.Select(lines => lines[a]).ToList())).ToList();
        var order = Enumerable.Range(0, first.Count).OrderByDescending(a => candidates[a].Value).ToList();
        var line = worlds[0][order[0]];
        bool agreed = line.Hashes != null && worlds.All(lines =>
            lines[order[0]].Actions.SequenceEqual(line.Actions) && lines[order[0]].Hashes!.SequenceEqual(line.Hashes));
        // A line may start with the first action and an answer to its prompt, which the first world's line gives.
        var pick = line.Actions.Count > 0 ? line.Actions[0] : first[order[0]];
        return new(pick, line.Actions.Skip(1).ToList(), order.Select(a => candidates[a]).ToList(), agreed ? line.Hashes!.Skip(1).ToList() : null, Repeats: line.Repeats);
    }

    /// <summary>Search order: ending the turn first (the fallback a spent budget leaves), then cards in hand order, then potions.</summary>
    public static List<CombatAction> Ordered(IReadOnlyList<CombatAction> legal) =>
        legal.OrderBy(a => a.Kind switch { CombatActionKind.EndTurn => 0, CombatActionKind.PlayCard => 1, _ => 2 }).ToList();

    /// <param name="lines">The action's best line in each world.</param>
    private static Candidate Summarize(string action, IReadOnlyList<PlannedLine> lines)
    {
        var values = lines.Select(l => l.Value).ToArray();
        double mean = values.Average();
        double ci = values.Length < 2 ? 0 : 1.96 * Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1) / values.Length);
        return new(action, mean, ci, values.Length, values.Select(v => Math.Round(v, 4)).ToList());
    }

    private sealed class WorldSearch(ICombatWorld world, int round, ICombatLeaf leaf, IReadOnlyList<EnemyTurns> history, int nodesPerFirst)
    {
        private readonly Dictionary<(ulong Obs, ulong Hidden), PlannedLine> _memo = [];
        private readonly List<LoopPoint> _path = [];
        private readonly List<CombatAction> _actions = [];
        private readonly List<ulong> _hashes = [];
        private readonly LoopSearch _loops = new(world, round, (status, obs, actions, hashes) =>
            new([.. actions], [.. hashes], status, leaf.Value(status, obs, history), 0, 0, history));
        private WorldSnapshot _root = new([]);
        private ulong _rootHash;
        private PromptSeen? _firstPrompt;
        private int _nodes;
        /// <summary>States cut short by the budget so far: a state whose subtree has none is searched in full.</summary>
        private int _cuts;

        public PlannedLine[] Root(IReadOnlyList<CombatAction> first)
        {
            _root = world.Snapshot();
            var obs = world.Observe();
            _rootHash = ProtocolJson.Hash(obs);
            _path.Add(new(obs, 0, _root));
            var lines = new PlannedLine[first.Count];
            for (int a = 0; a < first.Count; a++)
            {
                if (a > 0) world.Restore(_root);
                lines[a] = First(first[a]);
                // Another answer to the first action's prompt gets a line of its own only if it opens a cycle (docs: pimc/search §6).
                if (first[a].Kind == CombatActionKind.EndTurn || _firstPrompt is not { Options: > 1 } prompt) continue;
                for (int pick = 0; pick < prompt.Options; pick++)
                {
                    if (pick == prompt.Answer) continue;
                    var picked = first[a] with { Pick = pick };
                    world.Restore(_root);
                    world.Execute(picked);
                    var after = world.Observe();
                    if (world.Status != CombatStatus.InProgress || after.Round != round || !_loops.OpensCycle(after)) continue;
                    world.Restore(_root);
                    var line = First(picked);
                    if (line.Value > lines[a].Value) lines[a] = line;
                }
            }
            return lines;
        }

        /// <summary>The best line that starts with <paramref name="action"/>: the search's, then the cycles it found.</summary>
        private PlannedLine First(CombatAction action)
        {
            _nodes = 0;
            var after = After(action, _rootHash);
            var line = new PlannedLine(after.Value, [action, .. after.Actions], _nodes, [_rootHash, .. after.Hashes!]);
            foreach (var end in _loops.RunFound())
                if (end.Value > line.Value) line = new(end.Value, end.Actions, _nodes, end.Hashes, Repeats: true);
            if (!double.IsNegativeInfinity(line.Value)) return line;
            // The move was a cycle at once and no budget was left for it: what is left is ending the turn.
            world.Restore(_root);
            world.Execute(action);
            var obs = world.Observe();
            if (world.Status == CombatStatus.InProgress && obs.Round == round)
            {
                world.Execute(CombatAction.EndTurn);
                return new(leaf.Value(world.Status, world.Observe(), history), [action, CombatAction.EndTurn], _nodes, [_rootHash, ProtocolJson.Hash(obs)]);
            }
            return new(leaf.Value(world.Status, obs, history), [action], _nodes, [_rootHash]);
        }

        /// <summary>Plays <paramref name="action"/> and returns the best line from there.</summary>
        private PlannedLine After(CombatAction action, ulong hash)
        {
            _nodes++;
            world.Execute(action);
            if (_actions.Count == 0) _firstPrompt = world.LastPrompt;
            _actions.Add(action);
            _hashes.Add(hash);
            var line = Reached(action);
            _actions.RemoveAt(_actions.Count - 1);
            _hashes.RemoveAt(_hashes.Count - 1);
            return line;
        }

        private PlannedLine Reached(CombatAction action)
        {
            var status = world.Status;
            var obs = world.Observe();
            if (status != CombatStatus.InProgress || action.Kind == CombatActionKind.EndTurn || obs.Round != round)
                return new(leaf.Value(status, obs, history), [], 0, []);
            return _loops.Intercept(_path, obs, _actions, _hashes) ? new(double.NegativeInfinity, [], 0, []) : Best(obs);
        }

        /// <summary>The best line from the current state, where the player is still on this turn.</summary>
        private PlannedLine Best(CombatObservation obs)
        {
            ulong hash = ProtocolJson.Hash(obs);
            var key = (hash, world.HiddenKey());
            if (_memo.TryGetValue(key, out var known)) return known;
            var here = world.Snapshot();
            _path.Add(new(obs, _actions.Count, here));
            var best = new PlannedLine(double.NegativeInfinity, [], 0, []);
            bool moved = false;
            int cuts = _cuts;
            foreach (var action in Ordered(world.Legal()))
            {
                if (moved && _nodes >= nodesPerFirst)
                {
                    _cuts++;
                    break;
                }
                if (moved) world.Restore(here);
                moved = true;
                var after = After(action, hash);
                if (after.Value > best.Value) best = new(after.Value, [action, .. after.Actions], 0, [hash, .. after.Hashes!]);
            }
            _path.RemoveAt(_path.Count - 1);
            if (_cuts == cuts) _memo[key] = best;
            return best;
        }
    }
}
