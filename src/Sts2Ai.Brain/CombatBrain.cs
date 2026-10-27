using System.Numerics;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>How much search one decision may use (docs: harness §5): worlds, and actions simulated per world.</summary>
/// <param name="Elite">How many times the nodes per world an elite's decisions get (docs: combat-brain §5).</param>
/// <param name="Boss">The same for a boss.</param>
/// <param name="Halving">Whether the first actions share each world's nodes by sequential halving instead of evenly
/// (<see cref="PimcBrain.Root"/>).</param>
/// <param name="Follow">Whether the rest of a turn that every world planned the same is played without searching again
/// (<see cref="TurnPlan"/>).</param>
public sealed record Budget(int Worlds = 8, int NodesPerWorld = 300, double Elite = 1, double Boss = 1, bool Halving = false, bool Follow = false)
{
    /// <summary>The budget of a decision in this room (<see cref="CombatObservation.Room"/>), with its multiple applied.</summary>
    public Budget For(string? room) => this with
    {
        NodesPerWorld = (int)Math.Round(NodesPerWorld * (room switch { "Elite" => Elite, "Boss" => Boss, _ => 1.0 })),
        Elite = 1,
        Boss = 1,
    };

    /// <summary>As the champion's name shows it: 8x300, then the multiples that are not 1, then halving and follow if on.</summary>
    public override string ToString() => FormattableString.Invariant(
        $"{Worlds}x{NodesPerWorld}{(Elite != 1 ? $" elite x{Elite}" : "")}{(Boss != 1 ? $" boss x{Boss}" : "")}{(Halving ? " halving" : "")}{(Follow ? " follow" : "")}");
}

/// <summary>Searches every world from the decision's state: the best line after each of <paramref name="first"/>, in
/// that order, per world in world order, each first action with <paramref name="nodesPerFirst"/> nodes.</summary>
public delegate IReadOnlyList<IReadOnlyList<PlannedLine>> RootSearch(IReadOnlyList<CombatAction> first, int nodesPerFirst);

/// <param name="First">One of the legal actions.</param>
/// <param name="Plan">The rest of the turn after <paramref name="First"/>, in the first world (for narration and highlight).</param>
/// <param name="Candidates">Every first action, best first.</param>
/// <param name="PlanHashes">When every world planned the same <paramref name="Plan"/> and expected the same observation
/// before each of its steps, the hashes of those observations; otherwise null.</param>
public sealed record CombatDecisionResult(CombatAction First, IReadOnlyList<CombatAction> Plan, IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<ulong>? PlanHashes = null);

/// <summary>
/// The rest of the turn that a search planned, played without searching again (docs: combat-brain §5). A step is
/// played only while the combat shows the observation that every world expected before it: then nothing came to light
/// that the worlds disagreed on, and each world's best line from here is still the plan. Anything else (a draw, a
/// random result, a plan that differed between worlds) ends it, and the decision is searched. One per combat route.
/// </summary>
public sealed class TurnPlan
{
    private readonly Queue<(CombatAction Action, ulong Hash)> _steps = new();

    /// <summary>The plan's next step if it still holds here, else <paramref name="search"/>, whose plan is kept.</summary>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<CombatDecisionResult> search)
    {
        if (_steps.TryDequeue(out var step) && step.Hash == ProtocolJson.Hash(observation) && legal.Contains(step.Action))
            return new(step.Action, _steps.Select(s => s.Action).ToList(), [], _steps.Select(s => s.Hash).ToList());
        _steps.Clear();
        var result = search();
        if (result.PlanHashes is { } hashes)
            foreach (var s in result.Plan.Zip(hashes)) _steps.Enqueue(s);
        return result;
    }
}

/// <summary>Picks a combat action by searching the worlds it is given (docs: combat-brain §6).</summary>
public interface ICombatBrain
{
    CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed);
}

/// <summary>
/// Combat brain v0 (docs: combat-brain §2): PIMC over K worlds. In each world, a depth-first search over this turn's
/// action sequences, with ending the turn an option at every step; states are merged by their observation, so each
/// transposition is expanded once. A line ends when the turn ends (after the enemies' turn) or the combat does, and
/// is scored by <see cref="LeafValue"/>. Each first action is scored by its best line in each world, then averaged
/// over the worlds; never the max across worlds, which is strategy fusion. Only the first action is played: the
/// caller asks again after it. In SL there is one exact world, so this is plain planning.
///
/// Each world's nodes go to the first actions evenly, so each gets a fair look, or by sequential halving
/// (<see cref="Root"/>); inside a subtree the search stops expanding when its share is spent, and what is left is scored
/// as ending the turn there (that option is tried first).
///
/// The worlds are independent, so a decision is three steps that can run apart: <see cref="Ordered"/> first actions, then
/// <see cref="SearchWorld"/> for each world wherever that world lives (this process, or a worker), then merging by
/// <see cref="Root"/>, which asks for the worlds' searches once per halving round. <see cref="Decide"/> runs them all here.
/// </summary>
public sealed class PimcBrain(LeafWeights? weights = null) : ICombatBrain
{
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed)
    {
        if (legal.Count == 1) return Only(legal[0]);
        budget = budget.For(observation.Room);
        var live = worlds.Worlds(budget.Worlds, seed);
        var roots = live.Select(w => w.Snapshot()).ToList();
        return Root(Ordered(legal), budget, (first, nodes) => live.Select((w, k) =>
        {
            w.Restore(roots[k]);
            return SearchWorld(w, observation.Round, first, nodes);
        }).ToList());
    }

    /// <summary>The answer when there is nothing to choose: no search, so no candidates.</summary>
    public static CombatDecisionResult Only(CombatAction action) => new(action, [], []);

    /// <summary>One world's part of a decision: the best line after each of <paramref name="first"/>, in that order.</summary>
    public IReadOnlyList<PlannedLine> SearchWorld(ICombatWorld world, int round, IReadOnlyList<CombatAction> first, int nodesPerFirst) =>
        new WorldSearch(world, round, weights, nodesPerFirst).Root(first);

    /// <summary>
    /// Shares each world's nodes over the first actions and merges the worlds' lines. Evenly: one search, each first
    /// action with an equal share. By sequential halving (docs: combat-brain §5): ⌈log₂ m⌉ rounds over the m first
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

    private sealed class WorldSearch(ICombatWorld world, int round, LeafWeights? weights, int nodesPerFirst)
    {
        private readonly Dictionary<ulong, PlannedLine> _memo = [];
        private int _nodes;

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
                return new(LeafValue.Of(status, obs, weights), [], 0, []);
            return Best(obs);
        }

        /// <summary>The best line from the current state, where the player is still on this turn.</summary>
        private PlannedLine Best(CombatObservation obs)
        {
            ulong key = ProtocolJson.Hash(obs);
            if (_memo.TryGetValue(key, out var known)) return known;
            var here = world.Snapshot();
            var best = new PlannedLine(double.NegativeInfinity, [], 0, []);
            bool moved = false;
            foreach (var action in Ordered(world.Legal()))
            {
                if (moved && _nodes >= nodesPerFirst) break;
                if (moved) world.Restore(here);
                moved = true;
                var after = After(action);
                if (after.Value > best.Value) best = new(after.Value, [action, .. after.Actions], 0, [key, .. after.Hashes!]);
            }
            _memo[key] = best;
            return best;
        }
    }
}
