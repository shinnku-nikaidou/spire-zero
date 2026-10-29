
namespace Sts2Ai.Brain;

/// <summary>
/// Self-play exploration (docs: learning §5): draws one of a brain's options by the values the brain gave them, each with
/// chance ∝ exp((v − best) / τ). Options near the best are tried often and poor ones seldom; equal ones equally often.
/// <paramref name="temperature"/> τ is in the values' units, above 0. Never in evaluation.
/// </summary>
public sealed class Softmax(double temperature, Random rng)
{
    public double Temperature { get; } =
        temperature > 0 ? temperature : throw new ArgumentOutOfRangeException(nameof(temperature), "a softmax's temperature is above 0");

    /// <summary>An index into <paramref name="values"/>.</summary>
    public int Draw(IReadOnlyList<double> values)
    {
        double best = values.Max();
        var weights = values.Select(v => Math.Exp((v - best) / Temperature)).ToList();
        double r = rng.NextDouble() * weights.Sum();
        for (int i = 0; i < weights.Count - 1; i++)
            if ((r -= weights[i]) < 0) return i;
        return weights.Count - 1;
    }

    /// <summary>
    /// Whether a draw valued <paramref name="value"/> explored, against the brain's pick valued <paramref name="pick"/>:
    /// below it by more than τ / 100. Closer than that, the two are as good as tied (another order of the same turn, or
    /// float noise in a net), and the draw is as good as the pick.
    /// </summary>
    public bool Explored(double value, double pick) => pick - value > Temperature / 100;

    /// <summary>The first best of <paramref name="values"/>: what a brain takes without exploring.</summary>
    public static int Best(IReadOnlyList<double> values)
    {
        int best = 0;
        for (int i = 1; i < values.Count; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }
}

/// <summary>
/// Self-play exploration at card rewards (docs: learning §5): with chance <paramref name="share"/> per card reward, its
/// outcome is drawn evenly among its cards and skipping it, whatever V says, so that V also sees decks with the cards it
/// would not take. Never in evaluation.
/// </summary>
public sealed class CardDraw(double share, Random rng)
{
    public double Share { get; } =
        share is > 0 and <= 1 ? share : throw new ArgumentOutOfRangeException(nameof(share), "a share of card rewards is above 0, at most 1");

    /// <summary>For a reward of <paramref name="cards"/> cards: null to leave it to the brain, else the index drawn, <paramref name="cards"/> for skipping it.</summary>
    public int? Draw(int cards) => rng.NextDouble() < Share ? rng.Next(cards + 1) : null;
}
