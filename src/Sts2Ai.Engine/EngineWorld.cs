using System.Text;
using Sts2Ai.Protocol;

namespace Sts2Ai.Engine;

/// <summary>
/// A combat in the engine, as search uses a world. It is made from a live combat as the bridge wrote it out
/// (<see cref="Import"/>, the SL world), from that one with what the player cannot see drawn again
/// (<see cref="Determinize"/>, a NoSL world), or from another world (<see cref="Clone"/>);
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
    /// <summary>The run given at the import, hashed: what tells apart the runs the engine's net prices over.</summary>
    private readonly Lazy<ulong> _base;
    private Place _at;
    /// <summary>The observation last made where the world stands now, and its text's hash; none once the world has moved.</summary>
    private CombatObservation? _shown;
    private ulong _shownHash;

    private EngineWorld(WorldHandle world, RunView run, long weight, RunChanges changes, Lazy<ulong>? runHash = null)
    {
        _at = new(world);
        _run = run;
        _weight = weight;
        _changes = changes;
        _base = runHash ?? new(() => ProtocolJson.Hash(run));
    }

    /// <summary>The SL world: the real combat, exact. Its states are told apart by the full hidden state
    /// (<c>HiddenState.Full</c>).</summary>
    /// <param name="export">The live combat as <c>EngineExport</c> wrote it.</param>
    /// <param name="run">The run around the combat, as it stood at the export: <see cref="Run"/> is this with what the
    /// combat has changed since.</param>
    /// <exception cref="EngineException">The engine cannot take the combat in.</exception>
    public static EngineWorld Import(ReadOnlySpan<byte> export, RunView run)
    {
        WorldHandle world;
        fixed (byte* json = export) world = Native.sz_world_import(json, (nuint)export.Length, 0);
        if (world.IsInvalid) throw Native.Failed("sz_world_import");
        return new(world.Weighing(export.Length), run, export.Length, new());
    }

    /// <summary>The SL world of a combat written out as it begins, before its first turn (the host's export in the game's
    /// StartCombatInternal, once the executor has finished what it ran: the bridge's CombatOpening), where the combat's start has run: where the player is first asked, at a card
    /// prompt that opens the combat or at the first turn's plays (world.rs, <c>Live::begin</c>).</summary>
    /// <param name="run">The run around the combat, as it stood at the export.</param>
    /// <exception cref="EngineException">The engine cannot take the combat in, or its start failed.</exception>
    public static EngineWorld Begin(ReadOnlySpan<byte> export, RunView run)
    {
        WorldHandle world;
        fixed (byte* json = export) world = Native.sz_world_begin(json, (nuint)export.Length);
        if (world.IsInvalid) throw Native.Failed("sz_world_begin");
        return new(world.Weighing(export.Length), run, export.Length, new());
    }

    /// <summary>The draw pile's public record (JSON, as an export's <c>sim.draw_log</c>) as the host holds it where this
    /// world stands now, having taken the actions since its export as the game did: what a NoSL world drawn here reads
    /// (<see cref="Pimc"/> at a card prompt).</summary>
    public void DrawLog(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        fixed (byte* text = bytes)
            if (Native.sz_world_draw_log(_at.Handle, text, (nuint)bytes.Length) != 0) throw Native.Failed("sz_world_draw_log");
    }

    /// <summary>An independent world standing where this one stands.</summary>
    public EngineWorld Clone() => new(Copy(_at.Handle), _run, _weight, _changes, _base);

    /// <summary>
    /// A NoSL world: this combat with everything the player cannot see drawn again from <paramref name="seed"/> (the
    /// engine's determinize: every random stream, the run's seeds, the relic bag, the draw pile's order from its
    /// public record, the queued moves that look alike), one sample of what they cannot see. Its states are told apart
    /// by what the observation hides that changes how plays turn out (<c>HiddenState.Key</c>). A world at a card prompt
    /// is drawn where the prompt takes its answer, from the record <see cref="DrawLog"/> set: what the prompt shows stays
    /// the real combat's. This world stays as it is.
    /// </summary>
    /// <exception cref="EngineException">The engine cannot draw the world: its record of the draw pile does not
    /// hold.</exception>
    public EngineWorld Determinize(ulong seed)
    {
        var sample = Native.sz_world_determinize(_at.Handle, seed);
        return sample.IsInvalid ? throw Native.Failed("sz_world_determinize") : new(sample.Weighing(_weight), _run, _weight, _changes, _base);
    }

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

    /// <summary>The run given at the import with what the combat has changed of it since (<see cref="RunChange"/>).
    /// The deck and the relics are read once for as long as the engine writes
    /// them the same, for all the worlds of one import: the same lists come back, so a leaf that priced one run knows
    /// the next for the same but its HP and potions.</summary>
    public RunView Run()
    {
        Native.Buf buf;
        if (Native.sz_world_run(_at.Handle, &buf) != 0) throw Native.Failed("sz_world_run");
        return _changes.Over(_run, Native.Take(buf));
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
    public void Execute(CombatAction action) => Step(action.ToString());

    /// <summary>Answers the card prompt the world stands at with places in the game's own list of its options, as a
    /// host's record of its steps has them (the simulator's trace: <c>card_choice 2,0</c>), not the canonical order's.</summary>
    public void Answer(IReadOnlyList<int> given) => Step("given:" + string.Join(",", given));

    private void Step(string action)
    {
        var text = Encoding.UTF8.GetBytes(action);
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

    /// <summary>
    /// The SL planner's beam over turns in the engine (search/beam.rs) from where
    /// this world stands, with the budget's members of these names, its turns searched in <paramref name="threads"/> of the
    /// engine's threads, which ask V (<paramref name="value"/>) any number at once. This world stays where it stands.
    /// Once <paramref name="stop"/> is cancelled the search stops where it stands with an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="EngineException">The search failed in the engine.</exception>
    public PlanFound Plan(EngineNet net, Func<RunView, double> value, IReadOnlyList<EnemyTurns> history, int nodesPerWorld, int beam, int easyBeam,
        double turnDecay, int threads, CancellationToken stop = default)
    {
        var asked = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(new PlanAsked(history, nodesPerWorld, beam, easyBeam, turnDecay)));
        var flag = new int[1];
        using var told = stop.Register(() => Volatile.Write(ref flag[0], 1));
        return new Pricer(RunOf, value).With(ctx =>
        {
            Native.Buf buf;
            int said;
            fixed (byte* bytes = asked)
            fixed (int* halt = flag)
                said = Native.sz_plan(net.Handle, _at.Handle, bytes, (nuint)asked.Length, _base.Value, Pricer.Callback, ctx, threads, halt, &buf);
            if (said == 2) throw new OperationCanceledException(stop);
            if (said != 0) throw Native.Failed("sz_plan");
            return ProtocolJson.Deserialize<PlanFound>(Native.Take(buf));
        });
    }

    /// <summary>
    /// NoSL's search of a decision in the engine (search/pimc.rs, C#: PimcBrain): <paramref name="worlds"/> worlds drawn
    /// from the real combat this world is, the k-th from <c>world_seed(seed, k)</c> (at a card prompt, drawn where the
    /// prompt takes its answer, from the record <see cref="DrawLog"/> set), each searched for the best line
    /// after each of <paramref name="first"/> with <paramref name="nodesPerFirst"/> nodes, in
    /// <paramref name="threads"/> of the engine's threads, which ask V (<paramref name="value"/>) any number at once;
    /// every state is read in NoSL where <paramref name="nosl"/>. The lines by world, in world order, then by first
    /// action. This world stays where it stands.
    /// </summary>
    /// <exception cref="EngineException">The search failed in the engine.</exception>
    public IReadOnlyList<IReadOnlyList<PlannedLine>> Pimc(EngineNet net, Func<RunView, double> value, IReadOnlyList<EnemyTurns> history,
        IReadOnlyList<CombatAction> first, int nodesPerFirst, int worlds, ulong seed, bool nosl, int threads)
    {
        var asked = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(new PimcAsked(history, [.. first.Select(a => a.ToString())], nodesPerFirst, worlds, seed)));
        return new Pricer(RunOf, value).With(ctx =>
        {
            Native.Buf buf;
            int said;
            fixed (byte* bytes = asked)
                said = Native.sz_pimc(net.Handle, _at.Handle, bytes, (nuint)asked.Length, _base.Value, Pricer.Callback, ctx, nosl ? 1 : 0, threads, &buf);
            if (said != 0) throw Native.Failed("sz_pimc");
            return ProtocolJson.Deserialize<PlannedLine[][]>(Native.Take(buf));
        });
    }

    /// <summary>The engine's net's outcome of the state the world stands at (<c>CombatNet.Outcome</c>): the value, the
    /// chance to win and the HP held at a win, with V by <paramref name="value"/>.</summary>
    public (double Value, double Win, double Hp) Outcome(EngineNet net, Func<RunView, double> value, IReadOnlyList<EnemyTurns> history)
    {
        var text = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(history));
        return new Pricer(RunOf, value).With(ctx =>
        {
            var outcome = stackalloc double[3];
            int said;
            fixed (byte* bytes = text)
                said = Native.sz_net_outcome(net.Handle, _at.Handle, bytes, (nuint)text.Length, _base.Value, Pricer.Callback, ctx, outcome);
            return said == 0 ? (outcome[0], outcome[1], outcome[2]) : throw Native.Failed("sz_net_outcome");
        });
    }

    internal RunView RunOf(ReadOnlySpan<byte> change) => _changes.Over(_run, change);

    /// <summary>The native world where this one stands, for the search's calls.</summary>
    internal WorldHandle Handle => _at.Handle;

    /// <summary>The run given at the import, hashed (<see cref="_base"/>).</summary>
    internal ulong Base => _base.Value;

    /// <summary>Frees the world and every snapshot of it not forgotten.</summary>
    public void Dispose()
    {
        _at.Handle.Dispose();
        foreach (var kept in _snapshots.Values) kept.Handle.Dispose();
        _snapshots.Clear();
    }
}
