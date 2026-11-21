using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// How the macro brain takes one of the options it has valued. In play, the best, the first on a
/// tie. In self-play some answers are drawn instead: with <paramref name="uniform"/>, evenly whatever V says, a card
/// reward's outcome among its cards and skipping it, a card prompt's picks, and on every other screen its options but
/// those that would come back to a state already decided; with <paramref name="softmax"/>, every other answer by V, a
/// softmax over the log of the options' values, at a macro decision and at each step of a card prompt's answer.
/// One per run: it remembers each card reward's draw.
/// </summary>
public sealed class MacroDraws(Uniform? uniform = null, Softmax? softmax = null)
{
    /// <summary>Each card reward's draw by <c>uniform</c> (null: none), by its floor and cards, made when it is first seen.</summary>
    private readonly Dictionary<string, int?> _draws = [];

    /// <summary>
    /// With <c>uniform</c>, the options' values with a card reward's drawn outcome first (+∞) and its others last (−∞);
    /// null where nothing is drawn. On the rewards screen, a card reward drawn to be skipped is never opened and one drawn
    /// to be taken is opened now; on the card reward screen, the drawn card, or skipping, is taken.
    /// </summary>
    public List<double>? CardReward(Observation obs, IReadOnlyList<MacroAction> actions, IReadOnlyList<double> worth)
    {
        if (uniform == null) return null;
        var values = worth.ToList();
        bool any = false;
        switch (obs.Screen)
        {
            case RewardsScreen screen:
                for (int k = 0; k < actions.Count; k++)
                {
                    if (actions[k].Verb != MacroAction.Reward || screen.Rewards.FirstOrDefault(r => r.Index == actions[k].Index) is not { Kind: "card", Cards: { } offered }) continue;
                    if (Draw(obs.Run, offered) is not { } drawn) continue;
                    values[k] = drawn == offered.Count ? double.NegativeInfinity : double.PositiveInfinity;
                    any = true;
                }
                break;
            case CardRewardScreen screen when Draw(obs.Run, screen.Cards) is { } drawn:
                var wanted = drawn < screen.Cards.Count ? MacroAction.Of(MacroAction.Card, drawn) : new MacroAction(MacroAction.CardAlt, "Skip");
                int at = actions.ToList().IndexOf(wanted);
                if (at < 0) break;
                for (int k = 0; k < actions.Count; k++) values[k] = k == at ? double.PositiveInfinity : double.NegativeInfinity;
                any = true;
                break;
        }
        return any ? values : null;
    }

    private int? Draw(RunView run, IReadOnlyList<CardView> offered)
    {
        string key = $"{run.Floor}:{string.Join(",", offered.Select(c => $"{c.Id}+{c.Up ?? 0}"))}";
        if (!_draws.TryGetValue(key, out var drawn)) _draws[key] = drawn = uniform!.Draw(offered.Count + 1);
        return drawn;
    }

    /// <summary>
    /// With <c>uniform</c>, an option drawn evenly, or null to leave it to V: on every screen but the card reward's (its
    /// outcome is drawn as a whole, <see cref="CardReward"/>), among the options not worth −∞ (they would come back to a
    /// state already decided), when there are two or more.
    /// </summary>
    public int? Even(Observation obs, IReadOnlyList<double> worth)
    {
        if (uniform == null || obs.Screen is CardRewardScreen) return null;
        var open = Enumerable.Range(0, worth.Count).Where(k => !double.IsNegativeInfinity(worth[k])).ToList();
        return open.Count >= 2 && uniform.Draw(open.Count) is { } drawn ? open[drawn] : null;
    }

    /// <summary>With <c>uniform</c>, a card prompt's picks drawn evenly (<see cref="Uniform.Picks"/>), at least <paramref name="min"/>,
    /// a removal's cards that go first before any other (<see cref="RemovalOrder"/>); or null.</summary>
    public IReadOnlyList<int>? Even(CardChoice choice, int min)
    {
        int n = choice.Options.Count;
        return uniform?.Picks(n, Math.Min(min, n), Math.Min(choice.Max, n), RemovalOrder.Firsts(choice));
    }

    /// <summary>
    /// The first best of <paramref name="values"/>, or with <c>softmax</c> a draw by their logs: V is a chance that spans
    /// orders of magnitude within a run (at A10 from 1e-3 down to 1e-7), so the temperature is in its ratios, the same at
    /// any V. An option worth nothing (or −∞, a state already decided) is never drawn, unless all are.
    /// </summary>
    /// <param name="explored">Set when the draw is valued below the best (<see cref="Softmax.Explored"/>); never cleared.</param>
    public int Pick(IReadOnlyList<double> values, ref bool explored)
    {
        int best = Best(values);
        if (softmax == null || values[best] <= 0) return best;
        var logs = values.Select(v => v > 0 ? Math.Log(v) : double.NegativeInfinity).ToList();
        int drawn = softmax.Draw(logs);
        explored |= softmax.Explored(logs[drawn], logs[best]);
        return drawn;
    }

    /// <summary>The first best of <paramref name="values"/>.</summary>
    public static int Best(IReadOnlyList<double> values)
    {
        int best = 0;
        for (int i = 1; i < values.Count; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }
}
