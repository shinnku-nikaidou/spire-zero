using System.Runtime.CompilerServices;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.Tests;

internal static class Fixtures
{
    /// <summary>As the sim's Main: the game's assemblies resolve from the snapshot, should a test reach a game type.</summary>
    [ModuleInitializer]
    internal static void ResolveGameAssemblies() => GameAssemblies.InstallResolver();

    /// <summary>A real decision from self-play: round 1 of CORPSE_SLUGS_WEAK, two slugs (uids 1 and 2), SILENT at 70/70.</summary>
    public static CombatObservation Slugs() =>
        ProtocolJson.Deserialize<CombatObservation>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "corpse-slugs-round1.json")));

    /// <summary>A real run at its first decision, from self-play: SILENT on floor 1 at 70/70, 12 cards, Ring of the Snake.</summary>
    public static RunView Floor1() =>
        ProtocolJson.Deserialize<RunView>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "run-floor1.json")));

    private static readonly RunView Floor1Run = Floor1();

    /// <summary>A run around a combat for the search tests' worlds: <see cref="Floor1"/> with the combat's HP, max HP, gold and potions.</summary>
    public static RunView Around(CombatObservation obs) =>
        Floor1Run with { Hp = obs.Player.Hp, MaxHp = obs.Player.MaxHp, Gold = obs.Player.Gold, Potions = obs.Potions };

    public static CombatObservation WithEnemyHp(this CombatObservation obs, uint uid, int hp) =>
        obs with { Enemies = [.. obs.Enemies.Select(e => e.Uid == uid ? e with { Hp = hp } : e)] };

    public static readonly RunConfig Config = new("v0.111.0", "abc1234", "combat pimc 8x300, combat prompts p0, macro l0, macro prompts l0", "NoSL");

    public static RunRecord Run(string seed, int floor, string outcome = "lost", RunConfig? config = null, int randomDecisions = 0) =>
        new(config ?? Config, seed, "SILENT", 0, outcome, floor / 17, floor, 0, 70, 99, [], new Dictionary<string, int> { ["map"] = 10 },
            randomDecisions > 0 ? new Dictionary<string, int> { [Metrics.RandomBrain] = randomDecisions } : [],
            0, 0, 0, outcome == "failed" ? "boom" : null, 1000, $"run-{seed}");
}

/// <summary>
/// A leaf for the search tests, which have no combat net or V: a loss nothing, a win 0.2 and more for the HP share the run
/// keeps, and a combat still going below any win, higher for more HP kept and more of the enemies' HP taken.
/// </summary>
internal sealed class ToyLeaf : ICombatLeaf
{
    public LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history)
    {
        double kept = run.Hp / (double)Math.Max(1, run.MaxHp);
        if (status == CombatStatus.Lost) return new(0, 0, 0);
        if (status == CombatStatus.Won) return new(0.2 + 0.8 * kept, 1, run.Hp);
        double taken = 1 - obs.Enemies.Sum(e => Math.Max(0, e.Hp)) / (double)Math.Max(1, obs.Enemies.Sum(e => e.MaxHp));
        return new(0.2 * (kept + taken) / 2, 0, 0);
    }
}
