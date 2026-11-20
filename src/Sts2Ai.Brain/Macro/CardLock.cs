using System.Globalization;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// Self-play only: a run tilted toward card families (plans/2026-11-20-1231-silent-shiv-poison.md, 2.2; the user:
/// 「加小刀檔位」, 「得改成毒版本和小刀版本,或者混合選取的選卡方式」, then of taking every card offered: 「豈不是又會湊出來很屎的卡組」,
/// 「改成加分」). An afterstate is weighed at V times e^(<paramref name="Bonus"/> × the cards of <paramref name="Favor"/> it
/// adds to the deck), fewer where it takes some away: a bonus in log V that offsets a bias V has against a whole family,
/// while V still tells which card of it, how many copies and when. A card of <paramref name="Refuse"/> never enters the
/// deck by an answer. Cards the game gives with no choice still come. Everything else is V's, with the run's draws.
/// </summary>
public sealed record CardLock(IReadOnlySet<string> Favor, double Bonus, IReadOnlySet<string> Refuse)
{
    /// <summary>As the brain's name shows it.</summary>
    public string Name => string.Create(CultureInfo.InvariantCulture, $"favor {Favor.Count} by {Bonus} refuse {Refuse.Count}");

    /// <summary>Whether a deck that was <paramref name="before"/> breaks the lock as <paramref name="after"/>: more copies
    /// of a card to refuse.</summary>
    public bool Breaks(IReadOnlyList<CardView> before, IReadOnlyList<CardView> after) =>
        Refuse.Any(id => Count(after, id) > Count(before, id));

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
    public int Keep(int pick, IReadOnlyList<CardView> deck, IReadOnlyList<IReadOnlyList<CardView>> afters, IReadOnlyList<double> values)
    {
        if (!Breaks(deck, afters[pick])) return pick;
        int best = -1;
        for (int k = 0; k < afters.Count; k++)
            if (!Breaks(deck, afters[k]) && (best < 0 || values[k] > values[best])) best = k;
        return best < 0 ? pick : best;
    }

    private static int Count(IReadOnlyList<CardView> deck, string id) => deck.Count(c => c.Id == id);
}
