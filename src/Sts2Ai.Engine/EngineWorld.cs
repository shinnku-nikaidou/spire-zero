using System.Runtime.CompilerServices;
using System.Text;
using Sts2Ai.Protocol;

namespace Sts2Ai.Engine;

/// <summary>
/// A combat in the engine, as search uses a world. It is made from a live combat as the bridge wrote it out
/// (<see cref="Import"/>), or from another world (<see cref="Clone"/>); worlds share nothing, so each may be searched in
/// a thread of its own. A snapshot is a copy of the native world, which <see cref="Restore"/> copies back: the copy
/// belongs to the snapshot object and goes when the collector takes that.
/// </summary>
public sealed unsafe class EngineWorld : ICombatWorld, IDisposable
{
    private readonly ConditionalWeakTable<WorldSnapshot, WorldHandle> _snapshots = [];
    private readonly RunView _run;
    private readonly long _weight;
    private WorldHandle _world;

    private EngineWorld(WorldHandle world, RunView run, long weight)
    {
        _world = world;
        _run = run;
        _weight = weight;
    }

    /// <param name="export">The live combat as <c>EngineExport</c> wrote it.</param>
    /// <param name="run">The run around the combat, as it stood at the export: <see cref="Run"/> is this with what the
    /// combat has changed since.</param>
    /// <exception cref="EngineException">The engine cannot take the combat in.</exception>
    public static EngineWorld Import(ReadOnlySpan<byte> export, RunView run)
    {
        WorldHandle world;
        fixed (byte* json = export) world = Native.sz_world_import(json, (nuint)export.Length);
        if (world.IsInvalid) throw Native.Failed("sz_world_import");
        return new(world.Weighing(export.Length), run, export.Length);
    }

    /// <summary>An independent world standing where this one stands.</summary>
    public EngineWorld Clone() => new(Copy(_world), _run, _weight);

    private WorldHandle Copy(WorldHandle world)
    {
        var copy = Native.sz_world_clone(world);
        return copy.IsInvalid ? throw Native.Failed("sz_world_clone") : copy.Weighing(_weight);
    }

    /// <summary>The observation as the engine writes it: byte for byte what <see cref="ProtocolJson"/> would write of
    /// the real combat's.</summary>
    public string ObservationJson()
    {
        Native.Buf buf;
        if (Native.sz_world_observe(_world, &buf) != 0) throw Native.Failed("sz_world_observe");
        return Encoding.UTF8.GetString(Native.Take(buf));
    }

    public CombatObservation Observe() => ProtocolJson.Deserialize<CombatObservation>(ObservationJson());

    /// <summary>The run given at the import with what the combat has changed of it since (<see cref="RunChange"/>).</summary>
    public RunView Run()
    {
        Native.Buf buf;
        if (Native.sz_world_run(_world, &buf) != 0) throw Native.Failed("sz_world_run");
        return ProtocolJson.Deserialize<RunChange>(Encoding.UTF8.GetString(Native.Take(buf))).Over(_run);
    }

    public IReadOnlyList<CombatAction> Legal()
    {
        Native.Buf buf;
        if (Native.sz_world_legal(_world, &buf) != 0) throw Native.Failed("sz_world_legal");
        return [.. Encoding.UTF8.GetString(Native.Take(buf)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(CombatAction.Parse)];
    }

    public CombatStatus Status => Native.sz_world_status(_world) switch
    {
        0 => CombatStatus.InProgress,
        1 => CombatStatus.Won,
        2 => CombatStatus.Lost,
        _ => throw Native.Failed("sz_world_status"),
    };

    /// <exception cref="EngineException">The engine could not make the key.</exception>
    public ulong HiddenKey() => Native.Key(Native.sz_world_hidden_key(_world), "sz_world_hidden_key");

    /// <summary>The state as search tells states apart, without building the observation: what
    /// <c>PimcBrain.StateKey</c> is to the real combat's observation.</summary>
    /// <exception cref="EngineException">The engine could not make the key.</exception>
    public ulong StateKey() => Native.Key(Native.sz_world_state_key(_world), "sz_world_state_key");

    public void Execute(CombatAction action)
    {
        var text = Encoding.UTF8.GetBytes(action.ToString());
        int failed;
        fixed (byte* bytes = text) failed = Native.sz_world_apply(_world, bytes, (nuint)text.Length);
        if (failed != 0) throw Native.Failed($"sz_world_apply {action}");
    }

    public WorldSnapshot Snapshot()
    {
        var snapshot = new WorldSnapshot([]);
        _snapshots.Add(snapshot, Copy(_world));
        return snapshot;
    }

    public void Restore(WorldSnapshot snapshot)
    {
        if (!_snapshots.TryGetValue(snapshot, out var kept)) throw new InvalidOperationException("the snapshot is not one this world took");
        var world = Copy(kept);
        _world.Dispose();
        _world = world;
    }

    /// <summary>Frees the world and every snapshot of it still alive.</summary>
    public void Dispose()
    {
        _world.Dispose();
        foreach (var (_, kept) in _snapshots) kept.Dispose();
        _snapshots.Clear();
    }
}
