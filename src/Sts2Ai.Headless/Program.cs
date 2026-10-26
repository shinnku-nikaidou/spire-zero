using System.Reflection;
using System.Runtime.Loader;

namespace Sts2Ai.Headless;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Must run before any method that mentions a game type is compiled; Cli.Run is NoInlining for that reason.
        GameAssemblies.InstallResolver();
        return Cli.Run(args);
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
}
