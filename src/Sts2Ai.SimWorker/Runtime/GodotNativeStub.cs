using System.Reflection;
using System.Runtime.InteropServices;

namespace Sts2Ai.SimWorker.Runtime;

/// <summary>
/// GodotSharp reaches the engine through a table of native function pointers that the engine fills at startup.
/// With no engine, we fill every slot with one tiny function that returns a pointer to zeroed scratch memory.
/// Managed code that touches Godot (strings, nodes, singletons) then runs through without side effects.
/// Game rules must never depend on these results; if one does, it shows up as a sim-vs-real mismatch.
/// The function is <see cref="Zeroed"/>, compiled as any C# method: the same on every platform, no code written at run time.
/// </summary>
internal static unsafe class GodotNativeStub
{
    private const int ScratchSize = 2048;

    private static bool _installed;
    private static IntPtr _scratch;

    public static void Install()
    {
        if (_installed) return;
        _scratch = (IntPtr)NativeMemory.AllocZeroed(ScratchSize);
        var stub = (IntPtr)(delegate* unmanaged<IntPtr>)&Zeroed;

        var nativeFuncs = typeof(Godot.GD).Assembly.GetType("Godot.NativeInterop.NativeFuncs", throwOnError: true)!;
        var table = nativeFuncs.GetNestedType("UnmanagedCallbacks", BindingFlags.Public | BindingFlags.NonPublic)!;
        int slots = table.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length;
        int size = slots * IntPtr.Size;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        for (int i = 0; i < slots; i++) ((IntPtr*)buffer)[i] = stub;

        nativeFuncs.GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [buffer, size]);
        _installed = true;
    }

    /// <summary>Every engine call's answer: the arguments, whatever they are, stay unread.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr Zeroed() => _scratch;
}
