using System.Numerics;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>How much search one decision may use (docs: harness/routing §4): worlds, and actions simulated per world.</summary>
/// <param name="NodesPerWorld">PIMC: the actions simulated in each world's search of the turn. The SL planner: in each
/// search of one turn from one state.</param>
/// <param name="Elite">How many times its budget an elite's decisions get (docs: pimc/budget §4, sl-planner/budget §1): PIMC's nodes per world,
/// the SL planner's beam.</param>
/// <param name="Boss">The same for a boss.</param>
/// <param name="Halving">Whether the first actions share each world's nodes by sequential halving instead of evenly
/// (<see cref="PimcBrain.Root"/>).</param>
/// <param name="Follow">Whether the rest of a turn that every world planned the same is played without searching again
/// (<see cref="TurnPlan"/>).</param>
/// <param name="Beam">The SL planner's states kept from one turn to the next (<see cref="SlPlanner"/>); 0 for PIMC,
/// which does not read it. The planner has one world, and ignores <paramref name="Worlds"/>.</param>
/// <param name="Replan">The SL planner's turns of an unproved losing plan played before planning again; 0 plays it to its end.</param>
/// <param name="LossPlans">The most unproved losing plans the SL planner makes in a row, that is in one combat
/// (<see cref="SlRoute"/>); the last is played to its end. 0: no limit.</param>
/// <param name="EasyBeam">The SL planner's states kept when its greedy dive already wins the combat; 0:
/// <paramref name="Beam"/>.</param>
public sealed record Budget(int Worlds = 8, int NodesPerWorld = 300, double Elite = 1, double Boss = 1, bool Halving = false, bool Follow = false,
    int Beam = 0, int Replan = 1, int LossPlans = 0, int EasyBeam = 0)
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

    /// <summary>The budget of a decision in this room (<see cref="CombatObservation.Room"/>), with its multiple applied: to
    /// the SL planner's beam, or to PIMC's nodes per world. Applied twice, it is the same.</summary>
    public Budget For(string? room)
    {
        double x = room switch { "Elite" => Elite, "Boss" => Boss, _ => 1.0 };
        int Scaled(int beam) => beam > 0 ? Math.Max(1, (int)Math.Round(beam * x)) : 0;
        return Beam > 0
            ? this with { Beam = Scaled(Beam), EasyBeam = Scaled(EasyBeam), Elite = 1, Boss = 1 }
            : this with { NodesPerWorld = (int)Math.Round(NodesPerWorld * x), Elite = 1, Boss = 1 };
    }

    /// <summary>As the champion's name shows it: 8x300 (beam 16x300 for the planner), then the multiples that are not 1,
    /// then halving and follow if on, and the planner's easy beam, replan when not 1 and loss plans when limited.</summary>
    public override string ToString() => FormattableString.Invariant(
        $"{(Beam > 0 ? $"beam {Beam}" : $"{Worlds}")}x{NodesPerWorld}{(Elite != 1 ? $" elite x{Elite}" : "")}{(Boss != 1 ? $" boss x{Boss}" : "")}{(Halving ? " halving" : "")}{(Follow ? " follow" : "")}{(Beam > 0 && EasyBeam > 0 ? $" easy {EasyBeam}" : "")}{(Beam > 0 && Replan != 1 ? $" replan {Replan}" : "")}{(Beam > 0 && LossPlans > 0 ? $" loss plans {LossPlans}" : "")}");
}

/// <summary>Searches every world from the decision's state: the best line after each of <paramref name="first"/>, in
/// that order, per world in world order, each first action with <paramref name="nodesPerFirst"/> nodes.</summary>
public delegate IReadOnlyList<IReadOnlyList<PlannedLine>> RootSearch(IReadOnlyList<CombatAction> first, int nodesPerFirst);

/// <param name="First">One of the legal actions.</param>
/// <param name="Plan">The rest of the turn after <paramref name="First"/>, in the first world (for narration and highlight).</param>
/// <param name="Candidates">Every first action, best first.</param>
/// <param name="PlanHashes">When every world planned the same <paramref name="Plan"/> and expected the same observation
/// before each of its steps, the hashes of those observations; otherwise null.</param>
/// <param name="Explored">Whether <paramref name="First"/> was drawn by self-play exploration and valued below the
/// search's pick (<see cref="Softmax.Explored"/>; Harness's <c>CombatRoute.Exploring</c>).</param>
/// <param name="Replanned">Whether this search replaced a step of a plan that the combat did not show
/// (<see cref="TurnPlan"/>). In SL that means the clone differs from the real combat.</param>
public sealed record CombatDecisionResult(CombatAction First, IReadOnlyList<CombatAction> Plan, IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<ulong>? PlanHashes = null, bool Explored = false, bool Replanned = false);

