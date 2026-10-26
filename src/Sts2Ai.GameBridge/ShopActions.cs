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
    /// for the card through the card selector, and is marked used here because the shop node does that headless.
    /// "leave" changes nothing in the game: the caller opens the map.
    /// </summary>
    public static async Task<bool> Start(MerchantInventory inventory, string action)
    {
        if (!Legal(inventory).Contains(action)) throw ActionText.Illegal(action, Surfaces.Shop);
        if (action == Leave) return true;
        if (action == Remove)
        {
            var removal = inventory.CardRemovalEntry!;
            bool removed = await removal.OnTryPurchaseWrapper(inventory);
            if (removed) removal.SetUsed();
            return removed;
        }
        return await Goods(inventory)[ActionText.Index(action["buy:".Length..])].OnTryPurchaseWrapper(inventory);
    }

    private static List<MerchantEntry> Goods(MerchantInventory inventory) =>
        inventory.AllEntries.Where(e => e is not MerchantCardRemovalEntry).ToList();
}
