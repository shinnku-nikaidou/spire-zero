using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="PimcBrain"/> with its worlds searched in a <see cref="WorkerPool"/>. NoSL: one request per world, each
/// world rebuilt in its worker from the observation, so nothing of the true state leaves the host; with the same seeds
/// it gives the same answer as <see cref="PimcBrain.Decide"/> over in-process worlds. SL: the one exact world is the
/// node's clone, searched in one request per first action so the workers share it; each request keeps its own memo,
/// where a search in one process shares it across first actions. The workers search with the leaf weights they were
/// started with (<c>serve --leaf</c>).
/// </summary>
public sealed class PooledPimc(WorkerPool pool)
{
    private readonly List<string> _gameErrors = [];

    /// <param name="buildSeed">Seeds the combat built from the observation (docs: simulator §3).</param>
    /// <param name="seed">Draws the worlds.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, string buildSeed, Budget budget, ulong seed)
    {
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        budget = budget.For(observation.Room);
        var first = PimcBrain.Ordered(legal);
        var replies = Map(Enumerable.Range(0, budget.Worlds)
            .Select(k => (WorkerRequest)new PimcWorldRequest(k, observation, first, buildSeed, seed, k, budget.Worlds, budget.NodesPerWorld)).ToList());
        return PimcBrain.Merge(first, replies.Select(r => r.Lines!).ToList());
    }

    /// <summary>SL: each first action gets the share of the budget that it gets in one search of the world. The budget is
    /// the room's already (<see cref="Budget.For"/>).</summary>
    public CombatDecisionResult Decide(NodeClone node, IReadOnlyList<CombatAction> legal, Budget budget)
    {
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        var first = PimcBrain.Ordered(legal);
        int nodes = Math.Max(1, budget.NodesPerWorld / first.Count);
        var replies = Map(first.Select((action, a) => (WorkerRequest)new SlWorldRequest(a, node, [action], nodes)).ToList());
        return PimcBrain.Merge(first, [replies.Select(r => r.Lines![0]).ToList()]);
    }

    /// <summary>As Harness's combat route: NoSL draws the build seed from the decision's seed; SL searches the clone.</summary>
    /// <param name="name">The brain's name in reports: pimc, and its leaf weights' name unless they are the default.</param>
    public CombatRoute Route(Budget budget, string name = "pimc") =>
        new(name, (decision, seed, node) => node == null
            ? Decide(decision.Observation, decision.Legal, seed.ToString("X16"), budget, seed)
            : Decide(node, decision.Legal, budget.For(decision.Observation.Room)), budget);

    /// <summary>The game's Log.Error lines in the workers since the last call.</summary>
    public List<string> TakeGameErrors()
    {
        var errors = _gameErrors.ToList();
        _gameErrors.Clear();
        return errors;
    }

    private IReadOnlyList<WorkerReply> Map(IReadOnlyList<WorkerRequest> requests)
    {
        var replies = pool.Map(requests);
        foreach (var r in replies) _gameErrors.AddRange(r.GameErrors);
        if (replies.FirstOrDefault(r => r.Error != null) is { } failed) throw new InvalidOperationException($"world {failed.Id}: {failed.Error}");
        return replies;
    }
}
