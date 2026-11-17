using System.Runtime.ExceptionServices;
using Sts2Ai.Brain;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="PimcBrain"/> in NoSL, its worlds searched in the engine, in threads of this process. Every world is the
/// real combat as the host wrote it out (<see cref="CombatDecision.Export"/>, the one SL takes in too) with everything
/// the player cannot see drawn again in the engine (<see cref="EngineWorld.Determinize"/>): the random streams, the
/// run's seeds, the draw pile's order from its public record, the queued moves that look alike
/// (plans/2026-11-17-1120-nosl-dread-architecture.md, option c). What the player can see or remember is the combat's
/// own, so a world holds the game's mode and dominion, the fight's history and every private number of its models,
/// whatever the observation writes of them. The worlds' lines are merged in world order, so a decision is the same on
/// any number of threads.
/// </summary>
/// <param name="leaf">Prices the lines; asked from every thread at once.</param>
/// <param name="threads">How many worlds are searched at a time.</param>
public sealed class EnginePimc(ICombatLeaf leaf, int threads)
{
    private readonly PimcBrain _brain = new(leaf);

    /// <param name="live">The real combat, written out between the player's actions, and the run around it.</param>
    /// <param name="seed">Draws the worlds: world k is drawn from <see cref="WorldSeed"/> of it and k.</param>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<CombatExport> live, Budget budget,
        ulong seed, IReadOnlyList<EnemyTurns> history)
    {
        if (PimcBrain.Unplanned(observation) is { } unplanned) return unplanned;
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        var export = live();
        using var real = EngineWorld.Import(export.Bytes, export.Run);
        return PimcBrain.Root(PimcBrain.Ordered(legal, observation), budget, (first, nodes) =>
        {
            // A native world is used by one thread at a time: each thread draws its world from a copy of its own.
            var copies = new EngineWorld[budget.Worlds];
            var worlds = new EngineWorld[budget.Worlds];
            var lines = new IReadOnlyList<PlannedLine>[budget.Worlds];
            try
            {
                for (int k = 0; k < copies.Length; k++) copies[k] = real.Clone();
                Parallel.For(0, worlds.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
                {
                    worlds[k] = copies[k].Determinize(WorldSeed(seed, k));
                    lines[k] = _brain.SearchWorld(worlds[k], observation.Round, first, nodes, history);
                });
            }
            catch (AggregateException e)
            {
                // The first failure as it was thrown: a run's record keeps one line of it.
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            finally
            {
                foreach (var world in copies.Concat(worlds)) world?.Dispose();
            }
            return lines;
        });
    }

    /// <summary>World <paramref name="k"/>'s seed of a decision's: FNV-1a of the two, the same in every process.</summary>
    public static ulong WorldSeed(ulong seed, int k)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in $"{seed}:{k}") hash = (hash ^ c) * 1099511628211;
        return hash;
    }

    /// <summary>As Harness's combat route, one per run: the same decisions on the same seeds.</summary>
    /// <param name="name">The brain's name in reports: pimc and its leaf's name.</param>
    public CombatRoute Route(Budget budget, string name)
    {
        var plan = new TurnPlan(all: false);
        return new(name, (decision, seed, _, history) => plan.Decide(decision.Observation, decision.Legal,
            () => Decide(decision.Observation, decision.Legal,
                () => decision.Export ?? throw new InvalidOperationException("the host gives no live combat: NoSL's worlds are drawn from it"),
                budget.For(decision.Observation.Room), seed, history)), budget);
    }
}
