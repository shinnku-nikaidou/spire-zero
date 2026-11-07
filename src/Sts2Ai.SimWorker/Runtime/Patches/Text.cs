using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace Sts2Ai.SimWorker.Runtime.Patches;

internal static partial class HeadlessPatches
{
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

    /// <summary>
    /// The Fake Merchant says a line before each move, drawn from the monsters' loc table, which is not loaded headless
    /// (the lookup throws, and the fight's turn loop dies with it). The line is only shown, and drawn with the cosmetic
    /// RNG, so it is left out.
    /// </summary>
    [HarmonyPatch(typeof(FakeMerchantMonster), "ShowDialogueForMove")]
    private static class FakeMerchantSaysNothing
    {
        private static bool Prefix(ref Task __result) { __result = Task.CompletedTask; return false; }
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
}
