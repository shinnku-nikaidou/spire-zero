using Sts2Ai.Harness;

namespace Sts2Ai.Tests;

public class EvalReportTests
{
    private static readonly string[] Seeds = ["A", "B", "C"];

    [Fact]
    public void SortsTheRunsInTheSetsOrder()
    {
        var report = EvalReport.Of([Fixtures.Run("C", 10), Fixtures.Run("A", 20)], "set", Seeds, "2026-10-27");
        Assert.Equal(["A", "C"], report.Runs.Select(r => r.Seed));
        Assert.Equal(15, report.Metrics.FloorMean);
        Assert.False(report.Metrics.Void);
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
    public void ARandomDecisionOrAFailureVoidsIt()
    {
        Assert.True(EvalReport.Of([Fixtures.Run("A", 5, randomDecisions: 1)], "set", Seeds, "d").Metrics.Void);
        Assert.True(EvalReport.Of([Fixtures.Run("A", 5, outcome: "failed")], "set", Seeds, "d").Metrics.Void);
    }
}
