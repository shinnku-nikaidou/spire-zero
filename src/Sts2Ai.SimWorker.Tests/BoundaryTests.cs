using Sts2Ai.Brain;
using Sts2Ai.Engine;
using Sts2Ai.GameBridge;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.SimWorker.Tests;

/// <summary>
/// The decision side's boundary, as selftest checks it but without booting the game: the brain, the protocol and Harness
/// reach no game assembly, and the check does see the bridge's.
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
}
