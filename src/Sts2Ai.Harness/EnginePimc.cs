using System.Runtime.ExceptionServices;
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
///
/// Until the C# search goes (the plan's next step), STS2AI_PIMC_SEARCH=cs searches the worlds with it instead, in
/// threads of this process, and check with both, stopping at the first line that differs in anything.
/// </summary>
/// <param name="net">The combat net in the environment it reads states in (<see cref="CombatNet.In"/>), loaded in the
/// engine where a decision is first searched (the engine's library is loaded only once a world is made), once for each
/// net file.</param>
/// <param name="threads">How many worlds are searched at a time.</param>
public sealed class EnginePimc(CombatNet net, int threads)
{
    private readonly PimcBrain _brain = new(net);

    private readonly Lazy<EngineNet> _engine = new(() => Loaded.GetValue(net.File, file => EngineNet.Load(Convert.FromBase64String(file.Onnx),
        ProtocolJson.Serialize(file with { Onnx = "", Tests = [] }))));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CombatNetFile, EngineNet> Loaded = new();

    private static readonly string Search = Environment.GetEnvironmentVariable("STS2AI_PIMC_SEARCH") ?? "engine";

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
        return PimcBrain.Root(PimcBrain.Ordered(legal, observation), budget, (first, nodes) => Search switch
        {
            "cs" => CSharp(real, observation, first, nodes, budget, seed, history),
            "check" => Checked(Engine(real, first, nodes, budget, seed, history), CSharp(real, observation, first, nodes, budget, seed, history), first),
            _ => Engine(real, first, nodes, budget, seed, history),
        });
    }

    private IReadOnlyList<IReadOnlyList<PlannedLine>> Engine(EngineWorld real, IReadOnlyList<CombatAction> first, int nodes, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns> history) =>
        real.Pimc(_engine.Value, net.RunValue, history, first, nodes, budget.Worlds, seed, net.Env == PlayEnvironment.NoSL, threads);

    /// <summary>The worlds searched by the C# search, each drawn from a copy of the real combat in a thread of its own.</summary>
    private IReadOnlyList<IReadOnlyList<PlannedLine>> CSharp(EngineWorld real, CombatObservation observation, IReadOnlyList<CombatAction> first,
        int nodes, Budget budget, ulong seed, IReadOnlyList<EnemyTurns> history)
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
    }

    /// <summary>The engine's lines, once each is the C# search's to the last bit: its value, its actions, the hashes
    /// before them and whether it repeats a cycle.</summary>
    private static IReadOnlyList<IReadOnlyList<PlannedLine>> Checked(IReadOnlyList<IReadOnlyList<PlannedLine>> engine,
        IReadOnlyList<IReadOnlyList<PlannedLine>> csharp, IReadOnlyList<CombatAction> first)
    {
        if (engine.Count != csharp.Count) throw new InvalidOperationException($"pimc check: {engine.Count} worlds in the engine, {csharp.Count} in C#");
        for (int k = 0; k < engine.Count; k++)
            for (int a = 0; a < first.Count; a++)
            {
                PlannedLine e = engine[k][a], c = csharp[k][a];
                string? differs = BitConverter.DoubleToInt64Bits(e.Value) != BitConverter.DoubleToInt64Bits(c.Value) ? $"value {e.Value:R} against {c.Value:R}"
                    : !e.Actions.SequenceEqual(c.Actions) ? $"actions {string.Join(' ', e.Actions)} against {string.Join(' ', c.Actions)}"
                    : !(e.Hashes ?? []).SequenceEqual(c.Hashes ?? []) ? "hashes"
                    : e.Repeats != c.Repeats ? $"repeats {e.Repeats} against {c.Repeats}" : null;
                if (differs != null) throw new InvalidOperationException($"pimc check: world {k}, first action {first[a]}: the engine's {differs}");
            }
        return engine;
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
