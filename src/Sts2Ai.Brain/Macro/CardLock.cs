using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// Self-play only: a run locked to card families (plans/2026-11-20-1231-silent-shiv-poison.md, 2.2; the user: 「加小刀檔位」,
/// 「得改成毒版本和小刀版本,或者混合選取的選卡方式」). The cards of <paramref name="Take"/> are taken from a card reward
/// whenever one is offered and never leave the deck by an answer; the cards of <paramref name="Refuse"/> never enter it by
/// one. Cards the game gives with no choice still come. Everything else is V's, with the run's draws.
/// </summary>
public sealed record CardLock(IReadOnlySet<string> Take, IReadOnlySet<string> Refuse)
{
    /// <summary>As the brain's name shows it.</summary>
    public string Name => $"take {Take.Count} refuse {Refuse.Count}";

    /// <summary>Whether a deck that was <paramref name="before"/> breaks the lock as <paramref name="after"/>: fewer
    /// copies of a card to take, or more of a card to refuse.</summary>
    public bool Breaks(IReadOnlyList<CardView> before, IReadOnlyList<CardView> after) =>
        Take.Any(id => Count(after, id) < Count(before, id)) || Refuse.Any(id => Count(after, id) > Count(before, id));

    /// <summary>
    /// The options' values with a card reward to take from first: on the rewards screen, a card reward that offers a card
    /// to take is opened now (+∞); on the card reward screen, only the cards to take are left (the others −∞), the best by
    /// V taken. Null where no card to take is offered.
    /// </summary>
    public List<double>? Forced(Observation obs, IReadOnlyList<MacroAction> actions, IReadOnlyList<double> worth)
    {
        var values = worth.ToList();
        bool any = false;
        switch (obs.Screen)
        {
            case RewardsScreen screen:
                for (int k = 0; k < actions.Count; k++)
                {
                    if (actions[k].Verb != MacroAction.Reward || screen.Rewards.FirstOrDefault(r => r.Index == actions[k].Index) is not { Kind: "card", Cards: { } offered }) continue;
                    if (!offered.Any(c => Take.Contains(c.Id))) continue;
                    values[k] = double.PositiveInfinity;
                    any = true;
                }
                break;
            case CardRewardScreen screen when screen.Cards.Any(c => Take.Contains(c.Id)):
                for (int k = 0; k < actions.Count; k++)
                {
                    bool taken = actions[k].Verb == MacroAction.Card && actions[k].Index < screen.Cards.Count && Take.Contains(screen.Cards[actions[k].Index].Id);
                    if (taken) any = true;
                    else values[k] = double.NegativeInfinity;
                }
                break;
        }
        return any ? values : null;
    }

    /// <summary>
    /// <paramref name="pick"/>, or where its afterstate breaks the lock and another option's does not, the best by
    /// <paramref name="values"/> of those that do not (the first on a tie; one not worth −∞ if there is one).
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
