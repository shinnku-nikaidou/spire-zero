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
/// A card prompt the combat waits on is searched where it stands (plans/2026-11-21-1213-nosl-prompt-worlds.md): the
/// game is in the middle of an action there, so the engine takes the combat as the host last wrote it out and the
/// steps since (<see cref="PromptCombat"/>), and each world is drawn where the prompt takes its answer, from the
/// host's record of the draw pile there. Every answer is searched (<see cref="PimcBrain.Answers"/>).
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
    /// <param name="prompted">At a card prompt: the combat for the engine to stand there.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<CombatExport> live, Budget budget,
        ulong seed, IReadOnlyList<EnemyTurns> history, Func<PromptCombat>? prompted = null)
    {
        if (PimcBrain.Forced(observation, legal) is { } only) return PimcBrain.Only(only);
        RootSearch Search(EngineWorld real) => (first, nodes) =>
            real.Pimc(_engine.Value, net.FightValue, history, first, nodes, budget.Worlds, seed, net.Env == PlayEnvironment.NoSL, threads);
        if (observation.Prompt is { } prompt)
        {
            using var at = Stand(prompted?.Invoke() ?? throw new InvalidOperationException("a card prompt with no combat to stand at"));
            return PimcBrain.Answers(PromptAnswers.Of(prompt, SlPlanner.Unlimited, out bool _), budget, Search(at));
        }
        var export = live();
        using var real = EngineWorld.Import(export.Bytes, export.Run);
        return PimcBrain.Root(PimcBrain.Ordered(legal, observation), budget, Search(real));
    }

    /// <summary>The engine standing at the card prompt the combat waits on: the combat as the host last wrote it out,
    /// begun first where it was written as it began, and the steps since taken as the game took them; with the host's
    /// record of the draw pile at the prompt, which the worlds drawn there read. With <paramref name="alone"/>, SL's
    /// world of a co-op combat, the other players killed before the steps (<see cref="EngineWorld.Alone"/>).</summary>
    public static EngineWorld Stand(PromptCombat at, bool alone = false)
    {
        var world = alone ? EngineWorld.Alone(at.From.Bytes, at.From.Run, at.Opening)
            : at.Opening ? EngineWorld.Begin(at.From.Bytes, at.From.Run) : EngineWorld.Import(at.From.Bytes, at.From.Run);
        try
        {
            foreach (var step in at.Steps)
            {
                int space = step.IndexOf(' ');
                var (surface, text) = space < 0 ? (step, "") : (step[..space], step[(space + 1)..]);
                if (surface == Surfaces.Combat) world.Execute(CombatAction.Parse(text));
                else if (surface == Surfaces.CardChoice) world.Answer([.. text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)]);
                else throw new InvalidDataException($"a step the combat does not take: {step}");
            }
            world.DrawLog(at.DrawLog);
            return world;
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }

    /// <summary>As Harness's combat route, one per run: the same decisions on the same seeds.</summary>
    /// <param name="name">The brain's name in reports: pimc and its leaf's name.</param>
    /// <param name="tables">Whether each answer between actions carries its line's table (<see cref="Table"/>): the
    /// mod's brain.</param>
    public CombatRoute Route(Budget budget, string name, bool tables = false)
    {
        var plan = new TurnPlan(all: false);
        return new(name, (decision, seed, _, history) =>
        {
            var result = plan.Decide(decision.Observation, decision.Legal,
                () => Decide(decision.Observation, decision.Legal,
                    () => decision.Export ?? throw new InvalidOperationException("the host gives no live combat: NoSL's worlds are drawn from it"),
                    budget.For(decision.Observation.Room), seed, history,
                    () => decision.Prompted ?? throw new InvalidOperationException("the host gives no combat at the card prompt: NoSL's worlds are drawn there")));
            return tables && decision.Observation.Prompt == null && decision.Export is { } live
                ? result with { Table = Table(live, seed, history, result) }
                : result;
        }, budget);
    }

    /// <summary>The answer's line played out on the world it was planned in, PIMC's first (drawn as search/pimc.rs draws
    /// world 0), to the end of the enemies' turn; then the combat net's outcome there.</summary>
    private FightTable Table(CombatExport live, ulong seed, IReadOnlyList<EnemyTurns> history, CombatDecisionResult result)
    {
        using var real = EngineWorld.Import(live.Bytes, live.Run);
        using var world = real.Determinize(FightTables.WorldSeed(seed, 0));
        return FightTables.Of(world, [result.First, .. result.Plan], at =>
        {
            var (_, win, hp) = at.Outcome(_engine.Value, net.RunValue, history);
            return (win, hp);
        });
    }
}
