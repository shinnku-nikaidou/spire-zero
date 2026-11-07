using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>Turns the game's card-choice prompts into the protocol's canonical <see cref="CardChoice"/> and back.</summary>
public static class CardChoices
{
    /// <summary>
    /// The prompt with its options sorted by card key, and for each canonical index the index into the game's list.
    /// Identical cards are indistinguishable to the player, but which copy is taken can still matter (two equal
    /// cards at different depths of the draw pile), so ties are put in an order drawn from <paramref name="tieBreak"/>
    /// rather than the game's order. A deck prompt's options are the deck's cards as the run view shows them, upgrade
    /// previews included (<see cref="Observe.DeckCard"/>).
    /// </summary>
    public static (CardChoice Choice, int[] GameIndex) Canonical(IReadOnlyList<CardModel> options, int min, int max, ChoiceContext context, Random tieBreak)
    {
        bool deck = context.Purpose.StartsWith("deck_", StringComparison.Ordinal);
        var sorted = options
            .Select((card, index) => (View: deck ? Observe.DeckCard(card) : Observe.Card(card), Index: index, Tie: tieBreak.Next()))
            .OrderBy(o => ProtocolJson.CardKey(o.View), StringComparer.Ordinal).ThenBy(o => o.Tie)
            .ToList();
        var choice = new CardChoice { Options = [.. sorted.Select(o => o.View)], Min = min, Max = max, Context = context };
        return (choice, sorted.Select(o => o.Index).ToArray());
    }
}
