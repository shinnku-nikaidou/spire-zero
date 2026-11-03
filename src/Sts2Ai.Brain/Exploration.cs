namespace Sts2Ai.Brain;

/// <summary>
/// Self-play exploration by even draws (docs: learning/loop §9): with chance <paramref name="share"/> ε, a macro decision's answer is
/// drawn evenly among its options, whatever V says; otherwise the brain answers. What is drawn does not depend on V or on
/// how strong the run is. Never in evaluation.
/// </summary>
public sealed class Uniform(double share, Random rng)
{
    public double Share { get; } =
        share is > 0 and <= 1 ? share : throw new ArgumentOutOfRangeException(nameof(share), "a share of decisions is above 0, at most 1");

    /// <summary>For a decision of <paramref name="options"/> options: null to leave it to the brain, else the index drawn.</summary>
    public int? Draw(int options) => rng.NextDouble() < Share ? rng.Next(options) : null;

    /// <summary>
    /// For a card prompt of <paramref name="options"/> cards that takes <paramref name="min"/> to <paramref name="max"/>
    /// picks: null to leave it to the brain, else how many picks drawn evenly, then which, evenly, in order.
    /// </summary>
    public IReadOnlyList<int>? Picks(int options, int min, int max)
    {
        if (rng.NextDouble() >= Share) return null;
        int n = min + rng.Next(max - min + 1);
        return Enumerable.Range(0, options).OrderBy(_ => rng.Next()).Take(n).Order().ToList();
    }
}
