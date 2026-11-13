using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
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
