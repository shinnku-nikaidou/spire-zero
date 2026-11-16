using System.Text;
using Sts2Ai.Protocol;

namespace Sts2Ai.Engine;

/// <summary>
/// A combat in the engine, as search uses a world. It is made from a live combat as the bridge wrote it out
/// (<see cref="Import"/>, the SL world; <see cref="Sample"/>, a NoSL world), or from another world (<see cref="Clone"/>);
/// worlds share nothing, so each may be searched in a thread of its own. Each state of a search is one native world
/// that never changes: an action makes the world after it from the one before (<c>sz_world_step</c>), which a snapshot
/// keeps as it stands, and the step says the state's status and keys with it. A native world is freed when nothing stands on it any more: the world has moved on and
/// every snapshot taken there is forgotten (<see cref="Forget"/>), or the world is disposed.
/// </summary>
public sealed unsafe class EngineWorld : ICombatWorld, IDisposable
{
    /// <summary>A native world, how many stand on it (this world while it is there, and every snapshot taken there),
    /// and what the step that made it said of it; none for a world taken in or copied.</summary>
    private sealed class Place(WorldHandle handle, Native.Node? node = null)
    {
        public readonly WorldHandle Handle = handle;
        public readonly Native.Node? Node = node;
        public int Held = 1;
    }

    /// <summary>By the snapshot object itself: two snapshots are equal as records.</summary>
    private readonly Dictionary<WorldSnapshot, Place> _snapshots = new(ReferenceEqualityComparer.Instance);
    private readonly RunView _run;
    private readonly long _weight;
    private readonly RunChanges _changes;
    /// <summary>Whether the world is a NoSL sample (<see cref="Sample"/>).</summary>
    private readonly bool _sample;
    private Place _at;
    /// <summary>The observation last made where the world stands now, and its text's hash; none once the world has moved.</summary>
    private CombatObservation? _shown;
    private ulong _shownHash;

    private EngineWorld(WorldHandle world, RunView run, long weight, RunChanges changes, bool sample)
    {
        _at = new(world);
        _run = run;
        _weight = weight;
        _changes = changes;
        _sample = sample;
    }

    /// <summary>The SL world: the real combat, exact. Its states are told apart by the full hidden state
    /// (<c>HiddenState.Full</c>).</summary>
    /// <param name="export">The live combat as <c>EngineExport</c> wrote it.</param>
    /// <param name="run">The run around the combat, as it stood at the export: <see cref="Run"/> is this with what the
    /// combat has changed since.</param>
    /// <exception cref="EngineException">The engine cannot take the combat in.</exception>
    public static EngineWorld Import(ReadOnlySpan<byte> export, RunView run) => Take(export, run, sample: false);

    /// <summary>
    /// A NoSL world: a combat built to show what the player sees and determinized, one sample of what they cannot see,
    /// as the replay's NoSL world is. Its states are told apart by what the observation hides that changes how plays
    /// turn out (<c>HiddenState.Key</c>), and its run keeps <paramref name="run"/>'s deck: the built combat's own run
    /// holds the combat's piles as its deck, not the real one.
    /// </summary>
    /// <param name="export">The built combat as <c>EngineExport</c> wrote it.</param>
    /// <param name="run">The real run around the combat.</param>
    /// <exception cref="EngineException">The engine cannot take the combat in.</exception>
    public static EngineWorld Sample(ReadOnlySpan<byte> export, RunView run) => Take(export, run, sample: true);

    private static EngineWorld Take(ReadOnlySpan<byte> export, RunView run, bool sample)
    {
        WorldHandle world;
        fixed (byte* json = export) world = Native.sz_world_import(json, (nuint)export.Length, sample ? 1 : 0);
        if (world.IsInvalid) throw Native.Failed("sz_world_import");
        return new(world.Weighing(export.Length), run, export.Length, new(), sample);
    }

