// TEMPORARY diagnostic (delete after profiling): counts hook dispatches and listeners enumerated.
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using System.Diagnostics;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Ai.Headless;

internal static class Probe
{
    private static readonly Dictionary<string, long> Calls = new();
    private static long _combatIter, _combatItems, _runIter, _runItems, _hookCalls;
    private static readonly Dictionary<string, long> Ticks = new();
    private static readonly Dictionary<string, long> Logs = new();
    private static int _hookDepth, _enumDepth;
    private static long _hookStart, _hookAlloc, _enumStart;
    private static string _hookName = "";
    private static readonly Dictionary<string, (long Ticks, long Bytes, long Calls)> PerHook = new();

    public static void MaybeApply()
    {
        if (Environment.GetEnvironmentVariable("STS2AI_PROBE") != "1") return;
        var h = new Harmony("sts2ai.probe");
        var count = new HarmonyMethod(typeof(Probe), nameof(CountHook));
        foreach (var m in typeof(Hook).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => !m.IsGenericMethodDefinition))
            h.Patch(m, prefix: count, postfix: new HarmonyMethod(typeof(Probe), nameof(HookExit)));
        h.Patch(AccessTools.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)), postfix: new HarmonyMethod(typeof(Probe), nameof(WrapCombat)));
        h.Patch(AccessTools.Method(typeof(RunState), nameof(RunState.IterateHookListeners)), postfix: new HarmonyMethod(typeof(Probe), nameof(WrapRun)));
        foreach (var name in new[] { "LegalActions", "Execute", "Dispose", "Determinize" })
            h.Patch(AccessTools.Method(typeof(CombatSession), name), prefix: new HarmonyMethod(typeof(Probe), nameof(TimeStart)), postfix: new HarmonyMethod(typeof(Probe), nameof(TimeEnd)));
        h.Patch(AccessTools.Method(typeof(CombatSession), "Start", [typeof(Pump), typeof(CombatSetup), typeof(IChoicePolicy), typeof(IEnumerable<int[]>)]),
            prefix: new HarmonyMethod(typeof(Probe), nameof(TimeStart)), postfix: new HarmonyMethod(typeof(Probe), nameof(TimeEnd)));
        h.Patch(AccessTools.Method(typeof(Logger), nameof(Logger.LogMessage), [typeof(LogLevel), typeof(LogType), typeof(string), typeof(int)]),
            prefix: new HarmonyMethod(typeof(Probe), nameof(CountLog)));
        foreach (var t in typeof(GameAction).Assembly.GetTypes().Where(t => typeof(GameAction).IsAssignableFrom(t)))
            if (t.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, []) is { } ts)
                h.Patch(ts, prefix: new HarmonyMethod(typeof(Probe), nameof(TimeStart)), postfix: new HarmonyMethod(typeof(Probe), nameof(TimeToString)));
        long alloc0 = GC.GetTotalAllocatedBytes(true);
        int[] gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var wall = Stopwatch.StartNew();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Console.Error.WriteLine($"probe: wall {wall.Elapsed.TotalSeconds:F2}s, allocated {(GC.GetTotalAllocatedBytes(true) - alloc0) / 1e6:F0} MB, " +
                                    $"GCs {GC.CollectionCount(0) - gc0[0]}/{GC.CollectionCount(1) - gc0[1]}/{GC.CollectionCount(2) - gc0[2]}, GC pause {GC.GetTotalPauseDuration().TotalSeconds:F2}s");
            foreach (var (k, v) in Ticks) Console.Error.WriteLine($"probe: time {k}: {v / (double)Stopwatch.Frequency:F2}s");
            foreach (var (k, (t, b, c)) in PerHook.OrderByDescending(kv => kv.Value.Ticks).Take(25))
                Console.Error.WriteLine($"probe: hook {k}: {t / (double)Stopwatch.Frequency:F2}s, {b / 1e6:F0} MB, {c} outermost calls");
            foreach (var (k, v) in Logs) Console.Error.WriteLine($"probe: log {k}: {v}");
            Console.Error.WriteLine($"probe: hook calls {_hookCalls}; combat iterations {_combatIter} ({_combatItems} listeners); run iterations {_runIter} ({_runItems} listeners)");
            foreach (var (k, v) in Calls.OrderByDescending(kv => kv.Value).Take(30)) Console.Error.WriteLine($"probe: {v,10} {k}");
        };
    }

    private static void TimeStart(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void TimeEnd(long __state, MethodBase __originalMethod) =>
        Ticks[__originalMethod.Name] = Ticks.GetValueOrDefault(__originalMethod.Name) + Stopwatch.GetTimestamp() - __state;

    private static void CountLog(LogLevel level, LogType type, Logger __instance)
    {
        string key = $"{level} {type} {(__instance.WillLog(level) ? "printed" : "dropped")}";
        Logs[key] = Logs.GetValueOrDefault(key) + 1;
    }

    private static void HookEnter(string name)
    {
        if (_hookDepth++ != 0) return;
        _hookName = name; _hookAlloc = GC.GetAllocatedBytesForCurrentThread(); _hookStart = Stopwatch.GetTimestamp();
    }

    private static void HookExit()
    {
        if (--_hookDepth != 0) return;
        long dt = Stopwatch.GetTimestamp() - _hookStart, db = GC.GetAllocatedBytesForCurrentThread() - _hookAlloc;
        Ticks["(outermost hooks, sync part)"] = Ticks.GetValueOrDefault("(outermost hooks, sync part)") + dt;
        var (t, b, c) = PerHook.GetValueOrDefault(_hookName);
        PerHook[_hookName] = (t + dt, b + db, c + 1);
    }

    private static void CountHook(MethodBase __originalMethod)
    {
        HookEnter(__originalMethod.Name);
        _hookCalls++;
        Calls[__originalMethod.Name] = Calls.GetValueOrDefault(__originalMethod.Name) + 1;
    }

    private static void WrapCombat(ref IEnumerable<AbstractModel> __result) { _combatIter++; __result = Counted(__result, true); }
    private static void WrapRun(ref IEnumerable<AbstractModel> __result) { _runIter++; __result = Counted(__result, false); }

    private static IEnumerable<AbstractModel> Counted(IEnumerable<AbstractModel> source, bool combat)
    {
        using var e = source.GetEnumerator();
        while (true)
        {
            bool outer = _enumDepth++ == 0;
            if (outer) _enumStart = Stopwatch.GetTimestamp();
            bool more = e.MoveNext();
            _enumDepth--;
            if (outer) Ticks["(listener enumeration)"] = Ticks.GetValueOrDefault("(listener enumeration)") + Stopwatch.GetTimestamp() - _enumStart;
            if (!more) yield break;
            if (combat) _combatItems++; else _runItems++;
            yield return e.Current;
        }
    }

    private static void TimeToString(long __state) =>
        Ticks["(GameAction.ToString)"] = Ticks.GetValueOrDefault("(GameAction.ToString)") + Stopwatch.GetTimestamp() - __state;
}
