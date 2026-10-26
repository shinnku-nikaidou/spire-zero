using System.Reflection;
using System.Runtime.InteropServices;

namespace Sts2Ai.Headless;

/// <summary>
/// GodotSharp reaches the engine through a table of native function pointers that the engine fills at startup.
/// With no engine, we fill every slot with one tiny function that returns a pointer to zeroed scratch memory.
/// Managed code that touches Godot (strings, nodes, singletons) then runs through without side effects.
/// Game rules must never depend on these results; if one does, it shows up as a sim-vs-real mismatch.
/// </summary>
internal static unsafe class GodotNativeStub
{
    private const int PageSize = 4096;
    private const int ProtReadWriteExec = 7;
    private const int MapPrivateAnonymous = 0x22;

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("the native stub is written for linux-x64");

        IntPtr page = mmap(IntPtr.Zero, PageSize, ProtReadWriteExec, MapPrivateAnonymous, -1, 0);
        if (page == IntPtr.Zero || page == new IntPtr(-1))
            throw new InvalidOperationException("mmap failed, errno " + Marshal.GetLastPInvokeError());

        // mov rax, imm64; ret. The returned pointer is the upper half of the page, so a caller that writes
        // through it cannot overwrite the code in the lower half.
        byte* code = (byte*)page;
        code[0] = 0x48;
        code[1] = 0xB8;
        *(long*)(code + 2) = (long)(code + PageSize / 2);
        code[10] = 0xC3;

        var nativeFuncs = typeof(Godot.GD).Assembly.GetType("Godot.NativeInterop.NativeFuncs", throwOnError: true)!;
        var table = nativeFuncs.GetNestedType("UnmanagedCallbacks", BindingFlags.Public | BindingFlags.NonPublic)!;
        int slots = table.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length;
        int size = slots * IntPtr.Size;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        for (int i = 0; i < slots; i++) ((IntPtr*)buffer)[i] = page;

        nativeFuncs.GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { buffer, size });
        _installed = true;
    }
}
