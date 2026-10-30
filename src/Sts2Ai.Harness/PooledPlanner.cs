using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="SlPlanner"/> with its turns searched in a <see cref="WorkerPool"/> (docs: sl-planner/budget §4): each turn of the
/// beam is one request per state kept, which loads the node's clone, plays the state's path and searches its turn, so
/// the workers split the beam by action prefix and share the best whole line found so far as the floor of their
/// pruning. With the same budget it plans what <see cref="SlPlanner.Plan"/> plans on the node's world in one process.
/// The workers search with the leaf they were started with (<c>serve --leaf --combat-net</c>), at V's prices of the
/// combat's ends if the plan has them (<see cref="EndPrices"/>).
/// </summary>
public sealed class PooledPlanner(WorkerPool pool)
{
    /// <summary>Plans the rest of the combat at the decision that <paramref name="node"/> stands at.</summary>
    /// <param name="budget">The room's budget already (<see cref="Budget.For"/>).</param>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    /// <param name="prices">V's prices of the combat's ends, or null for the leaf weights'.</param>
    public SlPlan Plan(NodeClone node, Budget budget, IReadOnlyList<EnemyTurns> history, EndPrices? prices = null) =>
        SlPlanner.Search(budget, history, (jobs, nodes, keep, floor) => pool.Map(jobs
                .Select((job, i) => (WorkerRequest)new SlTurnRequest(i, node, job.Path, nodes, keep, floor, job.History, prices)).ToList())
            .Select(r => r.Turn ?? throw new InvalidDataException($"request {r.Id}: no turn in the reply")).ToList());

    /// <summary>As Harness's combat route in SL, one per run (<see cref="SlRoute"/>): plans once, then follows the plan,
    /// planning again only where it does not hold or has run out.</summary>
    /// <param name="name">The brain's name in reports: plan, and its leaf's names unless it is the default one.</param>
    /// <param name="prices">V's prices of a combat's ends from the run at the decision, made at each plan; null for the
    /// leaf weights'.</param>
    public CombatRoute Route(Budget budget, string name = "plan", Func<RunView, EndPrices>? prices = null)
    {
        var route = new SlRoute(budget);
        return new(name, (decision, _, node, history) => route.Decide(decision.Observation, decision.Legal, b =>
            Plan(node ?? throw new InvalidOperationException("the SL planner has no node clone: it plays in SL only"),
                b.For(decision.Observation.Room), history,
                prices?.Invoke((decision.RunObservation ?? throw new InvalidOperationException("a combat decision outside a run")).Run))), budget);
    }
}
