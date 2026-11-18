using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="SlPlanner"/> with its turns searched in a <see cref="WorkerPool"/>: each turn of the
/// beam is one request per state kept, which loads the node's clone, plays the state's path and searches its turn, so
/// the workers split the beam by action prefix. With the same budget it plans what <see cref="SlPlanner.Plan"/> plans on the node's world in one process.
/// The workers search with the leaf they were started with (<c>serve --combat-net</c>).
/// </summary>
public sealed class PooledPlanner(WorkerPool pool)
{
    /// <summary>Plans the rest of the combat at the decision that <paramref name="node"/> stands at.</summary>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    public SlPlan Plan(NodeClone node, Budget budget, IReadOnlyList<EnemyTurns> history) =>
        SlPlanner.Search(budget, history, (jobs, nodes, keep) => [.. pool.Map([.. jobs
                .Select((job, i) => (WorkerRequest)new SlTurnRequest(i, node, job.Path, nodes, keep, job.History, job.LifeBound))])
            .Select(r => r.Turn ?? throw new InvalidDataException($"request {r.Id}: no turn in the reply"))]);

    /// <summary>As Harness's combat route in SL, one per run (<see cref="SlRoute"/>): plans once, then follows the plan,
    /// planning again only where it does not hold or has run out.</summary>
    /// <param name="name">The brain's name in reports: plan and its leaf's name.</param>
    public CombatRoute Route(Budget budget, string name)
    {
        var route = new SlRoute(budget);
        // The search's processor time is the workers'.
        var meter = new PlanMeter(pool.CpuTime);
        return new(name, (decision, _, node, history) => route.Decide(decision.Observation, decision.Legal, b => meter.Count(() =>
            Plan(node ?? throw new InvalidOperationException("the SL planner has no node clone: it plays in SL only"),
                b, history))), budget, () => meter.Tally);
    }
}
