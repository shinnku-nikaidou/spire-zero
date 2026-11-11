using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

/// <summary>
/// The screen that asks for the decision, one variant per surface. The JSON names the variant in
/// its "surface" field. Lists in the screen's own order are the order the UI shows, which actions index into.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "surface")]
[JsonDerivedType(typeof(CombatScreen), Surfaces.Combat)]
[JsonDerivedType(typeof(CardChoiceScreen), Surfaces.CardChoice)]
[JsonDerivedType(typeof(MapScreen), Surfaces.Map)]
[JsonDerivedType(typeof(RewardsScreen), Surfaces.Rewards)]
[JsonDerivedType(typeof(CardRewardScreen), Surfaces.CardReward)]
[JsonDerivedType(typeof(BundleScreen), Surfaces.Bundle)]
[JsonDerivedType(typeof(EventScreen), Surfaces.Event)]
[JsonDerivedType(typeof(ShopScreen), Surfaces.Shop)]
[JsonDerivedType(typeof(RestScreen), Surfaces.Rest)]
[JsonDerivedType(typeof(TreasureScreen), Surfaces.Treasure)]
[JsonDerivedType(typeof(CrystalSphereScreen), Surfaces.CrystalSphere)]
public abstract record ScreenView;

public sealed record CombatScreen : ScreenView
{
    public required CombatObservation Combat { get; init; }
}

/// <summary>A card prompt, over the combat when one is going on.</summary>
public sealed record CardChoiceScreen : ScreenView
{
    public required CardChoice Choice { get; init; }
    public CombatObservation? Combat { get; init; }
}

/// <summary>The map is in <see cref="RunView.Map"/>; this is where the player can go, in the legal actions' order.</summary>
public sealed record MapScreen : ScreenView
{
    public required IReadOnlyList<Coord> Travelable { get; init; }
}

/// <summary>The rewards not taken yet.</summary>
public sealed record RewardsScreen : ScreenView
{
    public required IReadOnlyList<RewardView> Rewards { get; init; }
}

/// <summary>A reward button: the index "reward:i" takes, and what the button shows.</summary>
public sealed record RewardView
{
    public required int Index { get; init; }
    /// <summary>gold, potion, relic, card (opens a card reward), special_card, remove_card.</summary>
    public required string Kind { get; init; }
    /// <summary>The potion or relic.</summary>
    public string? Id { get; init; }
    /// <summary>The gold.</summary>
    public int? Amount { get; init; }
    /// <summary>The special card.</summary>
    public CardView? Card { get; init; }
    /// <summary>
    /// A card reward's cards. They are rolled before the screen shows, and opening the reward to look costs nothing
    /// (skipping keeps it), so they are known before it is opened.
    /// </summary>
    public IReadOnlyList<CardView>? Cards { get; init; }
}

public sealed record CardRewardScreen : ScreenView
{
    public required IReadOnlyList<CardView> Cards { get; init; }
    /// <summary>The buttons besides the cards (skip, reroll, ones added by relics), by id.</summary>
    public required IReadOnlyList<string> Alternatives { get; init; }
}

public sealed record BundleScreen : ScreenView
{
    public required IReadOnlyList<IReadOnlyList<CardView>> Bundles { get; init; }
}

/// <summary>
/// An event page. Texts are loc keys; the numbers the game fills into them are not included yet, since an event can
/// keep values the page doesn't show among them.
/// </summary>
public sealed record EventScreen : ScreenView
{
    public required string Id { get; init; }
    /// <summary>The page text's loc key, "table.key".</summary>
    public string? Page { get; init; }
    public required IReadOnlyList<EventOptionView> Options { get; init; }
    public required bool Finished { get; init; }
}

public sealed record EventOptionView
{
    /// <summary>What "event:i" picks.</summary>
    public required int Index { get; init; }
    /// <summary>The option's text key, which names it within the event.</summary>
    public required string Key { get; init; }
    public bool? Locked { get; init; }
    /// <summary>The relic an ancient's option gives.</summary>
    public string? Relic { get; init; }
    /// <summary>The cards, relics, potions, ... its hover tips show, by id.</summary>
    public IReadOnlyList<string>? Tips { get; init; }
    /// <summary>The button warns that it would kill the player.</summary>
    public bool? Kills { get; init; }
}

public sealed record ShopScreen : ScreenView
{
    /// <summary>The goods still for sale.</summary>
    public required IReadOnlyList<ShopEntryView> Entries { get; init; }
    /// <summary>The card removal's price; null once used.</summary>
    public int? RemovalCost { get; init; }
}

public sealed record ShopEntryView
{
    /// <summary>What "buy:SLOT" buys.</summary>
    public required int Slot { get; init; }
    /// <summary>card, relic, potion.</summary>
    public required string Kind { get; init; }
    public CardView? Card { get; init; }
    /// <summary>The relic or potion.</summary>
    public string? Id { get; init; }
    public required int Cost { get; init; }
    public bool? OnSale { get; init; }
}

public sealed record RestScreen : ScreenView
{
    public required IReadOnlyList<RestOptionView> Options { get; init; }
}

/// <param name="Heal">For HEAL: the HP it heals, as its description shows (relics change it).</param>
public sealed record RestOptionView(string Id, bool Enabled, int? Heal = null);

public sealed record TreasureScreen : ScreenView
{
    /// <summary>The relics in the chest, as "relic:i" indexes them.</summary>
    public required IReadOnlyList<string> Relics { get; init; }
}

/// <summary>
/// The crystal sphere grid. <see cref="Rows"/>[y][x] is cell x,y: '#' still hidden, '.' uncovered and empty, or what
/// the uncovered cell shows part of: 'R' relic, 'P' potion, 'C' card reward, 'G' gold, 'X' curse.
/// </summary>
public sealed record CrystalSphereScreen : ScreenView
{
    public required IReadOnlyList<string> Rows { get; init; }
    public required int DivinationsLeft { get; init; }
}
