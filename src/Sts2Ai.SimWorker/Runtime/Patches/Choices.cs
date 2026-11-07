using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Ai.SimWorker.Runtime.Patches;

internal static partial class HeadlessPatches
{
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
