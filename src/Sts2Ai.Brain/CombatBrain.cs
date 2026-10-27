using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>How much search one decision may use (docs: harness §5): worlds, and actions simulated per world.</summary>
/// <param name="Elite">How many times the nodes per world an elite's decisions get (docs: combat-brain §5).</param>
/// <param name="Boss">The same for a boss.</param>
public sealed record Budget(int Worlds = 8, int NodesPerWorld = 300, double Elite = 1, double Boss = 1)
{
    /// <summary>The budget of a decision in this room (<see cref="CombatObservation.Room"/>), with its multiple applied.</summary>
    public Budget For(string? room) =>
        new(Worlds, (int)Math.Round(NodesPerWorld * (room switch { "Elite" => Elite, "Boss" => Boss, _ => 1.0 })));

    /// <summary>As the champion's name shows it: 8x300, then the multiples that are not 1.</summary>
    public override string ToString() => FormattableString.Invariant(
        $"{Worlds}x{NodesPerWorld}{(Elite != 1 ? $" elite x{Elite}" : "")}{(Boss != 1 ? $" boss x{Boss}" : "")}");
}

/// <param name="First">One of the legal actions.</param>
/// <param name="Plan">The rest of the turn after <paramref name="First"/>, in the first world (for narration and highlight).</param>
/// <param name="Candidates">Every first action, best first.</param>
public sealed record CombatDecisionResult(CombatAction First, IReadOnlyList<CombatAction> Plan, IReadOnlyList<Candidate> Candidates);

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
/// The budget is split evenly over the first actions, so each gets a fair look; inside a subtree the search stops
/// expanding when its share is spent, and what is left is scored as ending the turn there (that option is tried first).
///
/// The worlds are independent, so a decision is three steps that can run apart: <see cref="Ordered"/> first actions, then
/// <see cref="SearchWorld"/> for each world wherever that world lives (this process, or a worker), then
/// <see cref="Merge"/>. <see cref="Decide"/> runs them all here.
/// </summary>
public sealed class PimcBrain(LeafWeights? weights = null) : ICombatBrain
{
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed)
    {
        if (legal.Count == 1) return Only(legal[0]);
        budget = budget.For(observation.Room);
        var first = Ordered(legal);
        return Merge(first, worlds.Worlds(budget.Worlds, seed).Select(w => SearchWorld(w, observation.Round, first, budget.NodesPerWorld)).ToList());
    }

    /// <summary>The answer when there is nothing to choose: no search, so no candidates.</summary>
    public static CombatDecisionResult Only(CombatAction action) => new(action, [], []);

    /// <summary>One world's part of a decision: the best line after each of <paramref name="first"/>, in that order.</summary>
    public IReadOnlyList<PlannedLine> SearchWorld(ICombatWorld world, int round, IReadOnlyList<CombatAction> first, int nodesPerWorld) =>
        new WorldSearch(world, round, weights, Math.Max(1, nodesPerWorld / first.Count)).Root(first);

    /// <summary>Scores each first action by its mean over the worlds; the plan is the best action's line in the first world.</summary>
    public static CombatDecisionResult Merge(IReadOnlyList<CombatAction> first, IReadOnlyList<IReadOnlyList<PlannedLine>> worlds)
    {
        var candidates = first.Select((action, a) => Summarize(action.ToString(), worlds.Select(lines => lines[a].Value).ToArray())).ToList();
        int best = Enumerable.Range(0, first.Count).MaxBy(a => candidates[a].Value);
        return new(first[best], worlds[0][best].Actions.Skip(1).ToList(), candidates.OrderByDescending(c => c.Value).ToList());
    }

    /// <summary>Search order: ending the turn first (the fallback a spent budget leaves), then cards in hand order, then potions.</summary>
    public static List<CombatAction> Ordered(IReadOnlyList<CombatAction> legal) =>
        legal.OrderBy(a => a.Kind switch { CombatActionKind.EndTurn => 0, CombatActionKind.PlayCard => 1, _ => 2 }).ToList();

    private static Candidate Summarize(string action, double[] values)
    {
        double mean = values.Average();
        double ci = values.Length < 2 ? 0 : 1.96 * Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1) / values.Length);
        return new(action, mean, ci, values.Length);
    }

    private sealed class WorldSearch(ICombatWorld world, int round, LeafWeights? weights, int nodesPerFirst)
    {
        private readonly Dictionary<ulong, PlannedLine> _memo = [];
        private int _nodes;

        public PlannedLine[] Root(IReadOnlyList<CombatAction> first)
        {
            var root = world.Snapshot();
            var lines = new PlannedLine[first.Count];
            for (int a = 0; a < first.Count; a++)
            {
                if (a > 0) world.Restore(root);
                _nodes = 0;
                var after = After(first[a]);
                lines[a] = new(after.Value, [first[a], .. after.Actions]);
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
                return new(LeafValue.Of(status, obs, weights), []);
            return Best(obs);
        }

        /// <summary>The best line from the current state, where the player is still on this turn.</summary>
        private PlannedLine Best(CombatObservation obs)
        {
            ulong key = ProtocolJson.Hash(obs);
            if (_memo.TryGetValue(key, out var known)) return known;
            var here = world.Snapshot();
            var best = new PlannedLine(double.NegativeInfinity, []);
            bool moved = false;
            foreach (var action in Ordered(world.Legal()))
            {
                if (moved && _nodes >= nodesPerFirst) break;
                if (moved) world.Restore(here);
                moved = true;
                var after = After(action);
                if (after.Value > best.Value) best = new(after.Value, [action, .. after.Actions]);
            }
            _memo[key] = best;
            return best;
        }
    }
}
