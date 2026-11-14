using Sts2Ai.Harness;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker.Commands;

namespace Sts2Ai.SimWorker.Tests;

public class SameDecisionsTests
{
    private static DecisionEntry Entry(int id, string chosen, double ms, double value = 0.5) =>
        new("run-1", id, "SL", Surfaces.Combat, "plan c", "00ff", 3, chosen, ["end_turn"], [new Candidate(chosen, value, 0, 1)], new Budget(Beam: 8), 7, ms);

    [Fact]
    public void LogsThatDifferOnlyInTheirTimesAreTheSame()
    {
        Assert.Null(SameDecisionsCommand.Difference([Entry(0, "play:1", 10), Entry(1, "end_turn", 20)], [Entry(0, "play:1", 300), Entry(1, "end_turn", 1)]));
    }

    [Fact]
    public void TheFirstRecordThatDiffersIsNamedWithTheMemberThatDoes()
    {
        var first = new[] { Entry(0, "play:1", 10), Entry(1, "end_turn", 20), Entry(2, "play:2", 5) };
        string? chosen = SameDecisionsCommand.Difference(first, [Entry(0, "play:1", 10), Entry(1, "play:3", 20), Entry(2, "play:9", 5)]);
        Assert.StartsWith("record 1 (run run-1 decision 1, combat), chosen: \"end_turn\" against \"play:3\"", chosen);
        string? value = SameDecisionsCommand.Difference(first, [Entry(0, "play:1", 10), Entry(1, "end_turn", 20), Entry(2, "play:2", 5, value: 0.25)]);
        Assert.StartsWith("record 2 (run run-1 decision 2, combat), candidates:", value);
        Assert.Equal("3 decisions against 2", SameDecisionsCommand.Difference(first, first[..2]));
    }

    [Fact]
    public void PlansThatChooseAlikeButSearchApartDiffer()
    {
        var first = new[] { Entry(0, "play:1", 10) with { Nodes = 120, Turns = 4 } };
        Assert.StartsWith("record 0 (run run-1 decision 0, combat), nodes: 120 against 126",
            SameDecisionsCommand.Difference(first, [first[0] with { Nodes = 126 }]));
        Assert.StartsWith("record 0 (run run-1 decision 0, combat), turns: 4 against 5",
            SameDecisionsCommand.Difference(first, [first[0] with { Turns = 5 }]));
    }
}
