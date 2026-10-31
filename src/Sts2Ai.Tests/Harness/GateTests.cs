using Sts2Ai.Harness;

namespace Sts2Ai.Tests;

public class GateTests
{
    private static readonly RunConfig Challenger = Fixtures.Config with { Champion = "combat pimc c7-2 8x300, combat prompts p0, macro l0, macro prompts l0" };

    /// <summary>Floors that differ by <paramref name="gain"/> on average, by gain ± 2 in turn so the variance is not 0.</summary>
    private static List<(RunRecord, RunRecord)> Pairs(int n, double gain, string? challengerOutcome = null) =>
        Enumerable.Range(0, n).Select(i => (
            Fixtures.Run($"S{i}", 20),
            Fixtures.Run($"S{i}", 20 + (int)gain + (i % 2 == 0 ? 2 : -2), challengerOutcome ?? "lost", Challenger))).ToList();

    [Fact]
    public void AClearGainPasses()
    {
        var result = Gate.Sprt(Pairs(40, 5), "floor", 1.5);
        Assert.Equal("pass", result.Verdict);
        Assert.Equal(Gate.MinPairs, result.Decided);
        Assert.Equal(5, result.FloorDiffMean);
    }

    [Fact]
    public void PairsOnlyRunsOfTheChampionsCharacterAndAscension()
    {
        var champion = EvalReport.Of([Fixtures.Run("A", 10), Fixtures.Run("B", 10)], "set", ["A", "B"], "d");
        Assert.Equal(2, Gate.Pairs(champion, [Fixtures.Run("A", 12, config: Challenger), Fixtures.Run("B", 12, config: Challenger)]).Count);
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 12, config: Challenger) with { Character = "IRONCLAD" }]));
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 12, config: Challenger) with { Ascension = 5 }]));
    }

    [Fact]
    public void NoGainFails() => Assert.Equal("fail", Gate.Sprt(Pairs(200, 0), "floor", 1.5).Verdict);

    [Fact]
    public void ANotWorseGatePassesNoLossAndFailsALoss()
    {
        Assert.Equal("pass", Gate.Sprt(Pairs(200, 0), "floor", 1.5, notWorse: true).Verdict);
        Assert.Equal("fail", Gate.Sprt(Pairs(200, -5), "floor", 1.5, notWorse: true).Verdict);
        Assert.Contains("floor, not worse, delta 1.5", Gate.Sprt(Pairs(200, 0), "floor", 1.5, notWorse: true).Summary());
    }

    [Fact]
    public void AFloorGateDecidesNothingBeforeItsMinimumPairs() =>
        Assert.Equal("open", Gate.Sprt(Pairs(Gate.MinPairs - 1, 10), "floor", 1.5).Verdict);

    [Fact]
    public void TiedPairsDoNotCountTowardsTheMinimum()
    {
        // One run in ten gains 15 floors, none of the first ten: a gain of 1.5 that ties at first.
        var pairs = Enumerable.Range(0, 200).Select(i => (
            Fixtures.Run($"S{i}", 20),
            Fixtures.Run($"S{i}", i >= 10 && i % 10 == 9 ? 35 : 20, config: Challenger))).ToList();
        var result = Gate.Sprt(pairs, "floor", 1.5);
        Assert.Equal("pass", result.Verdict);
        Assert.True(result.Decided > 10 * Gate.MinPairs);
    }

    [Fact]
    public void AChallengerThatFailsARunIsVoid()
    {
        var result = Gate.Sprt(Pairs(40, 5, "failed"), "floor", 1.5);
        Assert.Equal("void", result.Verdict);
        Assert.Contains("failed 40", result.VoidReason);
    }

    [Fact]
    public void AWinGateCountsTheWinsOnlyOneSideHas()
    {
        var result = Gate.Sprt(Pairs(30, 0, "won"), "win", 0.2);
        Assert.Equal("pass", result.Verdict);
        Assert.Equal(0, result.ChampionOnlyWins);
        Assert.Throws<ArgumentException>(() => Gate.Sprt(Pairs(30, 0), "hp", 0.2));
    }

    [Fact]
    public void PairsTheChampionsSeedsTheChallengerPlayed()
    {
        var champion = EvalReport.Of([Fixtures.Run("A", 10), Fixtures.Run("B", 11), Fixtures.Run("C", 12)], "set", ["A", "B", "C"], "d");
        var pairs = Gate.Pairs(champion, [Fixtures.Run("C", 15, config: Challenger), Fixtures.Run("A", 9, config: Challenger)]);
        Assert.Equal([("A", 10, 9), ("C", 12, 15)], pairs.Select(p => (p.Champion.Seed, p.Champion.Floor, p.Challenger.Floor)));
    }

    [Fact]
    public void RefusesChallengersPlayedElsewhereOrTwice()
    {
        var champion = EvalReport.Of([Fixtures.Run("A", 10), Fixtures.Run("B", 11)], "set", ["A", "B"], "d");
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 1, config: Challenger with { Env = "SL" })]));
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 1, config: Challenger), Fixtures.Run("A", 2, config: Challenger)]));
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 1, config: Challenger), Fixtures.Run("B", 2)]));
    }
}
