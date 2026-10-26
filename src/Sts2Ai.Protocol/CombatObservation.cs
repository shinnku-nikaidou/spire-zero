namespace Sts2Ai.Protocol;

/// <summary>
/// What the player can see of a combat. This is the only view of the game the brain gets, so every field is
/// whitelisted from what the game's UI shows. Hidden information never appears: the draw pile's order (it is listed
/// sorted, as a multiset), seeds and RNG state, which move a monster has queued (only its intents), and anything
/// about future turns.
///
/// Uids are the game's own per-combat ids, the ones its multiplayer messages use, so an action names the same card
/// or creature in the sim and in the real game. Only things the player can point at have one: the cards in hand and
/// the creatures. Cards in the draw, discard and exhaust piles have none, because the game numbers cards right after
/// the opening shuffle, so a card's id is its position in the shuffled draw pile.
/// </summary>
public sealed record CombatObservation
{
    /// <summary>The encounter's id: which monsters, in which places, under the fight's own rules.</summary>
    public required string? Encounter { get; init; }
    /// <summary>Act index, from 0.</summary>
    public required int Act { get; init; }
    public required int Ascension { get; init; }
    public required int Round { get; init; }
    public required PlayerView Player { get; init; }
    public required IReadOnlyList<CardView> Hand { get; init; }
    /// <summary>Sorted by card key: the real order is hidden.</summary>
    public required IReadOnlyList<CardView> Draw { get; init; }
    public required IReadOnlyList<CardView> Discard { get; init; }
    public required IReadOnlyList<CardView> Exhaust { get; init; }
    /// <summary>One entry per potion slot: the potion's id, or null for an empty slot.</summary>
    public required IReadOnlyList<string?> Potions { get; init; }
    public required IReadOnlyList<RelicView> Relics { get; init; }
    /// <summary>Allies other than the player (other players, summons).</summary>
    public required IReadOnlyList<CreatureView> Allies { get; init; }
    public required IReadOnlyList<CreatureView> Enemies { get; init; }
}

public sealed record PlayerView
{
    public required uint? Uid { get; init; }
    public required string Character { get; init; }
    public required int Hp { get; init; }
    public required int MaxHp { get; init; }
    public required int Block { get; init; }
    public required int Energy { get; init; }
    public required int MaxEnergy { get; init; }
    public required int Stars { get; init; }
    public required int Gold { get; init; }
    public required IReadOnlyList<PowerView> Powers { get; init; }
    public required int OrbSlots { get; init; }
    public required IReadOnlyList<OrbView> Orbs { get; init; }
}

/// <summary>
/// A card as the player sees it. Leaving out the uid, two cards that serialize the same are indistinguishable to
/// the player; that serialization is the card's key, used wherever an order must not depend on hidden state.
/// </summary>
public sealed record CardView
{
    public required string Id { get; init; }
    /// <summary>Only for cards in hand.</summary>
    public uint? Uid { get; init; }
    /// <summary>Upgrade level, when upgraded.</summary>
    public int? Up { get; init; }
    /// <summary>Energy cost with all modifiers; null for an X-cost card.</summary>
    public int? Cost { get; init; }
    public bool? XCost { get; init; }
    /// <summary>Star cost with modifiers, for cards that have one.</summary>
    public int? Stars { get; init; }
    public ModifierView? Enchant { get; init; }
    public ModifierView? Affliction { get; init; }
    /// <summary>Sorted.</summary>
    public IReadOnlyList<string>? Keywords { get; init; }
    /// <summary>The card's numbers (damage, block, magic number, ...), by name, sorted by name.</summary>
    public IReadOnlyDictionary<string, decimal>? Vars { get; init; }
}

/// <summary>An enchantment or affliction on a card.</summary>
public sealed record ModifierView(string Id, int Amount);

public sealed record PowerView(string Id, int Amount);

public sealed record OrbView(string Id, decimal Passive, decimal Evoke);

public sealed record RelicView
{
    public required string Id { get; init; }
    public int? Counter { get; init; }
    /// <summary>Set when not the normal status.</summary>
    public string? Status { get; init; }
    public bool? Melted { get; init; }
}

public sealed record CreatureView
{
    public required uint? Uid { get; init; }
    /// <summary>The monster's id; null for a creature that is not a monster.</summary>
    public required string? Id { get; init; }
    public required int Hp { get; init; }
    public required int MaxHp { get; init; }
    public required int Block { get; init; }
    public required IReadOnlyList<PowerView> Powers { get; init; }
    /// <summary>What the icons above a living enemy show.</summary>
    public IReadOnlyList<IntentView>? Intents { get; init; }
}

/// <summary>An intent icon and its number, as shown above the monster.</summary>
public sealed record IntentView
{
    public required string Type { get; init; }
    /// <summary>Damage per hit against the player, for attacks.</summary>
    public int? Damage { get; init; }
    public int? Hits { get; init; }
}
