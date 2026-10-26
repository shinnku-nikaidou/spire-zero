using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2Ai.SimWorker;

/// <summary>
/// Harmony patches that let the game's model layer run without the engine. Each patch replaces a call that
/// would otherwise reach Godot (rendering, assets, OS, files), persist data, or waste time. None of them changes
/// a game rule, except the "rule fidelity" group, which puts back the shipped rules where TestMode changed them.
/// </summary>
internal static class HeadlessPatches
{
    public static string GameVersion = "v0.0.0";

    /// <summary>How many times a Rolling Boulder hit was split into one hit per enemy (checked by selftest).</summary>
    public static int RollingBoulderSplits;

    public static void Apply() => new Harmony("sts2ai.simworker").PatchAll(typeof(HeadlessPatches).Assembly);

    // ---- engine and OS ----

    [HarmonyPatch(typeof(Logger), "GetIsRunningFromGodotEditor")]
    private static class NotInEditor
    {
        private static bool Prefix(ref bool __result) { __result = false; return false; }
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame.GetGameVersion))]
    private static class Version
    {
        private static bool Prefix(ref string __result) { __result = GameVersion; return false; }
    }

    [HarmonyPatch(typeof(Godot.OS), nameof(Godot.OS.GetCmdlineArgs))]
    private static class NoCmdlineArgs
    {
        private static bool Prefix(ref string[] __result) { __result = []; return false; }
    }

    [HarmonyPatch(typeof(Godot.OS), nameof(Godot.OS.HasFeature))]
    private static class NoFeatures
    {
        private static bool Prefix(ref bool __result) { __result = false; return false; }
    }

    [HarmonyPatch(typeof(Godot.OS), nameof(Godot.OS.GetExecutablePath))]
    private static class ExecutablePath
    {
        private static bool Prefix(ref string __result) { __result = Path.Combine(GameAssemblies.BinDir, "headless"); return false; }
    }

    [HarmonyPatch(typeof(Godot.Time), nameof(Godot.Time.GetTicksMsec))]
    private static class TicksMsec
    {
        private static bool Prefix(ref ulong __result) { __result = (ulong)Environment.TickCount64; return false; }
    }

    [HarmonyPatch(typeof(Godot.Time), nameof(Godot.Time.GetTicksUsec))]
    private static class TicksUsec
    {
        private static bool Prefix(ref ulong __result) { __result = (ulong)Environment.TickCount64 * 1000; return false; }
    }

    /// <summary>Engine singletons (OS, Time, Input, ...) become empty managed shells instead of native lookups.</summary>
    [HarmonyPatch]
    private static class EngineSingletons
    {
        private static MethodBase TargetMethod() =>
            typeof(Godot.GodotObject).Assembly.GetType("Godot.NativeInterop.InteropUtils", throwOnError: true)!
                .GetMethod("EngineGetSingleton", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

        private static bool Prefix(string name, ref Godot.GodotObject __result)
        {
            var type = typeof(Godot.GodotObject).Assembly.GetType("Godot." + name) ?? typeof(Godot.GodotObject);
            __result = (Godot.GodotObject)RuntimeHelpers.GetUninitializedObject(type);
            return false;
        }
    }

    [HarmonyPatch(typeof(Godot.ResourceLoader), nameof(Godot.ResourceLoader.Exists))]
    private static class NoResources
    {
        private static bool Prefix(ref bool __result) { __result = false; return false; }
    }

    [HarmonyPatch]
    private static class NoResourceLoads
    {
        private static MethodBase TargetMethod() =>
            typeof(Godot.ResourceLoader).GetMethods()
                .First(m => m.Name == nameof(Godot.ResourceLoader.Load) && !m.IsGenericMethod && m.GetParameters().Length == 3);

        private static bool Prefix(ref Godot.Resource? __result) { __result = null; return false; }
    }

    [HarmonyPatch(typeof(PreloadManager), nameof(PreloadManager.LoadRoomCombatAssets))]
    private static class NoCombatAssets
    {
        private static bool Prefix(ref Task __result) { __result = Task.CompletedTask; return false; }
    }

    [HarmonyPatch(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerName))]
    private static class PlayerName
    {
        private static bool Prefix(ref string __result) { __result = "Player"; return false; }
    }

    // ---- localization: text is never needed for rules, so loc lookups return their key ----

    [HarmonyPatch(typeof(LocString), nameof(LocString.GetFormattedText))]
    private static class LocFormatted
    {
        private static bool Prefix(LocString __instance, ref string __result) { __result = __instance.LocTable + "." + __instance.LocEntryKey; return false; }
    }

    [HarmonyPatch(typeof(LocString), nameof(LocString.GetRawText))]
    private static class LocRaw
    {
        private static bool Prefix(LocString __instance, ref string __result) { __result = __instance.LocTable + "." + __instance.LocEntryKey; return false; }
    }

    [HarmonyPatch(typeof(LocString), nameof(LocString.Exists), [typeof(string), typeof(string)])]
    private static class LocExists
    {
        private static bool Prefix(ref bool __result) { __result = true; return false; }
    }

    // ---- logging ----

    [HarmonyPatch(typeof(ConsoleLogPrinter), nameof(ConsoleLogPrinter.Print))]
    private static class LogToStderr
    {
        private static bool Prefix(LogLevel logLevel, string text)
        {
            if (HeadlessGame.Verbose || logLevel >= LogLevel.Warn) Console.Error.WriteLine($"[game:{logLevel}] {text}");
            return false;
        }
    }

    // ---- persistence: nothing is ever saved; the user's real saves must stay untouched ----

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun))]
    private static class NoRunSave
    {
        private static bool Prefix(ref Task __result) { __result = Task.CompletedTask; return false; }
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

    // ---- rule fidelity: undo TestMode branches that change game rules (tmp/notes/testmode-audit.md) ----

    /// <summary>
    /// Shipped game: the boulder rolls across the screen and damages each enemy separately as it reaches it, left
    /// to right. TestMode instead deals one multi-target hit. Enemies are laid out in list order, so hitting them
    /// one by one in that order reproduces the shipped sequence (encounters with authored slots are an
    /// approximation: their screen order comes from scene files we do not read).
    /// </summary>
    [HarmonyPatch(typeof(RollingBoulderPower), "DoDamage")]
    private static class RollingBoulderOneByOne
    {
        private static readonly Func<RollingBoulderPower, PlayerChoiceContext, IEnumerable<Creature>, Task<IEnumerable<DamageResult>>> DoDamage =
            AccessTools.MethodDelegate<Func<RollingBoulderPower, PlayerChoiceContext, IEnumerable<Creature>, Task<IEnumerable<DamageResult>>>>(
                AccessTools.Method(typeof(RollingBoulderPower), "DoDamage"));

        private static bool Prefix(RollingBoulderPower __instance, PlayerChoiceContext choiceContext, IEnumerable<Creature> targets,
            ref Task<IEnumerable<DamageResult>> __result)
        {
            var list = targets.ToList();
            if (list.Count <= 1) return true;
            RollingBoulderSplits++;
            __result = HitEach(__instance, choiceContext, list);
            return false;
        }

        private static async Task<IEnumerable<DamageResult>> HitEach(RollingBoulderPower power, PlayerChoiceContext ctx, List<Creature> targets)
        {
            var results = new List<DamageResult>();
            foreach (var target in targets) results.AddRange(await DoDamage(power, ctx, [target]));
            return results;
        }
    }

    /// <summary>
    /// Methods whose TestMode branch skips an RNG draw (or a grab-bag pull) that the shipped game makes, which would
    /// shift every later draw from that stream. Their shipped branch is plain logic, so it runs as-is with the
    /// TestMode check replaced by a constant.
    /// </summary>
    [HarmonyPatch]
    private static class ShippedBranch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
        [
            AccessTools.Method(typeof(MerchantPotionEntry), nameof(MerchantPotionEntry.CalcCost)),
            AccessTools.Method(typeof(Cauldron), "GenerateRewards"),
            AccessTools.Method(typeof(CallingBell), "GenerateRewards"),
        ];

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var isOn = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOn));
            var isOff = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOff));
            int replaced = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(isOn) || ins.Calls(isOff))
                {
                    // Keep the instruction object so labels and exception blocks stay attached.
                    ins.opcode = ins.Calls(isOn) ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1;
                    ins.operand = null;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
                throw new InvalidOperationException($"headless: no TestMode check left in {original.DeclaringType?.Name}.{original.Name}; re-audit after the game update");
        }
    }

    // ---- speed: same results, fewer allocations on the hottest paths ----

    /// <summary>
    /// Combat history, hook contexts and targeting read <c>CombatState.Players</c> many times per action, and the
    /// game builds it through three LINQ lists (Creatures, then PlayerCreatures, then Players). These loops return
    /// the same fresh snapshot list in the same order.
    /// </summary>
    [HarmonyPatch(typeof(CombatState), nameof(CombatState.Creatures), MethodType.Getter)]
    private static class FastCreatures
    {
        private static bool Prefix(CombatState __instance, ref IReadOnlyList<Creature> __result)
        {
            var allies = __instance.Allies;
            var enemies = __instance.Enemies;
            var list = new List<Creature>(allies.Count + enemies.Count);
            for (int i = 0; i < allies.Count; i++) list.Add(allies[i]);
            for (int i = 0; i < enemies.Count; i++) list.Add(enemies[i]);
            __result = list;
            return false;
        }
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.PlayerCreatures), MethodType.Getter)]
    private static class FastPlayerCreatures
    {
        private static bool Prefix(CombatState __instance, ref IReadOnlyList<Creature> __result)
        {
            var list = new List<Creature>(1);
            Collect(__instance.Allies, list);
            Collect(__instance.Enemies, list);
            __result = list;
            return false;
        }

        private static void Collect(IReadOnlyList<Creature> creatures, List<Creature> into)
        {
            for (int i = 0; i < creatures.Count; i++)
                if (creatures[i].IsPlayer) into.Add(creatures[i]);
        }
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.Players), MethodType.Getter)]
    private static class FastPlayers
    {
        private static bool Prefix(CombatState __instance, ref IReadOnlyList<Player> __result)
        {
            var list = new List<Player>(1);
            Collect(__instance.Allies, list);
            Collect(__instance.Enemies, list);
            __result = list;
            return false;
        }

        private static void Collect(IReadOnlyList<Creature> creatures, List<Player> into)
        {
            for (int i = 0; i < creatures.Count; i++)
                if (creatures[i].Player is { } player) into.Add(player);
        }
    }

    // ---- choices ----

    /// <summary>
    /// The choose-a-card screen always asks the selector for 0..1 cards; whether skipping is allowed is a
    /// separate flag. The selector runs synchronously inside this call, so a prefix/postfix pair can pass it on.
    /// </summary>
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    private static class ChooseACardSkip
    {
        private static void Prefix(bool canSkip) => PolicySelector.MustPickOne = !canSkip;
        private static void Postfix() => PolicySelector.MustPickOne = false;
    }
}
