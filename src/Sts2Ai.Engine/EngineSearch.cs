using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Ai.Protocol;

namespace Sts2Ai.Engine;

/// <summary>
/// The combat net loaded in the engine, for the SL planner's turn search there (plans/2026-11-18-2132-sl-search-rust.md,
/// B; <see cref="EngineWorld.SearchTurn"/>): its graph run by the same ONNX Runtime the C# side runs, its endings priced
/// by V, which stays on the C# side and is asked from the thread that searches. Used from any number of threads at once.
/// </summary>
public sealed unsafe class EngineNet : IDisposable
{
    internal NetHandle Handle { get; }

    private EngineNet(NetHandle handle) => Handle = handle;

    /// <summary>The net of a combat net file: its graph (ONNX) and the rest of the file as JSON.</summary>
    /// <exception cref="EngineException">The engine cannot load it.</exception>
    public static EngineNet Load(byte[] graph, string file)
    {
        byte[] text = Encoding.UTF8.GetBytes(file), ort = Encoding.UTF8.GetBytes(OnnxRuntime.Path);
        NetHandle net;
        fixed (byte* g = graph)
        fixed (byte* f = text)
        fixed (byte* o = ort)
            net = Native.sz_net_load(g, (nuint)graph.Length, f, (nuint)text.Length, o, (nuint)ort.Length);
        return net.IsInvalid ? throw Native.Failed("sz_net_load") : new(net);
    }

    public void Dispose() => Handle.Dispose();
}

/// <summary>Where the ONNX Runtime library the C# side runs is: beside the program (a published brain), or under its
/// runtimes folder (a build).</summary>
internal static class OnnxRuntime
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        string name = OperatingSystem.IsWindows() ? "onnxruntime.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so";
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string[] places =
        [
            System.IO.Path.Combine(AppContext.BaseDirectory, name),
            System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", $"{os}-{arch}", "native", name),
        ];
        return places.FirstOrDefault(File.Exists) ?? throw new DllNotFoundException($"no ONNX Runtime library at {string.Join(" or ", places)}");
    }
}

/// <summary>What the engine's beam over turns found (<see cref="EngineWorld.Plan"/>): the line picked, the value of the
/// turn start a losing pick died from, the best line after each first action in the order first found, whether the pick
/// is proved, and the nodes and turns searched; <c>SlPlanner.Picked</c> makes the plan of it.</summary>
public sealed record PlanFound(TurnEnd Pick, double From, IReadOnlyList<TurnEnd> ByFirst, bool Proved, int Nodes, int Turns);

/// <summary>
/// The SL planner's solver in the engine (<c>SlSolver</c>, moved there line for line;
/// plans/2026-11-18-2132-sl-search-rust.md, C): the rest of the combat searched a little at a time, from a root that
/// moves along as the combat goes on. It owns a world where its root stands, its turns searched from there in
/// <paramref name="threads"/> of the engine's threads, which ask V (<paramref name="value"/>) any number at once. One
/// thread uses it at a time.
/// </summary>
public sealed unsafe class EngineSolver : IDisposable
{
    private readonly SolverHandle _solver;
    private readonly EngineNet _net;
    private readonly Func<RunView, double> _value;
    private readonly int _threads;
    private readonly EngineWorld _root;

