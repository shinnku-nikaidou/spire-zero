using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Hooks;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>
/// The merchant: "buy:SLOT" (index into <see cref="MerchantInventory.AllEntries"/> without the removal service:
/// character cards, colorless cards, relics, potions), "remove" (card removal service), "leave".
/// </summary>
public static class ShopActions
{
    /// <summary>The goods still for sale, by slot, and the removal service's price unless used.</summary>
    public static ShopScreen Screen(MerchantInventory inventory) => new()
    {
        Entries = [.. Goods(inventory).Select((entry, slot) => (entry, slot)).Where(e => e.entry.IsStocked).Select(e => View(e.entry, e.slot))],
        RemovalCost = inventory.CardRemovalEntry is { IsStocked: true } removal ? removal.Cost : null,
    };

    private static ShopEntryView View(MerchantEntry entry, int slot) => entry switch
    {
        MerchantCardEntry card => new() { Slot = slot, Kind = "card", Card = Observe.Card(card.CreationResult!.Card), Cost = entry.Cost, OnSale = card.IsOnSale ? true : null },
        MerchantRelicEntry relic => new() { Slot = slot, Kind = "relic", Id = relic.Model!.Id.Entry, Cost = entry.Cost },
        MerchantPotionEntry potion => new() { Slot = slot, Kind = "potion", Id = potion.Model!.Id.Entry, Cost = entry.Cost },
        _ => throw new NotSupportedException($"shop entry {entry.GetType().Name} has no view"),
    };

    /// <summary>
    /// Stocked entries the player can afford. Potions need a free belt slot and the game's leave to take one (Sozu
    /// forbids it): without either the purchase fails and nothing changes.
    /// </summary>
    public static List<MacroAction> Legal(MerchantInventory inventory)
    {
        var actions = new List<MacroAction>();
        var entries = Goods(inventory);
        var player = inventory.Player;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!entry.IsStocked || !entry.EnoughGold) continue;
            if (entry is MerchantPotionEntry potion
                && (!player.HasOpenPotionSlots || !Hook.ShouldProcurePotion(player.RunState, player.Creature.CombatState, potion.Model!, player)))
                continue;
            actions.Add(MacroAction.Of(MacroAction.Buy, i));
        }
        if (inventory.CardRemovalEntry is { IsStocked: true, EnoughGold: true }) actions.Add(MacroAction.Remove);
        actions.Add(MacroAction.Leave);
        return actions;
    }

    /// <summary>
    /// Buys, as clicking the item does; the task ends when the purchase is done (true if bought). Card removal asks
    /// for the card through the card selector; the shop's removal button then marks it used.
    /// "leave" changes nothing in the game: the caller opens the map.
    /// </summary>
    public static async Task<bool> Start(MerchantInventory inventory, MacroAction action)
    {
        if (!Legal(inventory).Contains(action)) throw Illegal.Action(action, Surfaces.Shop);
        if (action == MacroAction.Leave) return true;
        if (action == MacroAction.Remove) return await inventory.CardRemovalEntry!.OnTryPurchaseWrapper(inventory);
        return await Goods(inventory)[action.Index].OnTryPurchaseWrapper(inventory);
    }

    /// <summary>Buying <paramref name="entry"/>, or the removal service, as the action names it.</summary>
    public static MacroAction Of(MerchantInventory inventory, MerchantEntry entry) =>
        entry is MerchantCardRemovalEntry ? MacroAction.Remove : MacroAction.Of(MacroAction.Buy, Goods(inventory).IndexOf(entry));

    private static List<MerchantEntry> Goods(MerchantInventory inventory) =>
        [.. inventory.AllEntries.Where(e => e is not MerchantCardRemovalEntry)];
}
