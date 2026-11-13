using Sts2Ai.Brain;
using Sts2Ai.Engine;
using Sts2Ai.GameBridge;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.SimWorker.Tests;

/// <summary>
/// The decision side's boundary, as selftest checks it but without booting the game: the brain, the protocol and Harness
/// reach no game assembly, and the check does see the bridge's. Harness references the engine's binding too, whose
/// native library is loaded when a world is first made: a host that plans on the node's clone needs no library.
/// </summary>
public class BoundaryTests
{
    [Fact]
    public void TheBrainProtocolAndHarnessReachNoGameAssembly()
    {
        foreach (var root in new[] { typeof(ICombatPolicy).Assembly, typeof(CombatObservation).Assembly, typeof(WorkerPool).Assembly, typeof(EngineWorld).Assembly })
            Assert.Empty(GameAssemblies.ReachedFrom(root));
        Assert.Contains("sts2", GameAssemblies.ReachedFrom(typeof(Observe).Assembly));
    }

    [Fact]
    public void TheEnginesLibraryIsLoadedOnlyWhenAWorldIsMade()
    {
        // No library sits beside the tests, unless the developer's variable names one.
        if (File.Exists(EngineLibrary.Path)) return;
        _ = new EnginePlanner(null!, threads: 2);
        var run = new RunView
        {
            Character = "SILENT", Ascension = 0, Act = 1, ActId = "HIVE", Floor = 1, Hp = 70, MaxHp = 70, Gold = 99,
            Deck = [], Relics = [], Potions = [null],
            Map = new MapView { Points = [], Visited = [], Boss = "QUEEN_BOSS" },
        };
        var missing = Assert.Throws<DllNotFoundException>(() => EngineWorld.Import("{}"u8, run));
        Assert.Contains($"no engine library at {EngineLibrary.Path}", missing.Message);
    }
}