    /// <summary>An independent world standing where this one stands.</summary>
    public EngineWorld Clone() => new(Copy(_at.Handle), _run, _weight, _changes, _sample);

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
        if (Native.sz_world_observe(_at.Handle, &buf) != 0) throw Native.Failed("sz_world_observe");
        return Encoding.UTF8.GetString(Native.Take(buf));
    }

    /// <summary>The observation read from the engine's text, whose hash is kept with it (<see cref="Hash"/>).</summary>
    public CombatObservation Observe()
    {
        Native.Buf buf;
        if (Native.sz_world_observe(_at.Handle, &buf) != 0) throw Native.Failed("sz_world_observe");
        var text = Native.Take(buf);
        _shownHash = ProtocolJson.HashOfJson(text);
        return _shown = ProtocolJson.Deserialize<CombatObservation>(text);
    }

    /// <summary>The hash of the observation's text as the engine wrote it, which is the text
    /// <see cref="ProtocolJson"/> writes of the observation: the same number without writing it again.</summary>
    public ulong Hash(CombatObservation observation) =>
        ReferenceEquals(observation, _shown) ? _shownHash : ProtocolJson.Hash(observation);

    /// <summary>The engine's own key of the state the observation was made at, while the world still stands there.</summary>
    public ulong StateKey(CombatObservation observation) =>
        ReferenceEquals(observation, _shown) ? StateKey() : ProtocolJson.StateKey(observation);

    /// <summary>The run given at the import with what the combat has changed of it since (<see cref="RunChange"/>);
    /// a NoSL sample's keeps the deck given. The deck and the relics are read once for as long as the engine writes
    /// them the same, for all the worlds of one import: the same lists come back, so a leaf that priced one run knows
    /// the next for the same but its HP and potions.</summary>
    public RunView Run()
    {
        Native.Buf buf;
        if (Native.sz_world_run(_at.Handle, &buf) != 0) throw Native.Failed("sz_world_run");
        var run = _changes.Over(_run, Native.Take(buf));
        return _sample ? run with { Deck = _run.Deck } : run;
    }

    public IReadOnlyList<CombatAction> Legal()
    {
        Native.Buf buf;
        if (Native.sz_world_legal(_at.Handle, &buf) != 0) throw Native.Failed("sz_world_legal");
        return [.. Encoding.UTF8.GetString(Native.Take(buf)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(CombatAction.Parse)];
    }

    public CombatStatus Status => (_at.Node?.Status ?? Native.sz_world_status(_at.Handle)) switch
    {
        0 => CombatStatus.InProgress,
        1 => CombatStatus.Won,
        2 => CombatStatus.Lost,
        _ => throw Native.Failed("sz_world_status"),
    };

    /// <exception cref="EngineException">The engine could not make the key.</exception>
    public ulong HiddenKey() => _at.Node is { Status: 0 } node ? node.HiddenKey
        : Native.Key(Native.sz_world_hidden_key(_at.Handle), "sz_world_hidden_key");

    /// <summary>The state as search tells states apart, without building the observation: what
    /// <c>PimcBrain.StateKey</c> is to the real combat's observation.</summary>
    /// <exception cref="EngineException">The engine could not make the key.</exception>
    public ulong StateKey() => _at.Node is { Status: 0 } node ? node.StateKey
        : Native.Key(Native.sz_world_state_key(_at.Handle), "sz_world_state_key");

    /// <summary>The world after the action is made from the one before, which stays for any snapshot taken there and
    /// is let go if there is none.</summary>
    public void Execute(CombatAction action)
    {
        var text = Encoding.UTF8.GetBytes(action.ToString());
        Native.Node node;
        WorldHandle after;
        fixed (byte* bytes = text) after = Native.sz_world_step(_at.Handle, bytes, (nuint)text.Length, &node);
        if (after.IsInvalid) throw Native.Failed($"sz_world_step {action}");
        var before = _at;
        _at = new(after.Weighing(_weight), node);
        _shown = null;
        Leave(before);
    }

    public WorldSnapshot Snapshot()
    {
        var snapshot = new WorldSnapshot([]);
        _snapshots.Add(snapshot, _at);
        _at.Held++;
        return snapshot;
    }

    public void Restore(WorldSnapshot snapshot)
    {
        var kept = Kept(snapshot);
        _shown = null;
        if (kept == _at) return;
        kept.Held++;
        Leave(_at);
        _at = kept;
    }

    public void Forget(WorldSnapshot snapshot)
    {
        var kept = Kept(snapshot);
        _snapshots.Remove(snapshot);
        Leave(kept);
    }

    private Place Kept(WorldSnapshot snapshot) => _snapshots.TryGetValue(snapshot, out var kept) ? kept
        : throw new InvalidOperationException("the snapshot is not one this world took, or it was forgotten");

    /// <summary>One fewer stands on the place: with none left, its native world is freed.</summary>
    private static void Leave(Place place)
    {
        if (--place.Held == 0) place.Handle.Dispose();
    }

    /// <summary>Frees the world and every snapshot of it not forgotten.</summary>
    public void Dispose()
    {
        _at.Handle.Dispose();
        foreach (var kept in _snapshots.Values) kept.Handle.Dispose();
        _snapshots.Clear();
    }
}
