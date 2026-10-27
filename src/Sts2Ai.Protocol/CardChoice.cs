namespace Sts2Ai.Protocol;

/// <summary>
/// A card-choice prompt raised while a card or power resolves (discard 1, exhaust up to 2, choose 1 of 3, ...), or
/// outside combat (upgrade at a rest site, remove at the shop, ...).
/// The answer is between <see cref="Min"/> and <see cref="Max"/> distinct indexes into <see cref="Options"/>.
/// Options come in canonical order, sorted by card key, never in the game's order, which can reflect hidden state
/// such as the draw pile's order.
/// </summary>
public sealed record CardChoice
{
    public required IReadOnlyList<CardView> Options { get; init; }
    /// <summary>0 when the prompt can be cancelled or skipped: the empty answer does that.</summary>
    public required int Min { get; init; }
    public required int Max { get; init; }
    public required ChoiceContext Context { get; init; }

    /// <summary>Whether <paramref name="picks"/> is a valid answer.</summary>
    public bool Accepts(IReadOnlyList<int> picks) =>
        picks.Count >= Min && picks.Count <= Max && picks.All(i => i >= 0 && i < Options.Count) && picks.Distinct().Count() == picks.Count;
}

/// <summary>Why the game asks: the same options mean different things for an upgrade, a removal or a discard.</summary>
public sealed record ChoiceContext
{
    /// <summary>
    /// The kind of prompt, from the game's entry point: "deck_for_upgrade", "deck_for_transformation",
    /// "deck_for_enchantment", "deck_for_removal", "deck_generic", "hand", "hand_for_discard", "hand_for_upgrade",
    /// "choose_a_card_screen", "simple_grid", "simple_grid_for_rewards", or "draw_pile"/"discard_pile"/"exhaust_pile"/...
    /// for a prompt over a combat pile.
    /// </summary>
    public required string Purpose { get; init; }
    /// <summary>The id of the card, relic, power, ... that asks, when the game names one.</summary>
    public string? Source { get; init; }
    /// <summary>A cancel or skip button closes the prompt without a pick.</summary>
    public required bool Cancelable { get; init; }
    /// <summary>Asked during a combat (a card or power resolving) rather than outside one (a rest site, an event, a shop).</summary>
    public required bool InCombat { get; init; }
    /// <summary>The prompt text's loc key, "table.key".</summary>
    public string? PromptKey { get; init; }
    /// <summary>For "deck_for_enchantment": the enchantment the picked cards get.</summary>
    public ModifierView? Enchantment { get; init; }
}
