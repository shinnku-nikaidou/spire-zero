using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="PimcBrain"/> with its worlds searched in a <see cref="WorkerPool"/>, one request per world. NoSL only:
/// each world is rebuilt in its worker from the observation, so nothing of the true state leaves the host. With the
/// same seeds it gives the same answer as <see cref="PimcBrain.Decide"/> over in-process worlds. The workers use the
/// default leaf weights.
/// </summary>
public sealed class PooledPimc(WorkerPool pool)
{
    private readonly List<string> _gameErrors = [];

    /// <param name="buildSeed">Seeds the combat built from the observation (docs: simulator §3).</param>
    /// <param name="seed">Draws the worlds.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, string buildSeed, Budget budget, ulong seed)
    {
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        var first = PimcBrain.Ordered(legal);
        var requests = Enumerable.Range(0, budget.Worlds)
            .Select(k => new PimcWorldRequest(k, observation, first, buildSeed, seed, k, budget.Worlds, budget.NodesPerWorld))
            .ToList();
        var replies = pool.Map(requests);
        foreach (var r in replies) _gameErrors.AddRange(r.GameErrors);
        if (replies.FirstOrDefault(r => r.Error != null) is { } failed) throw new InvalidOperationException($"world {failed.Id}: {failed.Error}");
        return PimcBrain.Merge(first, replies.Select(r => r.Lines!).ToList());
    }

    /// <summary>The game's Log.Error lines in the workers since the last call.</summary>
    public List<string> TakeGameErrors()
    {
        var errors = _gameErrors.ToList();
        _gameErrors.Clear();
        return errors;
    }
}
