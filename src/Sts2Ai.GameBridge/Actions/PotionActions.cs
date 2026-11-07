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
    public static List<string> Legal(Player player)
    {
        var actions = new List<string>();
        if (!player.CanUseOrRemovePotions || player.Creature.IsDead) return actions;
        for (int s = 0; s < player.PotionSlots.Count; s++)
        {
            var potion = player.PotionSlots[s];
            if (potion == null || potion.IsQueued) continue;
            if (potion.Usage == PotionUsage.AnyTime && potion.PassesCustomUsabilityCheck) actions.Add($"potion:{s}");
            actions.Add($"discard_potion:{s}");
        }
        return actions;
    }

    /// <summary>Whether <paramref name="action"/> is a belt action (legal or not) rather than one of the surface's own.</summary>
    public static bool Handles(string action) => ActionText.Is(action, "potion", out _) || ActionText.Is(action, "discard_potion", out _);

    /// <summary>Uses or discards the potion through the action queue, as the popup's buttons do.</summary>
    public static void Start(Player player, string action)
    {
        if (!Legal(player).Contains(action)) throw ActionText.Illegal(action, "potion");
        if (ActionText.Is(action, "potion", out var slot))
            player.PotionSlots[ActionText.Index(slot)]!.EnqueueManualUse(null);
        else if (ActionText.Is(action, "discard_potion", out slot))
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new DiscardPotionGameAction(player, (uint)ActionText.Index(slot), CombatManager.Instance.IsInProgress));
    }
}