    /// <param name="root">Where the solver begins; the solver searches from a copy of it.</param>
    /// <param name="history">The record of enemy moves there.</param>
    /// <param name="decay">The pick's turn decay.</param>
    public EngineSolver(EngineNet net, Func<RunView, double> value, EngineWorld root, IReadOnlyList<EnemyTurns> history, double decay, int threads,
        int batch = 16)
    {
        var text = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(history));
        fixed (byte* bytes = text) _solver = Native.sz_solver_new(bytes, (nuint)text.Length, decay, batch);
        if (_solver.IsInvalid) throw Native.Failed("sz_solver_new");
        (_net, _value, _threads, _root) = (net, value, threads, root.Clone());
    }

    /// <summary>A line another search found from the root, with the value of the turn start a losing line died from.</summary>
    public void Seed(TurnEnd line, double from)
    {
        var text = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(line));
        fixed (byte* bytes = text)
            if (Native.sz_solver_seed(_solver, bytes, (nuint)text.Length, from) != 0) throw Native.Failed("sz_solver_seed");
    }

    /// <summary>Searches on for at least <paramref name="nodes"/> more actions, or until nothing is left.</summary>
    public void Run(long nodes, CancellationToken stop = default) =>
        Searching(stop, "sz_solver_run", (ctx, halt) => Native.sz_solver_run(_solver, _net.Handle, _root.Handle, nodes, _root.Base, Pricer.Callback, ctx,
            _threads, (int*)halt));

    /// <summary>The combat went on from the root by <paramref name="played"/>, to where its record of enemy moves is
    /// <paramref name="moves"/>: the root moves there.</summary>
    public void Advance(IReadOnlyList<CombatAction> played, IReadOnlyList<EnemyTurns> moves, CancellationToken stop = default)
    {
        if (played.Count == 0) return;
        foreach (var action in played) _root.Execute(action);
        var asked = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(new SolverMoved(played, moves)));
        fixed (byte* bytes = asked)
        {
            nint at = (nint)bytes;
            Searching(stop, "sz_solver_advance", (ctx, halt) => Native.sz_solver_advance(_solver, _net.Handle, _root.Handle, (byte*)at,
                (nuint)asked.Length, _root.Base, Pricer.Callback, ctx, _threads, (int*)halt));
        }
    }

    /// <summary>The pick so far, for <c>SlPlanner.Picked</c>.</summary>
    public PlanFound Result()
    {
        Native.Buf buf;
        if (Native.sz_solver_result(_solver, &buf) != 0) throw Native.Failed("sz_solver_result");
        return ProtocolJson.Deserialize<PlanFound>(Native.Take(buf));
    }

    /// <summary>The actions simulated so far, the turns searched, the states held, the states left, and whether the pick
    /// is proved.</summary>
    public (long Nodes, int Searched, int States, int Left, bool Proved) Counts
    {
        get
        {
            var counts = stackalloc long[5];
            if (Native.sz_solver_counts(_solver, counts) != 0) throw Native.Failed("sz_solver_counts");
            return (counts[0], (int)counts[1], (int)counts[2], (int)counts[3], counts[4] != 0);
        }
    }

    private void Searching(CancellationToken stop, string call, Func<nint, nint, int> search)
    {
        var flag = new int[1];
        using var told = stop.Register(() => Volatile.Write(ref flag[0], 1));
        new Pricer(_root.RunOf, _value).With(ctx =>
        {
            int said;
            fixed (int* halt = flag) said = search(ctx, (nint)halt);
            if (said == 2) throw new OperationCanceledException(stop);
            return said == 0 ? 0 : throw Native.Failed(call);
        });
    }

    public void Dispose()
    {
        _solver.Dispose();
        _root.Dispose();
    }
}

/// <summary>What stands after a path of answers from a plan's card prompt, crossed on the node's clone
/// (<see cref="EngineCrossing"/>): a prompt or the combat's end, with the run and the hidden key there; or where the
/// player can play again, also the combat written out for the engine.</summary>
public sealed record CrossedPlace(CombatStatus Status, CombatObservation Observation, RunView Run, ulong HiddenKey, byte[]? Export);

/// <summary>
/// The SL planner's beam in the engine from a card prompt the node's clone stands at (plans/2026-11-18-2132-sl-search-rust.md,
/// C): the engine's search asks <c>cross</c> what each path of answers leads to, from any of its threads, until the
/// player can play again, and takes the combat in there.
/// </summary>
public static unsafe class EngineCrossing
{
    /// <summary>The decimals as strings, so that the engine reads them with their scale and writes the same text.</summary>
    private static readonly JsonSerializerOptions Lossless = new(ProtocolJson.Options) { Converters = { new DecimalText() } };

    private sealed class DecimalText : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    private sealed record Head(int Status, ulong HiddenKey, CombatObservation Observation, RunChange Run);

    /// <summary>The paths' crossing and the first failure in it, which is thrown once the engine's call is back.</summary>
    private sealed class Crosser(Func<IReadOnlyList<CombatAction>, CrossedPlace> cross)
    {
        public readonly Func<IReadOnlyList<CombatAction>, CrossedPlace> Cross = cross;
        public ExceptionDispatchInfo? Failed;
    }

