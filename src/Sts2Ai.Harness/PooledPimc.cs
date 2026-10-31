using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="PimcBrain"/> with its worlds searched in a <see cref="WorkerPool"/>. NoSL: one request per world, each
/// world rebuilt in its worker from the observation, so nothing of the true state leaves the host; with the same seeds
/// it gives the same answer as <see cref="PimcBrain.Decide"/> over in-process worlds. SL: the one exact world is the
/// node's clone, searched in one request per first action so the workers share it; each request keeps its own memo,
/// where a search in one process shares it across first actions. With sequential halving each round is one such set
/// of requests, with the actions still in. The workers search with the leaf they were started with
/// (<c>serve --leaf --combat-net</c>); each request carries the combat's record of enemy moves for it.
/// </summary>
public sealed class PooledPimc(WorkerPool pool)
{
    /// <param name="buildSeed">Seeds the combat built from the observation (docs: simulator/combat §5).</param>
    /// <param name="seed">Draws the worlds.</param>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, string buildSeed, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns>? history = null)
    {
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        budget = budget.For(observation.Room);
        return PimcBrain.Root(PimcBrain.Ordered(legal), budget, (first, nodes) => pool.Map(Enumerable.Range(0, budget.Worlds)
                .Select(k => (WorkerRequest)new PimcWorldRequest(k, observation, first, buildSeed, seed, k, budget.Worlds, nodes, history)).ToList())
            .Select(r => r.Lines!).ToList());
    }

    /// <summary>SL: each first action gets the share of the budget that it gets in one search of the world. The budget is
    /// the room's already (<see cref="Budget.For"/>).</summary>
    public CombatDecisionResult Decide(NodeClone node, IReadOnlyList<CombatAction> legal, Budget budget, IReadOnlyList<EnemyTurns>? history = null)
    {
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        return PimcBrain.Root(PimcBrain.Ordered(legal), budget, (first, nodes) =>
            [pool.Map(first.Select((action, a) => (WorkerRequest)new SlWorldRequest(a, node, [action], nodes, history)).ToList()).Select(r => r.Lines![0]).ToList()]);
    }

    /// <summary>As Harness's combat route, one per run: NoSL draws the build seed from the decision's seed; SL searches
    /// the clone. With <see cref="Budget.Follow"/>, the route keeps the run's <see cref="TurnPlan"/>.</summary>
    /// <param name="name">The brain's name in reports: pimc, and its leaf's names unless it is the default one.</param>
    public CombatRoute Route(Budget budget, string name = "pimc")
    {
        var plan = budget.Follow ? new TurnPlan() : null;
        CombatDecisionResult Search(CombatDecision decision, ulong seed, NodeClone? node, IReadOnlyList<EnemyTurns> history) => node == null
            ? Decide(decision.Observation, decision.Legal, seed.ToString("X16"), budget, seed, history)
            : Decide(node, decision.Legal, budget.For(decision.Observation.Room), history);
        return new(name, (decision, seed, node, history) => plan == null
            ? Search(decision, seed, node, history)
            : plan.Decide(decision.Observation, decision.Legal, () => Search(decision, seed, node, history)), budget);
    }
}
