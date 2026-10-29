using System.Reflection;
using System.Runtime.Loader;

namespace Sts2Ai.SimWorker;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Must run before any method that mentions a game type is compiled; Cli.Run is NoInlining for that reason.
        GameAssemblies.InstallResolver();
        return Cli.Run(args);
    }

    /// <summary>The commit this program was built from, as the build stamped it.</summary>
    public static readonly string Commit =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').ElementAtOrDefault(1) ?? "unknown";

    /// <summary>How to start this program again: its apphost, or dotnet with its dll if that is how it was started.</summary>
    public static IReadOnlyList<string> SelfCommand()
    {
        var exe = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(exe) == "dotnet" ? [exe, typeof(Program).Assembly.Location] : [exe];
    }
}

/// <summary>
/// Loads the game's own assemblies (sts2, GodotSharp, Harmony, ...) from the snapshot directory.
/// Framework assemblies still come from the shared .NET runtime: Resolving only fires when default probing fails.
/// </summary>
internal static class GameAssemblies
{
    public static string BinDir { get; private set; } = "";

    public static void InstallResolver()
    {
        BinDir = Environment.GetEnvironmentVariable("STS2_BIN_DIR")
            ?? typeof(GameAssemblies).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "Sts2BinDir").Value!;
        if (!File.Exists(Path.Combine(BinDir, "sts2.dll")))
            throw new FileNotFoundException($"sts2.dll not found in {BinDir} (set STS2_BIN_DIR)");

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(BinDir, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    /// <summary>
    /// Game assemblies (and game-side projects) that <paramref name="root"/> references, directly or through our
    /// own assemblies. Compiled references only list what the code actually uses.
    /// </summary>
    public static List<string> ReachedFrom(Assembly root)
    {
        string[] forbidden = ["sts2", "GodotSharp", "0Harmony", "SmartFormat", "Sts2Ai.GameBridge", "Sts2Ai.SimWorker"];
        var reached = new List<string>();
        var seen = new HashSet<string> { root.GetName().Name! };
        var pending = new Stack<AssemblyName>(root.GetReferencedAssemblies());
        while (pending.TryPop(out var name))
        {
            if (!seen.Add(name.Name!)) continue;
            if (forbidden.Contains(name.Name)) reached.Add(name.Name!);
            else if (name.Name!.StartsWith("Sts2Ai.", StringComparison.Ordinal))
                foreach (var r in Assembly.Load(name).GetReferencedAssemblies()) pending.Push(r);
        }
        return reached;
    }
}
