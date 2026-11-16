using System.Runtime.ExceptionServices;
using Sts2Ai.Brain;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="PimcBrain"/> in NoSL with its worlds searched in the engine, in threads of this process. Each world is
/// built from the observation and determinized in a worker of the pool, as <see cref="PooledPimc"/>'s is, so nothing of
/// the true state leaves the host; it comes back written out where it begins (<see cref="PimcSampleRequest"/>), is taken
/// in as a NoSL sample (<see cref="EngineWorld.Sample"/>) and is searched in a thread of its own. The worlds' lines are
/// merged in world order, so a decision is the same on any number of threads, and with the same seeds and leaf it is
/// <see cref="PooledPimc"/>'s, as long as the engine plays the combat as the game does.
/// </summary>
/// <param name="leaf">Prices the lines; asked from every thread at once.</param>
/// <param name="threads">How many worlds are searched at a time.</param>
/// <param name="pool">The workers that build the worlds.</param>
public sealed class EnginePimc(ICombatLeaf leaf, int threads, WorkerPool pool)
{
    private readonly PimcBrain _brain = new(leaf);

    /// <param name="buildSeed">Seeds the combat built from the observation.</param>
    /// <param name="seed">Draws the worlds.</param>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    /// <param name="run">The run around the combat, for the leaves.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, string buildSeed, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns> history, RunView run)
    {
        if (PimcBrain.Unplanned(observation) is { } unplanned) return unplanned;
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        return PimcBrain.Root(PimcBrain.Ordered(legal, observation), budget, (first, nodes) =>
        {
            var samples = pool.Map([.. Enumerable.Range(0, budget.Worlds)
                .Select(k => (WorkerRequest)new PimcSampleRequest(k, observation, buildSeed, seed, k, budget.Worlds, history))]);
            var worlds = new EngineWorld?[samples.Count];
            var lines = new IReadOnlyList<PlannedLine>[samples.Count];
            try
            {
                Parallel.For(0, samples.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
                {
                    var bytes = Convert.FromBase64String(samples[k].Sample ?? throw new InvalidDataException($"request {samples[k].Id}: no sample in the reply"));
                    var world = worlds[k] = EngineWorld.Sample(bytes, run);
                    lines[k] = _brain.SearchWorld(world, observation.Round, first, nodes, history);
                });
            }
            catch (AggregateException e)
            {
                // The first failure as it was thrown: a run's record keeps one line of it.
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            finally
            {
                foreach (var world in worlds) world?.Dispose();
            }
            return lines;
        });
    }

    /// <summary>As Harness's combat route, one per run, like <see cref="PooledPimc.Route"/>: the same decisions on the
    /// same seeds.</summary>
    /// <param name="name">The brain's name in reports: pimc and its leaf's name, as on replay.</param>
    public CombatRoute Route(Budget budget, string name)
    {
        var plan = new TurnPlan(all: false);
        return new(name, (decision, seed, _, history) => plan.Decide(decision.Observation, decision.Legal,
            () => Decide(decision.Observation, decision.Legal, seed.ToString("X16"), budget.For(decision.Observation.Room), seed, history,
                decision.RunObservation?.Run ?? throw new InvalidOperationException("pimc searches a combat in a run: its leaves need the run"))), budget);
    }
}
