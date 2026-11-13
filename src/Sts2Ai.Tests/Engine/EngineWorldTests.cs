using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Engine;

/// <summary>
/// A stand-in for the engine's library, built from Fixtures/engine/fake_engine.rs with rustc, the engine's own
/// toolchain: it has the C ABI of engine/DESIGN.md and a combat that is a list of the actions taken.
/// </summary>
public sealed class FakeEngine : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sts2ai-engine-").FullName;
    private readonly Live _live;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long Live();

    public FakeEngine()
    {
        string library = Path.Combine(_dir, "libsts2ai_engine.so");
        var rustc = Process.Start(new ProcessStartInfo("rustc",
            ["--edition", "2024", "--crate-type", "cdylib", "-C", "opt-level=1", "-o", library, Path.Combine(AppContext.BaseDirectory, "Fixtures", "engine", "fake_engine.rs")])
        {
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("rustc did not start");
        string errors = rustc.StandardError.ReadToEnd();
        rustc.WaitForExit();
        if (rustc.ExitCode != 0) throw new InvalidOperationException($"rustc could not build the stand-in engine: {errors}");
        Environment.SetEnvironmentVariable(EngineLibrary.Variable, library);
        _live = Marshal.GetDelegateForFunctionPointer<Live>(NativeLibrary.GetExport(NativeLibrary.Load(library), "sz_test_live"));
    }

    /// <summary>The native worlds not yet freed, once the collector has taken what nothing holds.</summary>
    public long Worlds()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return _live();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

/// <summary>The tests that use the stand-in engine: one library for all of them, since a process loads the engine once,
/// and one test at a time, since some count the library's live worlds.</summary>
[CollectionDefinition(Name)]
public sealed class EngineTests : ICollectionFixture<FakeEngine>
{
    public const string Name = "engine";
}

[Collection(EngineTests.Name)]
public sealed class EngineWorldTests(FakeEngine engine)
{
    private static readonly RunView Around = new()
    {
        Character = "SILENT", Ascension = 10, Act = 1, ActId = "HIVE", Floor = 20, Hp = 70, MaxHp = 70, Gold = 99,
        Deck = [], Relics = [], Potions = [null],
        Map = new MapView { Points = [], Visited = [], Boss = "QUEEN_BOSS" },
    };

    private static EngineWorld World(string encounter = "NIBBITS_WEAK") => EngineWorld.Import(Encoding.UTF8.GetBytes(encounter), Around);

    [Fact]
    public void AWorldShowsAndPlaysTheCombatItTookIn()
    {
        using var world = World();
        Assert.Equal(("NIBBITS_WEAK", 1, 70), (world.Observe().Encounter, world.Observe().Round, world.Observe().Player.Hp));
        Assert.Equal([CombatAction.Play(7), CombatAction.Potion(0, 2), CombatAction.EndTurn], world.Legal());
        Assert.Equal(CombatStatus.InProgress, world.Status);

        world.Execute(CombatAction.EndTurn);
        Assert.Equal((2, 69), (world.Observe().Round, world.Observe().Player.Hp));
        Assert.Equal((1UL, 2UL), (world.HiddenKey(), world.StateKey()));
        Assert.StartsWith("""{"encounter":"NIBBITS_WEAK","act":0,"ascension":0,"round":2,""", world.ObservationJson());
    }

    [Fact]
    public void TheRunIsTheOneAroundTheCombatWithWhatTheCombatChanged()
    {
        using var world = World();
        world.Execute(CombatAction.EndTurn);
        var run = world.Run();
        Assert.Equal((69, "STRIKE_SILENT", "RING_OF_THE_SNAKE"), (run.Hp, run.Deck.Single().Id, run.Relics.Single().Id));
        Assert.Equal(("HIVE", 20, 10, "QUEEN_BOSS"), (run.ActId, run.Floor, run.Ascension, run.Map.Boss));
    }

    [Theory]
    [InlineData("play:7", CombatStatus.Won)]
    [InlineData("potion:0", CombatStatus.Lost)]
    public void ACombatThatIsOverHasNoPlays(string last, CombatStatus status)
    {
        using var world = World();
        world.Execute(CombatAction.Parse(last));
        Assert.Equal(status, world.Status);
        Assert.Empty(world.Legal());
    }

    [Fact]
    public void AFailureSaysWhatTheEngineSaid()
    {
        Assert.Contains("takes no combat called refuse", Assert.Throws<EngineException>(() => World("refuse")).Message);
        using var world = World();
        var failed = Assert.Throws<EngineException>(() => world.Execute(CombatAction.Play(9)));
        Assert.Equal("sz_world_apply play:9: no card 9 in hand", failed.Message);
        Assert.Equal(0UL, world.HiddenKey());
    }

    [Fact]
    public void AKeyOfZeroIsAKeyUnlessTheEngineSaysItFailed()
    {
        using var world = World();
        Assert.Throws<EngineException>(() => world.Execute(CombatAction.Play(9)));
        // No action taken: the key is 0, and the failure before it is not this call's.
        Assert.Equal(0UL, world.HiddenKey());
        using var keyless = World("keyless");
        Assert.Equal("sz_world_hidden_key: the stand-in has no key for a combat called keyless", Assert.Throws<EngineException>(() => keyless.HiddenKey()).Message);
        Assert.Equal("sz_world_state_key: the stand-in has no key for a combat called keyless", Assert.Throws<EngineException>(() => keyless.StateKey()).Message);
    }

    [Fact]
    public void ASnapshotBringsItsStateBackAnyNumberOfTimes()
    {
        using var world = World();
        using var other = World();
        world.Execute(CombatAction.EndTurn);
        var snapshot = world.Snapshot();
        for (int again = 0; again < 3; again++)
        {
            world.Execute(CombatAction.EndTurn);
            world.Execute(CombatAction.Play(7));
            Assert.Equal((3, CombatStatus.Won), (world.Observe().Round, world.Status));
            world.Restore(snapshot);
            Assert.Equal((2, CombatStatus.InProgress, 1UL), (world.Observe().Round, world.Status, world.HiddenKey()));
        }
        Assert.Throws<InvalidOperationException>(() => other.Restore(snapshot));
        Assert.Throws<InvalidOperationException>(() => world.Restore(new WorldSnapshot([])));
    }

    [Fact]
    public void WorldsShareNothingAndPlayInThreadsOfTheirOwn()
    {
        using var world = World();
        world.Execute(CombatAction.EndTurn);
        var copies = Enumerable.Range(0, 8).Select(_ => world.Clone()).ToList();
        Parallel.For(0, copies.Count, i =>
        {
            for (int turn = 0; turn < 10 * (i + 1); turn++) copies[i].Execute(CombatAction.EndTurn);
        });
        Assert.Equal(Enumerable.Range(0, 8).Select(i => 2 + 10 * (i + 1)), copies.Select(c => c.Observe().Round));
        Assert.Equal(2, world.Observe().Round);
        foreach (var copy in copies) copy.Dispose();
    }

    [Fact]
    public void EveryNativeWorldIsFreed()
    {
        long before = engine.Worlds();
        var kept = Play(out var world);
        // The world, the snapshot still held, and nothing of the snapshots that were dropped.
        Assert.Equal(before + 2, engine.Worlds());
        world.Restore(kept);
        Assert.Equal(before + 2, engine.Worlds());
        world.Dispose();
        Assert.Equal(before, engine.Worlds());
        GC.KeepAlive(kept);
    }

    /// <summary>Takes snapshots and drops all but one, as search does, in a frame of its own so that none stays on the stack.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorldSnapshot Play(out EngineWorld world)
    {
        world = World();
        using (var copy = world.Clone()) copy.Execute(CombatAction.EndTurn);
        WorldSnapshot? kept = null;
        for (int i = 0; i < 50; i++)
        {
            var snapshot = world.Snapshot();
            world.Execute(CombatAction.EndTurn);
            if (i == 20) kept = snapshot;
            else if (i % 2 == 0) world.Restore(snapshot);
        }
        return kept!;
    }
}
