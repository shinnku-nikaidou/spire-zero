using Sts2Ai.Protocol;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>
/// The potion belt outside combat, available on every out-of-combat surface: "potion:S" drinks an any-time potion,
/// "discard_potion:S" throws one away. Same rules as the potion popup (<c>NPotionPopup</c>).
/// </summary>
public static class PotionActions
{
    public static List<MacroAction> Legal(Player player)
    {
        var actions = new List<MacroAction>();
        if (!player.CanUseOrRemovePotions || player.Creature.IsDead) return actions;
        for (int s = 0; s < player.PotionSlots.Count; s++)
        {
            var potion = player.PotionSlots[s];
            if (potion == null || potion.IsQueued) continue;
            if (potion.Usage == PotionUsage.AnyTime && potion.PassesCustomUsabilityCheck) actions.Add(MacroAction.Of(MacroAction.Potion, s));
            actions.Add(MacroAction.Of(MacroAction.DiscardPotion, s));
        }
        return actions;
    }

    /// <summary>Whether <paramref name="action"/> is a belt action (legal or not) rather than one of the surface's own.</summary>
    public static bool Handles(MacroAction action) => action.Verb is MacroAction.Potion or MacroAction.DiscardPotion;

    /// <summary>Uses or discards the potion through the action queue, as the popup's buttons do.</summary>
    public static void Start(Player player, MacroAction action)
    {
        if (!Legal(player).Contains(action)) throw Illegal.Action(action, "potion");
        if (action.Verb == MacroAction.Potion)
            player.PotionSlots[action.Index]!.EnqueueManualUse(null);
        else
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new DiscardPotionGameAction(player, (uint)action.Index, CombatManager.Instance.IsInProgress));
    }
}
