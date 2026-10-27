using System.Runtime.CompilerServices;
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

    public static CombatObservation WithEnemyHp(this CombatObservation obs, uint uid, int hp) =>
        obs with { Enemies = obs.Enemies.Select(e => e.Uid == uid ? e with { Hp = hp } : e).ToList() };

    public static readonly RunConfig Config = new("v0.111.0", "abc1234", "combat pimc 8x300, combat prompts p0, macro l0, macro prompts l0", "NoSL", "R1");

    public static RunRecord Run(string seed, int floor, string outcome = "lost", RunConfig? config = null, int randomDecisions = 0) =>
        new(config ?? Config, seed, "SILENT", 0, outcome, floor / 17, floor, 0, 70, 99, [], new Dictionary<string, int> { ["map"] = 10 },
            randomDecisions > 0 ? new Dictionary<string, int> { [Metrics.RandomBrain] = randomDecisions } : new Dictionary<string, int>(),
            0, 0, 0, outcome == "failed" ? "boom" : null, 1000);
}