/// <summary>
/// The rest of the plan that a search made, played without searching again (docs: pimc/budget §2, sl-planner §4). A step is
/// played only while the combat shows the observation the plan expected before it. PIMC's plan is the rest of a turn
/// that every world planned the same: anything that comes to light (a draw, a random result) ends it, and the decision
/// is searched. The SL planner's plan is the rest of the combat in its one exact world, so it ends only with the
/// combat; a step that does not hold there means the world differs from the real combat. One per combat route.
/// </summary>
public sealed class TurnPlan
{
    private readonly Queue<(CombatAction Action, ulong Hash)> _steps = new();

    /// <summary>The plan's next step if it still holds here, else <paramref name="search"/>, whose plan is kept; marked
    /// replanned if a step was left that did not hold.</summary>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<CombatDecisionResult> search)
    {
        bool pending = _steps.TryDequeue(out var step);
        if (pending && step.Hash == ProtocolJson.Hash(observation) && legal.Contains(step.Action))
            return new(step.Action, _steps.Select(s => s.Action).ToList(), [], _steps.Select(s => s.Hash).ToList());
        _steps.Clear();
        var result = search();
        if (pending) result = result with { Replanned = true };
        if (result.PlanHashes is { } hashes)
            foreach (var s in result.Plan.Zip(hashes)) _steps.Enqueue(s);
        return result;
    }
}

/// <summary>Picks a combat action by searching the worlds it is given (docs: combat-brain §4).</summary>
public interface ICombatBrain
{
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>), for the leaves.</param>
    CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns>? history = null);
}

