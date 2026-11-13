using System.Globalization;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>
/// A card in a loadout: its model id and upgrade count, written "BASH", "BASH+" or "BASH+2", and its enchantment
/// if any, written after "@" with its amount: "BASH@SHARP:2", "BASH+2@SHARP:2". A card that keeps properties of its
/// own in a save can be given them in brackets after its id, as NAME=VALUE with ";" between:
/// "MAD_SCIENCE[TinkerTimeType=Power;TinkerTimeRider=Improvement]".
/// </summary>
/// <param name="Props">The card's saved properties, as written between the brackets; null: as the game makes the card.</param>
internal readonly record struct CardSpec(string Id, int Upgrades = 0, string? Enchant = null, int EnchantAmount = 0, string? Props = null)
{
    /// <summary>The card as a deck's text writes it: what <see cref="Parse"/> reads.</summary>
    public string Text => Id + (Props == null ? "" : $"[{Props}]") + (Upgrades switch { 0 => "", 1 => "+", _ => $"+{Upgrades}" })
        + (Enchant == null ? "" : $"@{Enchant}:{EnchantAmount}");

    public static CardSpec Parse(string text)
    {
        string? props = null;
        if (text.IndexOf('[') is >= 0 and var open)
        {
            int close = text.IndexOf(']');
            if (close < open) throw new FormatException($"the properties of {text} are not closed by ]");
            (props, text) = (text[(open + 1)..close], text[..open] + text[(close + 1)..]);
        }
        string? enchant = null;
        int amount = 0;
        int at = text.IndexOf('@');
        if (at >= 0)
        {
            var parts = text[(at + 1)..].Split(':');
            if (parts.Length != 2) throw new FormatException($"enchantment of {text} is not ENCHANT:AMOUNT");
            (enchant, amount, text) = (parts[0], int.Parse(parts[1]), text[..at]);
        }
        int plus = text.IndexOf('+');
        if (plus < 0) return new(text, 0, enchant, amount, props);
        var count = text[(plus + 1)..];
        return new(text[..plus], count.Length == 0 ? 1 : int.Parse(count), enchant, amount, props);
    }

    public SerializableCard ToSerializable() => new()
    {
        Id = Models.Find<CardModel>(Id).Id,
        CurrentUpgradeLevel = Upgrades,
        Enchantment = Enchant == null ? null : new SerializableEnchantment { Id = Models.Find<EnchantmentModel>(Enchant).Id, Amount = EnchantAmount },
        Props = Saved(),
        FloorAddedToDeck = 1,
    };

    /// <summary>The card's saved properties as the game writes them of a card that has them set so.</summary>
    private SavedProperties? Saved()
    {
        if (Props == null) return null;
        var card = Models.Find<CardModel>(Id).ToMutable();
        foreach (string pair in Props.Split(';'))
        {
            if (pair.Split('=') is not [var name, var value]) throw new FormatException($"the property {pair} of {Id} is not NAME=VALUE");
            var property = card.GetType().GetProperty(name) is { } found && found.GetCustomAttribute<SavedPropertyAttribute>() != null ? found
                : throw new ArgumentException($"{Id} keeps no property {name} in a save");
            property.SetValue(card, property.PropertyType.IsEnum ? Enum.Parse(property.PropertyType, value, ignoreCase: true)
                : Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));
        }
        return SavedProperties.From(card);
    }

    public override string ToString() =>
        Upgrades switch { 0 => Id, 1 => Id + "+", _ => $"{Id}+{Upgrades}" } + (Props == null ? "" : $" [{Props}]") + (Enchant == null ? "" : $" ({Enchant} {EnchantAmount})");
}

/// <summary>
/// What the player brings into a combat. Null fields keep the character's starting values, so the default
/// loadout is a fresh run's. This is the final state, not a list of pickups: relics are added without running
/// their pickup effects (a max-HP relic does not raise <see cref="MaxHp"/>), except the potion slots a relic gives
/// (Potion Belt), which the potions need.
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
    /// <param name="ascension">The run's ascension level: from Tight Belt on, setting up the run takes a slot off the
    /// belt, which the game cannot do to a full one.</param>
    /// <exception cref="ArgumentException">The potions do not fit the belt.</exception>
    public void ApplyTo(Player player, int ascension)
    {
        if (Deck != null)
        {
            foreach (var card in player.Deck.Cards.ToList()) player.Deck.RemoveInternal(card, silent: true);
            foreach (var spec in Deck) player.Deck.AddInternal(CardModel.FromSerializable(spec.ToSerializable()), silent: true);
        }
        if (Relics != null)
        {
            foreach (var relic in player.Relics.ToList())
            {
                player.RemoveRelicInternal(relic, silent: true);
                player.SubtractFromMaxPotionCount(PotionSlots(relic));
            }
            foreach (var id in Relics)
            {
                var relic = Models.Find<RelicModel>(id).ToMutable();
                player.AddRelicInternal(relic, silent: true);
                player.AddToMaxPotionCount(PotionSlots(relic));
            }
        }
        if (Potions != null)
        {
            foreach (var potion in player.Potions.ToList()) player.DiscardPotionInternal(potion, silent: true);
            foreach (var id in Potions)
                if (!player.AddPotionInternal(Models.Find<PotionModel>(id).ToMutable(), silent: true).success)
                    throw new ArgumentException($"no free potion slot for {id}");
            if (Potions.Count == player.MaxPotionCount && new AscensionManager(GameMode.Standard, ascension).HasLevel(AscensionLevel.TightBelt))
                throw new ArgumentException($"{Potions.Count} potions do not fit the belt: ascension {ascension} leaves {player.MaxPotionCount - 1} slots");
        }
        if (MaxHp is int max) player.Creature.SetMaxHpInternal(max);
        if (Hp is int hp) player.Creature.SetCurrentHpInternal(Math.Min(hp, player.Creature.MaxHp));
    }

    /// <summary>
    /// The potion slots a relic gives on pickup: every such relic (Potion Belt, Phial Holster, Alchemical Coffer)
    /// passes its "PotionSlots" var to PlayerCmd.GainMaxPotionCount in AfterObtained.
    /// </summary>
    private static int PotionSlots(RelicModel relic) =>
        relic.DynamicVars.TryGetValue("PotionSlots", out var slots) ? slots.IntValue : 0;
}
