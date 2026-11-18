using Sts2Ai.Brain;
using Sts2Ai.Brain.Combat;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="PimcBrain"/> in NoSL, searched in the engine (search/pimc.rs, plans/2026-11-19-0030-nosl-search-rust.md):
/// the real combat as the host wrote it out (<see cref="CombatDecision.Export"/>, the one SL takes in too) is taken in
/// once a decision, and the engine draws every world from it, with everything the player cannot see drawn again (the
/// random streams, the run's seeds, the draw pile's order from its public record, the queued moves that look alike;
/// plans/2026-11-17-1120-nosl-dread-architecture.md, option c), and searches them in its threads. What the player can
/// see or remember is the combat's own, so a world holds the game's mode and dominion, the fight's history and every
/// private number of its models, whatever the observation writes of them. The worlds' lines come back in world order
/// and are merged here (<see cref="PimcBrain.Root"/>), so a decision is the same on any number of threads.
/// </summary>
/// <param name="net">The combat net in the environment it reads states in (<see cref="CombatNet.In"/>), loaded in the
/// engine where a decision is first searched (the engine's library is loaded only once a world is made), once for each
/// net file.</param>
/// <param name="threads">How many worlds are searched at a time.</param>
public sealed class EnginePimc(CombatNet net, int threads)
{
    private readonly Lazy<EngineNet> _engine = new(() => Loaded.GetValue(net.File, file => EngineNet.Load(Convert.FromBase64String(file.Onnx),
        ProtocolJson.Serialize(file with { Onnx = "", Tests = [] }))));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CombatNetFile, EngineNet> Loaded = new();

    /// <param name="live">The real combat, written out between the player's actions, and the run around it.</param>
    /// <param name="seed">Draws the worlds: world k from FNV-1a of "SEED:K" (search/pimc.rs, world_seed).</param>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<CombatExport> live, Budget budget,
        ulong seed, IReadOnlyList<EnemyTurns> history)
    {
        if (PimcBrain.Unplanned(observation) is { } unplanned) return unplanned;
        if (legal.Count == 1) return PimcBrain.Only(legal[0]);
        var export = live();
        using var real = EngineWorld.Import(export.Bytes, export.Run);
        return PimcBrain.Root(PimcBrain.Ordered(legal, observation), budget, (first, nodes) =>
            real.Pimc(_engine.Value, net.RunValue, history, first, nodes, budget.Worlds, seed, net.Env == PlayEnvironment.NoSL, threads));
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
