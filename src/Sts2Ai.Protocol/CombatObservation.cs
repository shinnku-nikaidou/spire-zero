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
    /// <summary>The encounter's room type, as the map shows it: Monster, Elite or Boss.</summary>
    public string? Room { get; init; }
    /// <summary>Act index, from 0.</summary>
    public required int Act { get; init; }
    public required int Ascension { get; init; }
    /// <summary>The game mode as the game names it (<c>Dread</c>); null in the standard game.</summary>
    public string? Mode { get; init; }
    /// <summary>The Dread mode's dominion on this fight (<see cref="RunView.Dominion"/>); null in the standard game and in
    /// a boss's fight.</summary>
    public string? Dominion { get; init; }
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
    /// <summary>
    /// The card prompt the combat waits on, if any (a card's, a potion's, or one a turn's end or start brings). Nothing
    /// else can be done until it is answered: the actions are then its answers (<see cref="CombatAction.Pick"/>).
    /// </summary>
    public CardChoice? Prompt { get; init; }

    /// <summary>
    /// Whether <paramref name="action"/> can be taken here, given the plays the game lists as <paramref name="legal"/>:
    /// at a prompt, an answer the prompt accepts; else one of the plays.
    /// </summary>
    public bool Takes(CombatAction action, IReadOnlyList<CombatAction> legal) => action.Kind == CombatActionKind.Pick
        ? Prompt?.Accepts(action.Picks) == true
        : Prompt == null && legal.Contains(action);
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
    /// <summary>The arrows held, ready to shoot, of those the icons beside the player show; null for a player with no arrows.</summary>
    public int? Arrows { get; init; }
    /// <summary>All the arrows those icons show: the ones held and the ones stuck in enemies. A destroyed arrow is not shown.</summary>
    public int? ArrowSlots { get; init; }
    /// <summary>The melodies played this turn, as the symbols beside the player show them: of Wah, Cho and Ki, in this order.</summary>
    public IReadOnlyList<string>? Melodies { get; init; }
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
    /// <summary>For a card the Tinker Time event made (Mad Science): the type and rider chosen there, which its frame
    /// and text show.</summary>
    public TinkerView? Tinker { get; init; }
    /// <summary>Sorted.</summary>
    public IReadOnlyList<string>? Keywords { get; init; }
    /// <summary>The keywords an effect gave it for this turn only, which the turn's end takes away: Expertise's Retain,
    /// Hand Trick's Sly.</summary>
    public IReadOnlyList<string>? ThisTurn { get; init; }
    /// <summary>The card's numbers (damage, block, magic number, ...), by name, sorted by name; a calculated number by its parts.</summary>
    public IReadOnlyDictionary<string, decimal>? Vars { get; init; }
    /// <summary>The card after one more upgrade, as the game's upgrade preview shows it (the deck view's and the upgrade
    /// prompt's): only on a card of the deck that can be upgraded, in the run's deck and in a deck prompt's options.</summary>
    public CardView? Upgraded { get; init; }
    /// <summary>The energy cost once its temporary changes end (when played, when the turn ends), when that is another.</summary>
    public int? Lasting { get; init; }
    /// <summary>The same for the star cost.</summary>
    public int? StarsLasting { get; init; }
    /// <summary>How many extra times a play runs the card, when any: the "Replay" its text states.</summary>
    public int? Replay { get; init; }
    /// <summary>Set when its enchantment's once-a-combat effect is spent, which the enchantment's greyed icon shows.</summary>
    public bool? Spent { get; init; }
    /// <summary>The number its text prints from the state of the combat (no target picked), for a card in combat that has one.</summary>
    public decimal? Calc { get; init; }
    /// <summary>How far its numbers other than damage and block have moved from those of a fresh copy at its upgrade
    /// level, when they have: the Aim a Show Off has gained.</summary>
    public decimal? Grown { get; init; }
}

/// <summary>What Tinker Time made of a Mad Science: its card type (Attack, Skill, Power) and its rider effect.</summary>
public sealed record TinkerView(string Type, string Rider);

/// <summary>An enchantment or affliction on a card.</summary>
/// <param name="Extra">The damage it has gathered beyond its amount, which the card's number shows: Momentum's.</param>
public sealed record ModifierView(string Id, int Amount, int? Extra = null);

/// <summary>A power as its icon and its tooltip show it.</summary>
/// <param name="Amount">The number on the icon.</param>
/// <param name="Stacks">The power's own amount, which its tooltip states, when the icon shows another number: Sloth's
/// icon counts the cards played this turn, and its stacks are the limit.</param>
/// <param name="Card">The card the power keeps, which its tooltip names: the card Nightmare adds copies of.</param>
/// <param name="Detail">How far the number its tooltip states has moved from the power's own, when one has: Toric
/// Toughness's block, The Bomb's damage.</param>
/// <param name="Stolen">The card it took, which its tooltip names: Swipe's.</param>
public sealed record PowerView(string Id, int Amount, int? Stacks = null, CardView? Card = null, decimal? Detail = null, CardView? Stolen = null);

public sealed record OrbView(string Id, decimal Passive, decimal Evoke);

public sealed record RelicView
{
    public required string Id { get; init; }
    public int? Counter { get; init; }
    /// <summary>Set when not the normal status.</summary>
    public string? Status { get; init; }
    public bool? Melted { get; init; }
    /// <summary>Set when the relic is used up, as its tip says (a spent LIZARD_TAIL, WINGED_BOOTS with no flights left).</summary>
    public bool? UsedUp { get; init; }
    /// <summary>The cards the relic keeps, by id, as its tip lists them: PAELS_TOOTH's, given back one after each combat.</summary>
    public IReadOnlyList<string>? Cards { get; init; }
    /// <summary>Set when its once-a-combat or once-a-turn effect is spent, for what the player remembers and its status does
    /// not say: CENTENNIAL_PUZZLE's, or REGALITE's this turn.</summary>
    public bool? Spent { get; init; }
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
    /// <summary>What the icons above an enemy show, one that lies dead until it revives included: its revival is a move.</summary>
    public IReadOnlyList<IntentView>? Intents { get; init; }
    /// <summary>The player's arrows stuck in it, when any, as it shows them.</summary>
    public int? Arrows { get; init; }
}

/// <summary>An intent icon and its number, as shown above the monster.</summary>
public sealed record IntentView
{
    public required string Type { get; init; }
    /// <summary>Damage per hit against the player, for attacks.</summary>
    public int? Damage { get; init; }
    public int? Hits { get; init; }
}
