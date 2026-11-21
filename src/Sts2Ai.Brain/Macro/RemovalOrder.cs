using System.Collections.Frozen;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// The order in which cards leave the deck, the user's rule (2026-11-22): 「商店得加手寫規則, 打擊防禦詛咒沒刪除完畢禁止刪除別的
/// (有的時候總是刪除s級別卡或者刪除打擊+, 非常神秘),適用所有情況,從訓練到實際」. At a prompt that removes cards from the deck
/// (<see cref="Covers"/>: the merchant's removal, an event's, a relic's, the rest site's Cook), while it offers a card that
/// goes first (<see cref="First"/>), a pick takes one of those; once the prompt has none left unpicked, any card. A prompt
/// that takes more picks than it offers such cards takes them all and the rest freely; picking none, where the prompt allows
/// it, stays allowed. V chooses among what the rule leaves.
///
/// It holds wherever a removal is answered or valued, in SL and NoSL, self-play, tests and the mod: the prompt's answer,
/// greedy or on the node's clone (<see cref="MacroBrain"/>), the prompts the clone's search reaches inside a node
/// (<see cref="CloneSearch"/>), the even draws (<see cref="Uniform.Picks"/>), and a removal's predicted afterstate, by which
/// NoSL values the merchant's removal and a removal reward (<see cref="ScreenAfterstates"/>). A transform is not a removal.
/// </summary>
public static class RemovalOrder
{
    /// <summary>The game's curses (<c>CardType.Curse</c>, v0.113.1); the self-test checks the list against the game.</summary>
    public static readonly FrozenSet<string> Curses = new[]
    {
        "ASCENDERS_BANE", "BAD_LUCK", "CLUMSY", "CURSE_OF_THE_BELL", "DEBT", "DECAY", "DOUBT", "ENTHRALLED", "FOLLY",
        "GREED", "GUILTY", "INJURY", "NORMALITY", "POOR_SLEEP", "REGRET", "SHAME", "SPORE_MIND", "WRITHE",
    }.ToFrozenSet();

    /// <summary>
    /// A card that goes before any other: a character's basic Strike or Defend not upgraded (<c>STRIKE_</c> or <c>DEFEND_</c>
    /// and the character), or a curse. An upgraded Strike or Defend is any other card (the user, 2026-11-22: 「算別的」).
    /// </summary>
    public static bool First(CardView card) => Curses.Contains(card.Id)
        || card.Up == null && (card.Id.StartsWith("STRIKE_", StringComparison.Ordinal) || card.Id.StartsWith("DEFEND_", StringComparison.Ordinal));

    /// <summary>Whether the rule covers <paramref name="choice"/>: a prompt that removes cards from the deck, outside combat.</summary>
    public static bool Covers(CardChoice choice) => choice.Context.Purpose == "deck_for_removal" && !choice.Context.InCombat;

    /// <summary>The options of <paramref name="choice"/> that go first; none where the rule does not cover it.</summary>
    public static IReadOnlyList<int> Firsts(CardChoice choice) =>
        Covers(choice) ? [.. Enumerable.Range(0, choice.Options.Count).Where(i => First(choice.Options[i]))] : [];

    /// <summary>Whether <paramref name="picks"/> keep the rule: as many of the cards that go first as they can hold.</summary>
    public static bool Keeps(CardChoice choice, IReadOnlyCollection<int> picks)
    {
        var firsts = Firsts(choice);
        return picks.Count(firsts.Contains) == Math.Min(picks.Count, firsts.Count);
    }

    /// <summary>Whether a pick after <paramref name="picks"/> may take option <paramref name="option"/>: one that goes first,
    /// or any once those are all picked.</summary>
    public static bool Opens(CardChoice choice, IReadOnlyCollection<int> picks, int option)
    {
        var firsts = Firsts(choice);
        return firsts.Contains(option) || firsts.All(picks.Contains);
    }

    /// <summary>Of the cards a removal could take, those the rule lets it: the ones that go first, if there are any.</summary>
    public static IReadOnlyList<CardView> Removable(IEnumerable<CardView> cards)
    {
        var all = cards.ToList();
        return all.Any(First) ? [.. all.Where(First)] : all;
    }
}