    /// <param name="run">The run at the plan's prompt, which what the combat changes is priced over.</param>
    public static PlanFound Plan(EngineNet net, Func<RunView, double> value, RunView run, Func<IReadOnlyList<CombatAction>, CrossedPlace> cross,
        IReadOnlyList<EnemyTurns> history, int nodesPerWorld, int beam, int easyBeam, double turnDecay, int threads, CancellationToken stop = default)
    {
        var asked = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(new PlanAsked(history, nodesPerWorld, beam, easyBeam, turnDecay)));
        var flag = new int[1];
        using var told = stop.Register(() => Volatile.Write(ref flag[0], 1));
        var changes = new RunChanges();
        var crosser = new Crosser(cross);
        var handle = GCHandle.Alloc(crosser);
        try
        {
            return new Pricer(change => changes.Over(run, change), value).With(ctx =>
            {
                Native.Buf buf;
                int said;
                fixed (byte* bytes = asked)
                fixed (int* halt = flag)
                    said = Native.sz_plan_crossed(net.Handle, &Crossing, GCHandle.ToIntPtr(handle), bytes, (nuint)asked.Length, ProtocolJson.Hash(run),
                        Pricer.Callback, ctx, threads, halt, &buf);
                crosser.Failed?.Throw();
                if (said == 2) throw new OperationCanceledException(stop);
                if (said != 0) throw Native.Failed("sz_plan_crossed");
                return ProtocolJson.Deserialize<PlanFound>(Native.Take(buf));
            });
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int Crossing(nint ctx, byte* path, nuint len, nint sink)
    {
        var crosser = (Crosser)GCHandle.FromIntPtr(ctx).Target!;
        try
        {
            var answers = JsonSerializer.Deserialize<string[]>(new ReadOnlySpan<byte>(path, checked((int)len)))!;
            var place = crosser.Cross([.. answers.Select(CombatAction.Parse)]);
            var head = JsonSerializer.SerializeToUtf8Bytes(new Head((int)place.Status, place.HiddenKey, place.Observation, RunChange.Of(place.Run)), Lossless);
            fixed (byte* bytes = head) Native.sz_sink_put(sink, bytes, (nuint)head.Length);
            if (place.Export is { } export)
            {
                byte line = (byte)'\n';
                Native.sz_sink_put(sink, &line, 1);
                fixed (byte* bytes = export) Native.sz_sink_put(sink, bytes, (nuint)export.Length);
            }
            return 0;
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref crosser.Failed, ExceptionDispatchInfo.Capture(e), null);
            return 1;
        }
    }
}

/// <summary>Where a solver's root moved, as <c>search/read.rs</c> reads it.</summary>
internal sealed record SolverMoved(IReadOnlyList<CombatAction> Played, IReadOnlyList<EnemyTurns> Moves);

/// <summary>A plan asked of the engine, as <c>search/read.rs</c> reads it.</summary>
internal sealed record PlanAsked(IReadOnlyList<EnemyTurns> History, int NodesPerWorld, int Beam, int EasyBeam, double TurnDecay);

/// <summary>A turn's search asked of the engine, as <c>search/read.rs</c> reads it.</summary>
internal sealed record TurnAsked(IReadOnlyList<CombatAction> Path, IReadOnlyList<EnemyTurns> History, int Nodes, int Keep,
    IReadOnlyDictionary<string, int>? LifeBounds);

/// <summary>V as a search in the engine asks it (<c>sz_price</c>): of the run given at a world's import with the change
/// the engine writes over it. A failure of V is kept and thrown once the engine's call is back.</summary>
internal sealed unsafe class Pricer(Pricer.RunOf run, Func<RunView, double> value)
{
    /// <summary>The run a change the engine wrote stands for.</summary>
    public delegate RunView RunOf(ReadOnlySpan<byte> change);

    private readonly RunOf _run = run;
    private readonly Func<RunView, double> _value = value;

    private ExceptionDispatchInfo? _failed;

    /// <summary>Runs <paramref name="call"/> with the context the engine hands back to <see cref="Price"/>; a failure of
    /// V inside it is thrown here.</summary>
    public T With<T>(Func<nint, T> call)
    {
        var handle = GCHandle.Alloc(this);
        try
        {
            var result = call(GCHandle.ToIntPtr(handle));
            _failed?.Throw();
            return result;
        }
        finally
        {
            handle.Free();
        }
    }

    public static delegate* unmanaged<nint, byte*, nuint, double*, int> Callback => &Price;

    [UnmanagedCallersOnly]
    private static int Price(nint ctx, byte* json, nuint len, double* value)
    {
        var pricer = (Pricer)GCHandle.FromIntPtr(ctx).Target!;
        try
        {
            *value = pricer._value(pricer._run(new ReadOnlySpan<byte>(json, checked((int)len))));
            return 0;
        }
        catch (Exception e)
        {
            // V may be asked from several of the engine's threads at once: the first failure is the one thrown.
            Interlocked.CompareExchange(ref pricer._failed, ExceptionDispatchInfo.Capture(e), null);
            return 1;
        }
    }
}
