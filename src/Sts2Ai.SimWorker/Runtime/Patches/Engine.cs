using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;

namespace Sts2Ai.SimWorker.Runtime.Patches;

internal static partial class HeadlessPatches
{
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
        private static MethodInfo TargetMethod() =>
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
        private static MethodInfo TargetMethod() =>
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
}
