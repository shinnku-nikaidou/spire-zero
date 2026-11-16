using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Harness;

public class EvalReportTests
{
    private static readonly string[] Seeds = ["A", "B", "C"];

    [Fact]
    public void ADreadReportIsRankedApartFromTheStandardGames()
    {
        var standard = EvalReport.Of([Fixtures.Run("A", 10)], "set", ["A"], "d");
        var dread = EvalReport.Of([Fixtures.Run("A", 10, config: Fixtures.Config with { Mode = "Dread" })], "set", ["A"], "d");
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 10), Fixtures.Run("B", 10, config: Fixtures.Config with { Mode = "Dread" })], "set", ["A", "B"], "d"));
        var boards = Leaderboard.Markdown([standard, dread], []);
        Assert.Contains("## SILENT, ascension 0: set", boards);
        Assert.Contains("## SILENT, ascension 0, Dread: set", boards);
        Assert.Contains("SILENT A0 Dread |", dread.Markdown());
    }

    [Fact]
    public void SortsTheRunsInTheSetsOrder()
    {
        var report = EvalReport.Of([Fixtures.Run("C", 10), Fixtures.Run("A", 20)], "set", Seeds, "2026-10-27");
        Assert.Equal(["A", "C"], report.Runs.Select(r => r.Seed));
        Assert.Equal(15, report.Metrics.FloorMean);
        Assert.False(report.Metrics.Void);
    }

    [Fact]
    public void ReadLeavesOutFailedRuns()
    {
        var file = Path.GetTempFileName();
        try
        {
            RunRecord[] runs = [Fixtures.Run("A", 5), Fixtures.Run("B", 5, outcome: "failed"), Fixtures.Run("C", 5)];
            File.WriteAllLines(file, runs.Select(ProtocolJson.Serialize));
            Assert.Equal(["A", "C"], RunRecord.Read(file).Select(r => r.Seed));
            var report = EvalReport.Of(RunRecord.Read(file), "set", RunRecord.SeedsLeft(file, Seeds), "d");
            Assert.Equal(2, report.SetSize);
            Assert.False(report.Metrics.Void);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void RejectsRunsThatBreakTheSet()
    {
        Assert.Throws<ArgumentException>(() => EvalReport.Of([], "set", Seeds, "d"));
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 1), Fixtures.Run("A", 2)], "set", Seeds, "d"));
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("Z", 1)], "set", Seeds, "d"));
        var other = Fixtures.Config with { Commit = "def5678" };
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 1), Fixtures.Run("B", 1, config: other)], "set", Seeds, "d"));
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 1), Fixtures.Run("B", 1) with { Character = "IRONCLAD" }], "set", Seeds, "d"));
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 1), Fixtures.Run("B", 1) with { Ascension = 1 }], "set", Seeds, "d"));
    }

    [Fact]
    public void APlaceholdersDecisionOrAFailureVoidsIt()
    {
        Assert.True(EvalReport.Of([Fixtures.Run("A", 5, randomDecisions: 1)], "set", Seeds, "d").Metrics.Void);
        Assert.True(EvalReport.Of([Fixtures.Run("A", 5) with { Brains = new Dictionary<string, int> { ["play"] = 3 } }], "set", Seeds, "d").Metrics.Void);
        Assert.True(EvalReport.Of([Fixtures.Run("A", 5, outcome: "failed")], "set", Seeds, "d").Metrics.Void);
    }
}
