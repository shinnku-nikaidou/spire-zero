using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2Ai.SimWorker;

/// <summary>
/// A card in a loadout: its model id and upgrade count, written "BASH", "BASH+" or "BASH+2", and its enchantment
/// if any (not available on the command line).
/// </summary>
internal readonly record struct CardSpec(string Id, int Upgrades = 0, string? Enchant = null, int EnchantAmount = 0)
{
    public static CardSpec Parse(string text)
    {
        int plus = text.IndexOf('+');
        if (plus < 0) return new(text);
        var count = text[(plus + 1)..];
        return new(text[..plus], count.Length == 0 ? 1 : int.Parse(count));
    }

    public SerializableCard ToSerializable() => new()
    {
        Id = Models.Find<CardModel>(Id).Id,
        CurrentUpgradeLevel = Upgrades,
        Enchantment = Enchant == null ? null : new SerializableEnchantment { Id = Models.Find<EnchantmentModel>(Enchant).Id, Amount = EnchantAmount },
        FloorAddedToDeck = 1,
    };

    public override string ToString() =>
        Upgrades switch { 0 => Id, 1 => Id + "+", _ => $"{Id}+{Upgrades}" } + (Enchant == null ? "" : $" ({Enchant} {EnchantAmount})");
}

/// <summary>
/// What the player brings into a combat. Null fields keep the character's starting values, so the default
/// loadout is a fresh run's. This is the final state, not a list of pickups: relics are added without running
/// their pickup effects (a max-HP relic does not raise <see cref="MaxHp"/>).
/// </summary>
internal sealed record Loadout(
    IReadOnlyList<CardSpec>? Deck = null,
    IReadOnlyList<string>? Relics = null,
    IReadOnlyList<string>? Potions = null,
    int? Hp = null,
    int? MaxHp = null)
{
    /// <summary>
    /// Parses comma-separated lists, where "ID*N" repeats an entry: deck "STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH+".
    /// </summary>
    public static Loadout Parse(string? deck, string? relics, string? potions, int? hp, int? maxHp) => new(
        deck == null ? null : Expand(deck).Select(CardSpec.Parse).ToList(),
        relics == null ? null : Expand(relics).ToList(),
        potions == null ? null : Expand(potions).ToList(),
        hp,
        maxHp);

    private static IEnumerable<string> Expand(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SelectMany(item =>
        {
            int star = item.LastIndexOf('*');
            return star < 0 ? [item] : Enumerable.Repeat(item[..star], int.Parse(item[(star + 1)..]));
        });

    /// <summary>Replaces the player's starting inventory. Must run before the player is added to a run.</summary>
    public void ApplyTo(Player player)
    {
        if (Deck != null)
        {
            foreach (var card in player.Deck.Cards.ToList()) player.Deck.RemoveInternal(card, silent: true);
            foreach (var spec in Deck) player.Deck.AddInternal(CardModel.FromSerializable(spec.ToSerializable()), silent: true);
        }
        if (Relics != null)
        {
            foreach (var relic in player.Relics.ToList()) player.RemoveRelicInternal(relic, silent: true);
            foreach (var id in Relics) player.AddRelicInternal(Models.Find<RelicModel>(id).ToMutable(), silent: true);
        }
        if (Potions != null)
        {
            foreach (var potion in player.Potions.ToList()) player.DiscardPotionInternal(potion, silent: true);
            foreach (var id in Potions)
                if (!player.AddPotionInternal(Models.Find<PotionModel>(id).ToMutable(), silent: true).success)
                    throw new ArgumentException($"no free potion slot for {id}");
        }
        if (MaxHp is int max) player.Creature.SetMaxHpInternal(max);
        if (Hp is int hp) player.Creature.SetCurrentHpInternal(Math.Min(hp, player.Creature.MaxHp));
    }
}
