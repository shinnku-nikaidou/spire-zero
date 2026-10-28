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
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using Sts2Ai.GameBridge;

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

    /// <summary>Plays a crystal sphere minigame the game would put on screen. Without it, the screen's code runs (and fails).</summary>
    public static Action<CrystalSphereMinigame>? CrystalSphereShown;

    /// <summary>The game saving on arrival at a map point (docs: environments §4): its state is a node checkpoint.</summary>
    public static Action? NodeEntered;

    public static void Apply()
    {
        var harmony = new Harmony("sts2ai.simworker");
        // First: patching compiles each replaced method, which can inline a CardSelectCmd entry not yet patched
        // (SapphireSeed's planting, rewritten by EventPresentation, did).
        ChoiceContexts.Patch(harmony);
        harmony.PatchAll(typeof(HeadlessPatches).Assembly);
    }

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

    /// <summary>Scene and texture preloads for the run, each act and each room kind.</summary>
    [HarmonyPatch]
    private static class NoAssetPreloads
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(PreloadManager).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.ReturnType == typeof(Task) && (m.Name is nameof(PreloadManager.LoadRunAssets) or nameof(PreloadManager.LoadActAssets)
                    || m.Name.StartsWith("LoadRoom", StringComparison.Ordinal)));

        private static bool Prefix(ref Task __result) { __result = Task.CompletedTask; return false; }
    }

    // Model hooks that reach a node without the TestMode / NonInteractiveMode check the rest of the model code has.
    // Their whole body is presentation (sound, animation, music), so they are skipped.

    /// <summary>Kaiser Crab's arms.</summary>
    [HarmonyPatch]
    private static class NoKaiserCrabDeathEffects
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            [AccessTools.Method(typeof(Rocket), nameof(Rocket.BeforeDeath)), AccessTools.Method(typeof(Crusher), nameof(Crusher.BeforeDeath))];

        private static bool Prefix(ref Task __result) { __result = Task.CompletedTask; return false; }
    }

    [HarmonyPatch(typeof(SoulNexus), "AfterDeath")]
    private static class NoSoulNexusDeathAnimation
    {
        private static bool Prefix() => false;
    }

    /// <summary>
    /// Event options do the same in the middle of their rules (screen shake, sounds, the portrait), and Amalgamator
    /// waits real time between two shakes. In event code, every call into a Godot object, or returning one, is dropped
    /// (its result is the default) and Task.Delay is a finished task. No event reads a rule back from a node.
    /// </summary>
    [HarmonyPatch]
    private static class EventPresentation
    {
        private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The event methods, with their lambdas and async state machines, that make such a call.</summary>
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(EventModel).Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(EventModel)))
                .SelectMany(WithNested)
                .SelectMany(t => t.GetMethods(Declared).Cast<MethodBase>().Concat(t.GetConstructors(Declared)))
                .Where(m => !m.ContainsGenericParameters && m.GetMethodBody() != null
                    && PatchProcessor.ReadMethodBody(m).Any(i => i.Value is MethodInfo called && IsPresentation(called)));

        private static IEnumerable<Type> WithNested(Type t) => t.GetNestedTypes(Declared).SelectMany(WithNested).Prepend(t);

        private static bool IsPresentation(MethodInfo m) =>
            typeof(Godot.GodotObject).IsAssignableFrom(m.DeclaringType) || typeof(Godot.GodotObject).IsAssignableFrom(m.ReturnType)
            || m.DeclaringType == typeof(Task) && m.Name == nameof(Task.Delay);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            foreach (var ins in instructions)
            {
                if (!(ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) || ins.operand is not MethodInfo m || !IsPresentation(m))
                {
                    yield return ins;
                    continue;
                }
                // Pop the arguments and the instance, then push the result: none, a finished task, or the default.
                var replaced = new List<CodeInstruction>();
                for (int i = m.GetParameters().Length + (m.IsStatic ? 0 : 1); i > 0; i--) replaced.Add(new CodeInstruction(OpCodes.Pop));
                var type = m.ReturnType;
                if (type == typeof(Task)) replaced.Add(new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Task), nameof(Task.CompletedTask))));
                else if (type.IsValueType && type != typeof(void))
                {
                    var local = il.DeclareLocal(type);
                    replaced.AddRange([new(OpCodes.Ldloca, local), new(OpCodes.Initobj, type), new(OpCodes.Ldloc, local)]);
                }
                else if (type != typeof(void)) replaced.Add(new CodeInstruction(OpCodes.Ldnull));
                if (replaced.Count == 0) replaced.Add(new CodeInstruction(OpCodes.Nop));
                ins.MoveLabelsTo(replaced[0]);
                ins.MoveBlocksTo(replaced[0]);
                foreach (var r in replaced) yield return r;
            }
        }
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
    private static class LogToLogs
    {
        private static bool Prefix(LogLevel logLevel, string text)
        {
            var level = logLevel switch
            {
                LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
                LogLevel.Warn => Microsoft.Extensions.Logging.LogLevel.Warning,
                _ => Microsoft.Extensions.Logging.LogLevel.Debug,
            };
            // The extension method by its class: this file's LogLevel is the game's.
            if (Logs.Game.IsEnabled(level)) Microsoft.Extensions.Logging.LoggerExtensions.Log(Logs.Game, level, "{Text}", text);
            return false;
        }
    }

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

    // ---- game quirks: errors the shipped game logs too, fixed so that every game error stays a real one ----

    /// <summary>
    /// A card play pushes the card on its choice context and pops it at the end, but returns early, without the pop,
    /// when the player has died during the play (or the card left combat). The model stack only feeds multiplayer
    /// display, so nothing else changes; but the hook that started the play then pops its own model with the card
    /// on top and logs an error. Seen as Tools of the Trade discarding Ricochet at turn start, Sly playing it, and a
    /// Spiny Toad's Thorns killing the player (checked by selftest). This pops the card when the play left it on top.
    /// </summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    internal static class PopCardAfterEarlyReturn
    {
        /// <summary>How many plays returned early with the card left on top (checked by selftest).</summary>
        public static int Pops;

        private static void Postfix(CardModel __instance, PlayerChoiceContext choiceContext, ref Task __result)
        {
            if (__result.IsCompletedSuccessfully) PopIfLeft(__instance, choiceContext);
            else __result = PopAfter(__instance, choiceContext, __result);
        }

        private static async Task PopAfter(CardModel card, PlayerChoiceContext context, Task play)
        {
            await play;
            PopIfLeft(card, context);
        }

        private static void PopIfLeft(CardModel card, PlayerChoiceContext context)
        {
            if (context.LastInvolvedModel != card || !(card.Owner.Creature.IsDead || card.CombatState == null)) return;
            Pops++;
            context.PopModel(card);
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

    /// <summary>The crystal sphere's screen: <see cref="CrystalSphereShown"/> plays the minigame instead.</summary>
    [HarmonyPatch(typeof(NCrystalSphereScreen), nameof(NCrystalSphereScreen.ShowScreen))]
    private static class CrystalSphereScreen
    {
        private static bool Prefix(CrystalSphereMinigame grid)
        {
            if (CrystalSphereShown is not { } play) return true;
            play(grid);
            return false;
        }
    }

    /// <summary>
    /// A card removal is marked used by the shop's removal button (<c>NMerchantCardRemoval.OnCardRemovalUsed</c>), whoever
    /// bought it: the player or Lord's Parasol. Headless there is no button, so this marks it.
    /// </summary>
    [HarmonyPatch(typeof(MerchantCardRemovalEntry), "OnTryPurchase", [typeof(MerchantInventory), typeof(bool), typeof(bool)])]
    private static class CardRemovalUsed
    {
        private static void Postfix(MerchantCardRemovalEntry __instance, ref Task<(bool, int)> __result) => __result = MarkUsed(__instance, __result);

        private static async Task<(bool, int)> MarkUsed(MerchantCardRemovalEntry removal, Task<(bool, int)> purchase)
        {
            var result = await purchase;
            if (result.Item1) removal.SetUsed();
            return result;
        }
    }

    /// <summary>
    /// TestMode always takes the first bundle. With a bundle handler, this runs the screen's own path instead: reserve
    /// the choice, ask, sync the answer (<c>CardSelectCmd.FromChooseABundleScreen</c>).
    /// </summary>
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseABundleScreen))]
    private static class ChooseABundle
    {
        private static bool Prefix(Player player, IReadOnlyList<IReadOnlyList<CardModel>> bundles, ref Task<IEnumerable<CardModel>> __result)
        {
            if (CardSelectCmd.Selector is not PolicySelector { ChoosesBundles: true } selector || CombatManager.Instance.IsEnding || bundles.Count == 0)
                return true;
            var choices = RunManager.Instance.PlayerChoiceSynchronizer;
            uint choiceId = choices.ReserveChoiceId(player);
            int i = selector.ChooseBundle(bundles);
            choices.SyncLocalChoice(player, choiceId, PlayerChoiceResult.FromIndex(i));
            __result = Task.FromResult<IEnumerable<CardModel>>(bundles[i]);
            return false;
        }
    }
}
