using System.Globalization;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// Self-play only: a run tilted toward card families (plans/2026-11-20-1231-silent-shiv-poison.md, 2.2; the user:
/// 「加小刀檔位」, 「得改成毒版本和小刀版本,或者混合選取的選卡方式」, then of taking every card offered: 「豈不是又會湊出來很屎的卡組」,
/// 「改成加分」). An afterstate is weighed at V times e^(<paramref name="Bonus"/> × the cards of <paramref name="Favor"/> it
/// adds to the deck), fewer where it takes some away: a bonus in log V that offsets a bias V has against a whole family,
/// while V still tells which card of it, how many copies and when. A card of <paramref name="Refuse"/> never enters the
/// deck by an answer, and a card of <paramref name="Keep"/> never leaves it by one (a removal or a transformation). At an
/// upgrade's prompt outside combat a card of <paramref name="UpgradeFirst"/> not upgraded goes before any other, the way
/// a removal's basic cards do (<see cref="RemovalOrder"/>). The Regent's Tyranny runs keep and upgrade first what they
/// favor (plans/2026-11-23-1545-sl-regent-g0.md; the user: 「暴政就拿, 優先敲, 不准刪卡」, 「不准刪暴政」). Cards the game
/// gives or takes with no choice still come and go. Everything else is V's, with the run's draws.
/// </summary>
public sealed record CardLock(IReadOnlySet<string> Favor, double Bonus, IReadOnlySet<string> Refuse, IReadOnlySet<string> Keep,
    IReadOnlySet<string> UpgradeFirst)
{
    /// <summary>As the brain's name shows it; "keep K" and "upgrade first U" only where it has them.</summary>
    public string Name => string.Create(CultureInfo.InvariantCulture, $"favor {Favor.Count} by {Bonus} refuse {Refuse.Count}")
        + (Keep.Count > 0 ? $" keep {Keep.Count}" : "") + (UpgradeFirst.Count > 0 ? $" upgrade first {UpgradeFirst.Count}" : "");

    /// <summary>Whether a deck that was <paramref name="before"/> breaks the lock as <paramref name="after"/>: more copies
    /// of a card to refuse, or fewer of a card to keep.</summary>
    public bool Breaks(IReadOnlyList<CardView> before, IReadOnlyList<CardView> after) =>
        Refuse.Any(id => Count(after, id) > Count(before, id)) || Keep.Any(id => Count(after, id) < Count(before, id));

    /// <summary>An afterstate's <paramref name="value"/> as the lock weighs it against the deck <paramref name="before"/>:
    /// −∞ where it breaks the lock, else times e^(<see cref="Bonus"/> × the favored cards it adds).</summary>
    public double Worth(IReadOnlyList<CardView> before, IReadOnlyList<CardView> after, double value)
    {
        if (Breaks(before, after)) return double.NegativeInfinity;
        int added = Favor.Sum(id => Count(after, id) - Count(before, id));
        return added == 0 || !double.IsFinite(value) ? value : value * Math.Exp(Bonus * added);
    }

    /// <summary>
    /// <paramref name="pick"/>, or where its afterstate breaks the lock and another option's does not, the best by
    /// <paramref name="values"/> of those that do not (the first on a tie).
    /// </summary>
    public int Kept(int pick, IReadOnlyList<CardView> deck, IReadOnlyList<IReadOnlyList<CardView>> afters, IReadOnlyList<double> values)
    {
        if (!Breaks(deck, afters[pick])) return pick;
        int best = -1;
        for (int k = 0; k < afters.Count; k++)
            if (!Breaks(deck, afters[k]) && (best < 0 || values[k] > values[best])) best = k;
        return best < 0 ? pick : best;
    }

    /// <summary>
    /// The options of <paramref name="choice"/> a pick takes first under <paramref name="cards"/> (null for none): at an
    /// upgrade's prompt outside combat its cards of <see cref="UpgradeFirst"/> not upgraded, else a removal's basic cards
    /// (<see cref="RemovalOrder.Firsts"/>).
    /// </summary>
    public static IReadOnlyList<int> Firsts(CardLock? cards, CardChoice choice) =>
        cards is { UpgradeFirst.Count: > 0 } && choice.Context.Purpose == "deck_for_upgrade" && !choice.Context.InCombat
            ? [.. Enumerable.Range(0, choice.Options.Count).Where(i => choice.Options[i].Up == null && cards.UpgradeFirst.Contains(choice.Options[i].Id))]
            : RemovalOrder.Firsts(choice);

    private static int Count(IReadOnlyList<CardView> deck, string id) => deck.Count(c => c.Id == id);
}
