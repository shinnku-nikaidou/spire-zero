using System.Runtime.CompilerServices;
using Sts2Ai.SimWorker;

namespace Sts2Ai.SimWorker.Tests;

internal static class GameAssembliesSetup
{
    /// <summary>As the sim's Main: the game's assemblies resolve from the snapshot, should a test reach a game type.</summary>
    [ModuleInitializer]
    internal static void ResolveGameAssemblies() => GameAssemblies.InstallResolver();
}
