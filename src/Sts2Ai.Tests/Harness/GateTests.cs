using Sts2Ai.Harness;

namespace Sts2Ai.Tests.Harness;

public class GateTests
{
    private static readonly RunConfig Challenger = Fixtures.Config with { Champion = "combat pimc c7-2 8x300, combat prompts p0, macro l0, macro prompts l0" };

    /// <summary>
    /// <paramref name="n"/> pairs at floor 20 against 20 + <paramref name="gain"/>; the challenger alone wins every one of
    /// the first <paramref name="challengerWins"/>, the champion alone every one of the next <paramref name="championWins"/>,
    /// and both lose the rest.
    /// </summary>
    private static List<(RunRecord, RunRecord)> Pairs(int n, int challengerWins, int championWins, int gain = 0, string? challengerOutcome = null) =>
        [.. Enumerable.Range(0, n).Select(i => (
            Fixtures.Run($"S{i}", 20, i >= challengerWins && i < challengerWins + championWins ? "won" : "lost"),
            Fixtures.Run($"S{i}", 20 + gain, challengerOutcome ?? (i < challengerWins ? "won" : "lost"), Challenger)))];

    /// <summary>Pairs where one side alone wins, the challenger and the champion in turn: an even share.</summary>
    private static List<(RunRecord, RunRecord)> Even(int n) =>
        [.. Enumerable.Range(0, n).Select(i => (
            Fixtures.Run($"S{i}", 20, i % 2 == 1 ? "won" : "lost"),
            Fixtures.Run($"S{i}", 20, i % 2 == 0 ? "won" : "lost", Challenger)))];

    [Fact]
    public void AClearGainPasses()
    {
        var result = Gate.Sprt(Pairs(40, 40, 0, gain: 5), 0.2);
        Assert.Equal("pass", result.Verdict);
        Assert.Equal(0, result.ChampionOnlyWins);
        Assert.Equal(5, result.FloorDiffMean);
        Assert.Equal(result.Decided, result.ChallengerOnlyWins);
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
    public void NoReportOrGateTakesARunPlayedByTheAutoWinRule()
    {
        var ruled = Fixtures.Run("A", 12, config: Challenger with { AutoWin = [0, 1] });
        var champion = EvalReport.Of([Fixtures.Run("A", 10)], "set", ["A"], "d");
        Assert.Contains("acts 1,2", Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [ruled])).Message);
        Assert.Throws<ArgumentException>(() => EvalReport.Of([ruled], "set", ["A"], "d"));
    }

    [Fact]
    public void NoGainFails() => Assert.Equal("fail", Gate.Sprt(Even(400), 0.1).Verdict);

    [Fact]
    public void ANotWorseGatePassesNoLossAndFailsALoss()
    {
        Assert.Equal("pass", Gate.Sprt(Even(400), 0.1, notWorse: true).Verdict);
        Assert.Equal("fail", Gate.Sprt(Pairs(100, 0, 100), 0.1, notWorse: true).Verdict);
        Assert.Contains("wins, not worse, delta 0.1", Gate.Sprt(Even(400), 0.1, notWorse: true).Summary());
    }

    [Fact]
    public void PairsBothSidesWinOrLoseDecideNothing() => Assert.Equal("open", Gate.Sprt(Pairs(200, 0, 0, gain: 10), 0.1).Verdict);

    [Fact]
    public void AChallengerThatFailsARunIsVoid()
    {
        var result = Gate.Sprt(Pairs(40, 0, 0, challengerOutcome: "failed"), 0.1);
        Assert.Equal("void", result.Verdict);
        Assert.Contains("failed 40", result.VoidReason);
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
