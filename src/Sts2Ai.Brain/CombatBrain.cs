using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>How much search one decision may use (docs: harness §5): worlds, and actions simulated per world.</summary>
public sealed record Budget(int Worlds = 8, int NodesPerWorld = 300);

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
/// </summary>
public sealed class PimcBrain(LeafWeights? weights = null) : ICombatBrain
{
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed)
    {
        if (legal.Count == 1) return new(legal[0], [], [new(legal[0].ToString(), double.NaN, 0, 0)]);
        var first = Ordered(legal);
        var sampled = worlds.Worlds(budget.Worlds, seed);
        var values = first.Select(_ => new double[sampled.Count]).ToArray();
        Line[] firstWorld = [];
        for (int k = 0; k < sampled.Count; k++)
        {
            var lines = new WorldSearch(sampled[k], observation.Round, weights, Math.Max(1, budget.NodesPerWorld / first.Count)).Root(first);
            for (int a = 0; a < first.Count; a++) values[a][k] = lines[a].Value;
            if (k == 0) firstWorld = lines;
        }
        var candidates = first.Select((action, a) => Summarize(action.ToString(), values[a])).ToList();
        int best = Enumerable.Range(0, first.Count).MaxBy(a => candidates[a].Value);
        return new(first[best], firstWorld[best].Actions.Skip(1).ToList(), candidates.OrderByDescending(c => c.Value).ToList());
    }

    /// <summary>Ending the turn first (the fallback a spent budget leaves), then cards in hand order, then potions.</summary>
    private static List<CombatAction> Ordered(IReadOnlyList<CombatAction> legal) =>
        legal.OrderBy(a => a.Kind switch { CombatActionKind.EndTurn => 0, CombatActionKind.PlayCard => 1, _ => 2 }).ToList();

    private static Candidate Summarize(string action, double[] values)
    {
        double mean = values.Average();
        double ci = values.Length < 2 ? 0 : 1.96 * Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1) / values.Length);
        return new(action, mean, ci, values.Length);
    }

    private readonly record struct Line(double Value, IReadOnlyList<CombatAction> Actions);

    private sealed class WorldSearch(ICombatWorld world, int round, LeafWeights? weights, int nodesPerFirst)
    {
        private readonly Dictionary<ulong, Line> _memo = [];
        private int _nodes;

        public Line[] Root(IReadOnlyList<CombatAction> first)
        {
            var root = world.Snapshot();
            var lines = new Line[first.Count];
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
        private Line After(CombatAction action)
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
        private Line Best(CombatObservation obs)
        {
            ulong key = ProtocolJson.Hash(obs);
            if (_memo.TryGetValue(key, out var known)) return known;
            var here = world.Snapshot();
            var best = new Line(double.NegativeInfinity, []);
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
