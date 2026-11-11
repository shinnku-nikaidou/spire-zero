using Sts2Ai.Harness;
using Sts2Ai.Protocol;

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

    /// <summary>A character's two reports of one gate on <paramref name="n"/> seeds: the champion wins the first
    /// <paramref name="championWins"/>, the challenger <paramref name="challengerWins"/> from seed <paramref name="from"/> on.</summary>
    private static (EvalReport Champion, EvalReport Challenger) Sides(string character, int n, int championWins, int challengerWins, int gain = 0, int from = 0)
    {
        var seeds = Enumerable.Range(0, n).Select(i => $"S{i}").ToList();
        EvalReport Report(int first, int wins, int floor, RunConfig config) => EvalReport.Of(
            [.. seeds.Select((s, i) => Fixtures.Run(s, floor, i >= first && i < first + wins ? "won" : "lost", config) with { Character = character })], "set", seeds, "d");
        return (Report(0, championWins, 20, Fixtures.Config), Report(from, challengerWins, 20 + gain, Challenger));
    }

    [Fact]
    public void NewNetsAreJudgedByEveryCharactersPairsTogether()
    {
        // SILENT a little worse, IRONCLAD much better: together the nets pass.
        var result = NetGate.Judge([Sides("SILENT", 100, 60, 57), Sides("IRONCLAD", 100, 30, 70, gain: 4)]);
        Assert.True(result.Promote);
        Assert.Equal(Fixtures.Config.Champion, result.Against);
        Assert.Equal(["SILENT", "IRONCLAD"], result.Characters.Select(c => c.Character));
        Assert.Equal(-0.03, result.Characters[0].WinsDiff, 9);
        Assert.True(result.Characters[0].WinsLower95 < 0);
        Assert.Equal((200, 127, 90), (result.All.Pairs, result.All.Won, result.All.ChampionWon));
        Assert.Equal(0.185, result.All.WinsDiff, 9);
        // The standard error of 200 paired differences, 3 of them -1 and 40 of them +1.
        double se = Math.Sqrt((3 * Math.Pow(-1 - 0.185, 2) + 40 * Math.Pow(1 - 0.185, 2) + 157 * 0.185 * 0.185) / 199 / 200);
        Assert.Equal(se, result.All.WinsDiffSe, 9);
        Assert.Equal(0.185 - 1.645 * se, result.All.WinsLower95, 9);
        Assert.Equal(2, result.All.FloorDiff, 9);
        Assert.Contains("net gate pass", result.Summary());
    }

    [Fact]
    public void NetsNoBetterAreNotPromoted()
    {
        Assert.False(NetGate.Judge([Sides("SILENT", 100, 50, 50)]).Promote);
        // Three more wins, 8 seeds won only by the challenger and 5 only by the champion: within the noise.
        Assert.False(NetGate.Judge([Sides("SILENT", 100, 50, 53, from: 5), Sides("DEFECT", 100, 50, 50)]).Promote);
        Assert.False(NetGate.Judge([Sides("SILENT", 1, 0, 1)]).Promote);
    }

    [Fact]
    public void ANetGateRefusesReportsThatDoNotPair()
    {
        var silent = Sides("SILENT", 10, 5, 6);
        Assert.Throws<ArgumentException>(() => NetGate.Judge([]));
        Assert.Throws<ArgumentException>(() => NetGate.Judge([silent, silent]));
        var other = Sides("DEFECT", 10, 5, 6);
        Assert.Throws<ArgumentException>(() => NetGate.Judge([silent, (other.Champion with { SeedSet = "other" }, other.Challenger)]));
        Assert.Throws<ArgumentException>(() => NetGate.Judge([silent, (other.Champion, other.Challenger with { Config = Fixtures.Config })]));
        Assert.Throws<ArgumentException>(() => NetGate.Judge([(silent.Champion, other.Challenger)]));
    }

    [Fact]
    public void APromotedChampionFileNamesTheChallengerAndItsGate()
    {
        var gate = NetGate.Judge([Sides("SILENT", 100, 30, 70)]);
        var file = new ChampionFile("SL", "old", new Dictionary<string, string> { ["effort"] = "medium", ["value"] = "weights/all/v1.json" },
            new Dictionary<string, string> { ["SILENT"] = "old.json" }, "2026-01-01");
        var options = new Dictionary<string, string> { ["effort"] = "medium", ["value"] = "weights/all/v2.json" };
        var promoted = file.Promoted("new", options, "2026-02-02", new Dictionary<string, string> { ["SILENT"] = "new.json" }, gate);
        Assert.Equal(("SL", "new", "2026-02-02", "new.json"), (promoted.Env, promoted.Champion, promoted.Since, promoted.Reports["SILENT"]));
        Assert.Equal(options, promoted.Options);
        Assert.Equal(gate, ProtocolJson.Deserialize<ChampionFile>(ProtocolJson.Serialize(promoted)).Gate! with { Characters = gate.Characters });
        // Another environment's file that takes the nets has no report of its own yet, and no gate.
        var sibling = file.Promoted("new", options, "2026-02-02");
        Assert.Empty(sibling.Reports);
        Assert.Null(sibling.Gate);
    }
}
