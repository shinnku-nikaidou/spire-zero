using MegaCrit.Sts2.Core.Entities.Merchant;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>
/// The merchant: "buy:SLOT" (index into <see cref="MerchantInventory.AllEntries"/> without the removal service:
/// character cards, colorless cards, relics, potions), "remove" (card removal service), "leave".
/// </summary>
public static class ShopActions
{
    public const string Remove = "remove";
    public const string Leave = "leave";

    /// <summary>The goods still for sale, by slot, and the removal service's price unless used.</summary>
    public static ShopScreen Screen(MerchantInventory inventory) => new()
    {
        Entries = Goods(inventory).Select((entry, slot) => (entry, slot)).Where(e => e.entry.IsStocked).Select(e => View(e.entry, e.slot)).ToList(),
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
    /// Stocked entries the player can afford. Potions need a free belt slot: without one the purchase fails and
    /// nothing changes.
    /// </summary>
    public static List<string> Legal(MerchantInventory inventory)
    {
        var actions = new List<string>();
        var entries = Goods(inventory);
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!entry.IsStocked || !entry.EnoughGold) continue;
            if (entry is MerchantPotionEntry && !inventory.Player.HasOpenPotionSlots) continue;
            actions.Add($"buy:{i}");
        }
        if (inventory.CardRemovalEntry is { IsStocked: true, EnoughGold: true }) actions.Add(Remove);
        actions.Add(Leave);
        return actions;
    }

    /// <summary>
    /// Buys, as clicking the item does; the task ends when the purchase is done (true if bought). Card removal asks
    /// for the card through the card selector; the shop's removal button then marks it used.
    /// "leave" changes nothing in the game: the caller opens the map.
    /// </summary>
    public static async Task<bool> Start(MerchantInventory inventory, string action)
    {
        if (!Legal(inventory).Contains(action)) throw ActionText.Illegal(action, Surfaces.Shop);
        if (action == Leave) return true;
        if (action == Remove) return await inventory.CardRemovalEntry!.OnTryPurchaseWrapper(inventory);
        return await Goods(inventory)[ActionText.Index(action["buy:".Length..])].OnTryPurchaseWrapper(inventory);
    }

    private static List<MerchantEntry> Goods(MerchantInventory inventory) =>
        inventory.AllEntries.Where(e => e is not MerchantCardRemovalEntry).ToList();
}
