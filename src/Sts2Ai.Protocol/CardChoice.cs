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

    /// <summary>The options grouped by what the player can tell apart (<see cref="ProtocolJson.CardKey"/>), each group's
    /// indexes in order: picking k of a group picks its first k.</summary>
    public IReadOnlyList<IReadOnlyList<int>> Groups() =>
        [.. Enumerable.Range(0, Options.Count).GroupBy(i => ProtocolJson.CardKey(Options[i])).Select(g => (IReadOnlyList<int>)[.. g])];

    /// <summary>
    /// Every answer the prompt takes, once per choice of cards the player can tell apart (k cards of a group are its first
    /// k), the fewest picks first, then in the options' order (the cards of a group are next to each other). The first is
    /// the first options, as few as the prompt takes. There can be very many: take as many as are needed.
    /// </summary>
    public IEnumerable<IReadOnlyList<int>> Answers()
    {
        var groups = Groups();
        for (int size = Min; size <= Math.Min(Max, Options.Count); size++)
            foreach (var picks in Choose(groups, 0, size))
                yield return picks;
    }

    /// <summary>Every choice of <paramref name="size"/> cards from the groups at <paramref name="from"/> and after.</summary>
    private static IEnumerable<List<int>> Choose(IReadOnlyList<IReadOnlyList<int>> groups, int from, int size)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }
        for (int g = from; g < groups.Count; g++)
            for (int k = Math.Min(size, groups[g].Count); k >= 1; k--)
                foreach (var rest in Choose(groups, g + 1, size - k))
                    yield return [.. groups[g].Take(k), .. rest];
    }
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
