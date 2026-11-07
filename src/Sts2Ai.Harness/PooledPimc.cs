using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="PimcBrain"/> in NoSL with its worlds searched in a <see cref="WorkerPool"/>: one request per world, each
/// world rebuilt in its worker from the observation, so nothing of the true state leaves the host; with the same seeds
/// it gives the same answer as <see cref="PimcBrain.Decide"/> over in-process worlds. The workers search with the leaf
/// they were started with (<c>serve --combat-net</c>); each request carries the combat's record of enemy moves for it.
/// </summary>
public sealed class PooledPimc(WorkerPool pool)
{
    /// <param name="buildSeed">Seeds the combat built from the observation (docs: simulator/combat §5).</param>
    /// <param name="seed">Draws the worlds.</param>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    /// <param name="run">The run around the combat, for the leaves.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, string buildSeed, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns> history, RunView run)
    {
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        return PimcBrain.Root(PimcBrain.Ordered(legal), budget, (first, nodes) => pool.Map(Enumerable.Range(0, budget.Worlds)
                .Select(k => (WorkerRequest)new PimcWorldRequest(k, observation, first, buildSeed, seed, k, budget.Worlds, nodes, history, run)).ToList())
            .Select(r => r.Lines!).ToList());
    }

    /// <summary>As Harness's combat route, one per run: the build seed drawn from the decision's seed. The route keeps the
    /// run's <see cref="TurnPlan"/>, with the plans that repeat a cycle.</summary>
    /// <param name="name">The brain's name in reports: pimc and its leaf's name.</param>
    public CombatRoute Route(Budget budget, string name)
    {
        var plan = new TurnPlan(all: false);
        return new(name, (decision, seed, _, history) => plan.Decide(decision.Observation, decision.Legal,
            () => Decide(decision.Observation, decision.Legal, seed.ToString("X16"), budget, seed, history,
                decision.RunObservation?.Run ?? throw new InvalidOperationException("pimc searches a combat in a run: its leaves need the run"))), budget);
    }
}
