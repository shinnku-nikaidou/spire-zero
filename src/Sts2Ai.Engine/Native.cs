using System.Runtime.InteropServices;
using System.Text;

namespace Sts2Ai.Engine;

/// <summary>Where the engine's files are: the one place that says.</summary>
public static class EngineLibrary
{
    /// <summary>The variable that names the library file, for a build that does not sit beside the program (a
    /// developer's <c>engine/target/release</c>).</summary>
    public const string Variable = "STS2AI_ENGINE_LIB";

    /// <summary>The variable that names the schema file, for the same case (a developer's <c>gen/schema.json</c>).</summary>
    public const string SchemaVariable = "STS2AI_ENGINE_SCHEMA";

    /// <summary>The library file: the one <see cref="Variable"/> names, or else the one beside the program, under the
    /// platform's name for it.</summary>
    public static string Path { get; } = Environment.GetEnvironmentVariable(Variable) ?? System.IO.Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "sts2ai_engine.dll" : OperatingSystem.IsMacOS() ? "libsts2ai_engine.dylib" : "libsts2ai_engine.so");

    /// <summary>The schema the library's state was made from, which a live combat is written out along: the file
    /// <see cref="SchemaVariable"/> names, or else the one beside the program.</summary>
    public static string SchemaPath { get; } = Environment.GetEnvironmentVariable(SchemaVariable)
        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "sts2ai_engine.schema.json");
}

/// <summary>The engine failed: what it said (<c>sz_last_error</c>).</summary>
public sealed class EngineException(string message) : Exception(message);

/// <summary>
/// The C ABI of <c>libsts2ai_engine</c>, as engine/DESIGN.md gives it. A world is an opaque 64-bit handle, passed here
/// as a pointer-sized one, which is the same thing on the 64-bit platforms the engine is built for. A call on one world
/// never touches another, so different worlds may be used from different threads; one world from one thread at a time.
/// </summary>
internal static unsafe partial class Native
{
    private const string Library = "sts2ai_engine";

    static Native()
    {
        if (nint.Size != 8) throw new PlatformNotSupportedException("the engine's handles are 64 bits wide");
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => name != Library ? 0
            : File.Exists(EngineLibrary.Path) ? NativeLibrary.Load(EngineLibrary.Path)
            : throw new DllNotFoundException($"no engine library at {EngineLibrary.Path} ({EngineLibrary.Variable} names another)"));
    }

    /// <summary><c>sz_buf</c>: bytes the engine allocated, to be given back with <see cref="sz_buf_free"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Buf
    {
        public byte* Ptr;
        public nuint Len;
    }

    /// <summary><c>sz_node</c>: what <see cref="sz_world_step"/> says of the world it made, which search reads of every
    /// state. The keys are 0 once the combat is over.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Node
    {
        public int Status;
        public int Round;
        public int Prompt;
        public ulong StateKey;
        public ulong HiddenKey;
    }

    [LibraryImport(Library)] internal static partial WorldHandle sz_world_import(byte* json, nuint len, int sample);
    [LibraryImport(Library)] internal static partial WorldHandle sz_world_clone(WorldHandle world);
    [LibraryImport(Library)] internal static partial void sz_world_free(nint world);
    [LibraryImport(Library)] internal static partial WorldHandle sz_world_step(WorldHandle world, byte* action, nuint len, Node* node);
    [LibraryImport(Library)] internal static partial int sz_world_status(WorldHandle world);
    [LibraryImport(Library)] internal static partial int sz_world_legal(WorldHandle world, Buf* result);
    [LibraryImport(Library)] internal static partial int sz_world_observe(WorldHandle world, Buf* result);
    [LibraryImport(Library)] internal static partial int sz_world_run(WorldHandle world, Buf* result);
    [LibraryImport(Library)] internal static partial ulong sz_world_hidden_key(WorldHandle world);
    [LibraryImport(Library)] internal static partial ulong sz_world_state_key(WorldHandle world);
    [LibraryImport(Library)] internal static partial void sz_buf_free(Buf buf);
    [LibraryImport(Library)] internal static partial int sz_last_error(Buf* result);

    /// <summary>The bytes of a buffer the engine filled, which is given back.</summary>
    internal static byte[] Take(Buf buf)
    {
        try
        {
            return new ReadOnlySpan<byte>(buf.Ptr, checked((int)buf.Len)).ToArray();
        }
        finally
        {
            sz_buf_free(buf);
        }
    }

    /// <summary>What the engine said of the last call on this thread: empty when that call did not fail.</summary>
    internal static string Said()
    {
        Buf buf;
        return sz_last_error(&buf) == 0 ? Encoding.UTF8.GetString(Take(buf)) : "";
    }

    /// <summary>What the engine said of the call that just failed on this thread.</summary>
    internal static EngineException Failed(string call) =>
        new(Said() is { Length: > 0 } said ? $"{call}: {said}" : $"{call} failed, and the engine does not say why");

    /// <summary>A key the engine gave. A key has no value that says "failed": the engine answers 0 then and says why,
    /// and a key that is 0 comes with nothing said.</summary>
    internal static ulong Key(ulong key, string call) =>
        key != 0 || Said() is not { Length: > 0 } said ? key : throw new EngineException($"{call}: {said}");
}

/// <summary>
/// A native world, freed when its world lets go of it or, for a world nobody disposed, when the collector finds it.
/// The collector cannot see the native memory behind a handle, so each one tells it how much that is, about: the
/// size of the export the world came from.
/// </summary>
internal sealed class WorldHandle : SafeHandle
{
    private long _weight;

    public WorldHandle() : base(0, ownsHandle: true) { }

    public override bool IsInvalid => handle == 0;

    /// <summary>Tells the collector this world holds about <paramref name="bytes"/> of native memory.</summary>
    public WorldHandle Weighing(long bytes)
    {
        if (bytes > 0) GC.AddMemoryPressure(_weight = bytes);
        return this;
    }

    protected override bool ReleaseHandle()
    {
        Native.sz_world_free(handle);
        if (_weight > 0) GC.RemoveMemoryPressure(_weight);
        return true;
    }
}