/// <summary>
/// Combat brain v0 (docs: pimc/search §1): PIMC over K worlds. In each world, a depth-first search over this turn's
/// action sequences, with ending the turn an option at every step; states are merged by their observation and the
/// world's hidden state (<see cref="ICombatWorld.HiddenKey"/>), so each transposition is expanded once, and two lines
/// that look the same but drew or rolled differently are not. A line ends when the turn ends (after the enemies'
/// turn) or the combat does, and is scored by the leaf (<see cref="ICombatLeaf"/>; <see cref="HandLeaf"/> by default). Each first action is scored
/// by its best line in each world, then averaged over the worlds; never the max across worlds, which is strategy fusion. Only the first action is played: the
/// caller asks again after it. In SL there is one exact world, so this is plain planning.
///
/// Each world's nodes go to the first actions evenly, so each gets a fair look, or by sequential halving
/// (<see cref="Root"/>); inside a subtree the search stops expanding when its share is spent, and what is left is scored
/// as ending the turn there (that option is tried first). A state cut short so is not kept for its transpositions: its
/// value is only a lower bound, and another first action reaching it searches it with its own share.
///
/// The worlds are independent, so a decision is three steps that can run apart: <see cref="Ordered"/> first actions, then
/// <see cref="SearchWorld"/> for each world wherever that world lives (this process, or a worker), then merging by
/// <see cref="Root"/>, which asks for the worlds' searches once per halving round. <see cref="Decide"/> runs them all here.
/// </summary>
public sealed class PimcBrain(ICombatLeaf? leaf = null) : ICombatBrain
{
    private readonly ICombatLeaf _leaf = leaf ?? new HandLeaf();

    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns>? history = null)
    {
        if (legal.Count == 1) return Only(legal[0]);
        budget = budget.For(observation.Room);
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
        new WorldSearch(world, round, _leaf, history ?? [], nodesPerFirst).Root(first);

    /// <summary>
    /// Shares each world's nodes over the first actions and merges the worlds' lines. Evenly: one search, each first
    /// action with an equal share. By sequential halving (docs: pimc/budget §3): ⌈log₂ m⌉ rounds over the m first
    /// actions; each round's part of the nodes is split evenly over the actions still in, each searched again from the
    /// root with all the nodes it has had so far (workers keep nothing between requests, so a round searches anew rather
    /// than resuming, at up to about 1.5 times the nodes), and then the better half by the mean over the same worlds
    /// stays. The pick is the better of the last two.
    /// </summary>
    public static CombatDecisionResult Root(IReadOnlyList<CombatAction> first, Budget budget, RootSearch search)
    {
        if (!budget.Halving) return Merge(first, search(first, Math.Max(1, budget.NodesPerWorld / first.Count)), new int[first.Count]);
        int rounds = BitOperations.Log2((uint)first.Count - 1) + 1;
        var lines = new List<PlannedLine[]>();
        var reached = new int[first.Count];
        var left = Enumerable.Range(0, first.Count).ToList();
        int nodes = 0;
        for (int round = 0; ; round++)
        {
            nodes += Math.Max(1, budget.NodesPerWorld / (rounds * left.Count));
            var found = search(left.Select(a => first[a]).ToList(), nodes);
            for (int w = 0; w < found.Count; w++)
            {
                if (w == lines.Count) lines.Add(new PlannedLine[first.Count]);
                for (int i = 0; i < left.Count; i++) lines[w][left[i]] = found[w][i];
            }
            foreach (int a in left) reached[a] = round;
            if (round == rounds - 1) break;
            left = left.OrderByDescending(a => lines.Average(l => l[a].Value)).Take((left.Count + 1) / 2).Order().ToList();
        }
        return Merge(first, lines, reached);
    }

    /// <summary>Scores each first action by its mean over the worlds; the plan is the best action's line in the first world.</summary>
    /// <param name="reached">Each first action's last halving round: the pick is the best of those that reached the
    /// last one, and the candidates are listed by round, then value.</param>
    private static CombatDecisionResult Merge(IReadOnlyList<CombatAction> first, IReadOnlyList<IReadOnlyList<PlannedLine>> worlds, IReadOnlyList<int> reached)
    {
        var candidates = first.Select((action, a) => Summarize(action.ToString(), worlds.Select(lines => lines[a]).ToList())).ToList();
        var order = Enumerable.Range(0, first.Count).OrderByDescending(a => reached[a]).ThenByDescending(a => candidates[a].Value).ToList();
        var line = worlds[0][order[0]];
        bool agreed = line.Hashes != null && worlds.All(lines =>
            lines[order[0]].Actions.SequenceEqual(line.Actions) && lines[order[0]].Hashes!.SequenceEqual(line.Hashes));
        return new(first[order[0]], line.Actions.Skip(1).ToList(), order.Select(a => candidates[a]).ToList(), agreed ? line.Hashes!.Skip(1).ToList() : null);
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
        return new(action, mean, ci, values.Length, values.Select(v => Math.Round(v, 4)).ToList(), lines.Select(l => l.Nodes).ToList());
    }

    private sealed class WorldSearch(ICombatWorld world, int round, ICombatLeaf leaf, IReadOnlyList<EnemyTurns> history, int nodesPerFirst)
    {
        private readonly Dictionary<(ulong Obs, ulong Hidden), PlannedLine> _memo = [];
        private int _nodes;
        /// <summary>States cut short by the budget so far: a state whose subtree has none is searched in full.</summary>
        private int _cuts;

        public PlannedLine[] Root(IReadOnlyList<CombatAction> first)
        {
            var root = world.Snapshot();
            ulong rootHash = ProtocolJson.Hash(world.Observe());
            var lines = new PlannedLine[first.Count];
            for (int a = 0; a < first.Count; a++)
            {
                if (a > 0) world.Restore(root);
                _nodes = 0;
                var after = After(first[a]);
                lines[a] = new(after.Value, [first[a], .. after.Actions], _nodes, [rootHash, .. after.Hashes!]);
            }
            return lines;
        }

        /// <summary>Plays <paramref name="action"/> and returns the best line from there.</summary>
        private PlannedLine After(CombatAction action)
        {
            _nodes++;
            world.Execute(action);
            var status = world.Status;
            var obs = world.Observe();
            if (status != CombatStatus.InProgress || action.Kind == CombatActionKind.EndTurn || obs.Round != round)
                return new(leaf.Value(status, obs, history), [], 0, []);
            return Best(obs);
        }

        /// <summary>The best line from the current state, where the player is still on this turn.</summary>
        private PlannedLine Best(CombatObservation obs)
        {
            ulong hash = ProtocolJson.Hash(obs);
            var key = (hash, world.HiddenKey());
            if (_memo.TryGetValue(key, out var known)) return known;
            var here = world.Snapshot();
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
                var after = After(action);
                if (after.Value > best.Value) best = new(after.Value, [action, .. after.Actions], 0, [hash, .. after.Hashes!]);
            }
            if (_cuts == cuts) _memo[key] = best;
            return best;
        }
    }
}
