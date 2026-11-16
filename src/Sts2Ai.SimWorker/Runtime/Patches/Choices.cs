using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using Sts2Ai.SimWorker.Simulation;

namespace Sts2Ai.SimWorker.Runtime.Patches;

internal static partial class HeadlessPatches
{
    // ---- choices ----

    /// <summary>
    /// In combat the game asks a player on the screen: <c>CardSelectCmd</c> finds no selector on the player's stack,
    /// pauses what asked (<c>SignalPlayerChoiceBegun</c>: a hook of the turn's start lets the rest of the turn start run
    /// first), then asks the local selector in the screen's place. A session's selector is that local selector
    /// (<see cref="PromptSelector.Install"/>), so the stack does not show it while a combat is in progress, and the
    /// combat runs in the order the real game runs it. Outside combat it stays on the stack, where card rewards and
    /// bundles look for it. The game's own selector (Whispering Earring's) is on top of it and answers at once, as in
    /// the game.
    /// </summary>
    [HarmonyPatch(typeof(CardSelectorStack), nameof(CardSelectorStack.GetSelector))]
    private static class AskedOnTheScreen
    {
        private static void Postfix(ref ICardSelector? __result)
        {
            if (__result is PromptSelector && CombatManager.Instance.IsInProgress) __result = null;
        }
    }

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
    /// TestMode always takes the first bundle. With the policy's selector, this runs the screen's own path instead: reserve
    /// the choice, ask, sync the answer (<c>CardSelectCmd.FromChooseABundleScreen</c>).
    /// </summary>
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseABundleScreen))]
    private static class ChooseABundle
    {
        private static bool Prefix(Player player, IReadOnlyList<IReadOnlyList<CardModel>> bundles, ref Task<IEnumerable<CardModel>> __result)
        {
            if (RunManager.Instance.CardSelectorStack.GetSelector(player) is not PromptSelector selector || CombatManager.Instance.IsEnding || bundles.Count == 0)
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
