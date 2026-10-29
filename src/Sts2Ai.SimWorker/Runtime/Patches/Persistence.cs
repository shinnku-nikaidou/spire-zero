using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Ai.SimWorker;

internal static partial class HeadlessPatches
{
    // ---- persistence: nothing is ever saved; the user's real saves must stay untouched ----

    /// <summary>Also tells <see cref="NodeEntered"/>: the save with no finished room is the one on arrival at a map point.</summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun))]
    private static class NoRunSave
    {
        private static bool Prefix(AbstractRoom? preFinishedRoom, ref Task __result)
        {
            if (preFinishedRoom == null) NodeEntered?.Invoke();
            __result = Task.CompletedTask;
            return false;
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.UpdateProgressAfterCombatWon))]
    private static class NoProgressUpdate
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveProgressFile))]
    private static class NoProgressSave
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.WriteReplay))]
    private static class NoReplay
    {
        private static bool Prefix() => false;
    }

    /// <summary>
    /// TestMode routes saves to an in-memory file system, so the real-disk file layer should never run.
    /// The save migrators are the only game code that uses System.IO directly. Any call here is a bug: fail loudly.
    /// </summary>
    [HarmonyPatch]
    private static class NoDiskAccess
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            new[]
            {
                typeof(GodotFileIo),
                typeof(MigrationUtil),
                typeof(ProfileAccountScopeMigrator),
                typeof(AccountScopeUserDataMigrator),
            }
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            // Property accessors only hold paths. CreateDirectory is handled below.
            .Where(m => !m.IsSpecialName && !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null)
            .Where(m => !(m.DeclaringType == typeof(GodotFileIo) && InertFileIo.Contains(m.Name)));

        private static readonly string[] InertFileIo =
            [nameof(GodotFileIo.CreateDirectory), nameof(GodotFileIo.FileExists), nameof(GodotFileIo.DirectoryExists)];

        private static bool Prefix(MethodBase __originalMethod) =>
            throw new InvalidOperationException($"headless: disk access blocked ({__originalMethod.DeclaringType?.Name}.{__originalMethod.Name})");
    }

    /// <summary>
    /// The null platform layer builds a GodotFileIo(".") (whose constructor creates the directory) and checks it
    /// for an optional multiplayer-names file. Headless, nothing exists and nothing is created.
    /// </summary>
    [HarmonyPatch(typeof(GodotFileIo), nameof(GodotFileIo.CreateDirectory))]
    private static class NoCreateDirectory
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(GodotFileIo), nameof(GodotFileIo.FileExists))]
    private static class NoFiles
    {
        private static bool Prefix(ref bool __result) { __result = false; return false; }
    }

    [HarmonyPatch(typeof(GodotFileIo), nameof(GodotFileIo.DirectoryExists))]
    private static class NoDirectories
    {
        private static bool Prefix(ref bool __result) { __result = false; return false; }
    }
}
