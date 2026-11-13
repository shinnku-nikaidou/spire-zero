using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class RunNetTests
{
    /// <summary>
    /// An untrained V net with small widths, its hazard head's bias lowered by 6 so a run survives most floors, its rows for
    /// a card's damage, block and modifiers' amounts random, its test runs real views (Fixtures' floor 1 and two of g3, one
    /// with a Mad Science of Tinker Time and cards with keywords; the first's first card given a Goopy of amount 4 and a
    /// growth of 4)
    /// valued by the trainer at their chance to win (<c>learn/v/net.py</c>'s win_chance): loading checks RunNet gives each
    /// the trainer's value, so a change to how RunNet feeds a run's tokens to the graph fails here.
    /// </summary>
    [Fact]
    public void ValuesATinyNetsRunsAtTheTrainersChanceToWin()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json")));
        using var net = RunNet.Load(file);
        Assert.All(file.Tests, t => Assert.InRange(net.Value(t.Run), 0.01, 0.99));
        Assert.Equal(3, file.Tests.Select(t => Math.Round(t.Value, 2)).Distinct().Count());
    }

    /// <summary>The champion's own file: loading checks RunNet gives its test runs the trainer's values.</summary>
    [Fact]
    public void ReadsTheChampionsTestRunsAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Fixtures.ChampionNet("value")));
        using var net = RunNet.Load(file);
        Assert.Equal(8, file.Tests.Count);
    }

    private static MapPointView Point(int col, int row, string type, bool? quest = null, params (int Col, int Row)[] next) =>
        new() { Col = col, Row = row, Type = type, Quest = quest, Next = [.. next.Select(n => new Coord(n.Col, n.Row))] };

    // As learn/tests' MAP: start (0,0) -> (0,1) Monster or (1,1) Elite; both -> (0,2) RestSite -> (0,3) Boss; (2,1)
    // leads nowhere from here.
    private static MapView Map(params (int Col, int Row)[] visited) => new()
    {
        Points =
        [
            Point(0, 0, "Ancient", null, (0, 1), (1, 1)), Point(0, 1, "Monster", null, (0, 2)),
            Point(1, 1, "Elite", true, (0, 2)), Point(2, 1, "Shop", null, (0, 2)), Point(0, 2, "RestSite", null, (0, 3)),
            Point(0, 3, "Boss"),
        ],
        Visited = [.. visited.Select(v => new Coord(v.Col, v.Row))],
        Boss = "B",
    };

    [Fact]
    public void TheMapIsEveryPointAPathStillLeadsToWithItsFloorsAhead()
    {
        Assert.Equal(["Monster+1", "Elite+1:quest", "RestSite+2", "Boss+3"], RunNet.MapTokens(Map((0, 0))));
        Assert.Equal(["RestSite+1", "Boss+2"], RunNet.MapTokens(Map((0, 0), (1, 1))));
        Assert.Equal(RunNet.MapTokens(Map((0, 0))), RunNet.MapTokens(Map()));
        Assert.Empty(RunNet.MapTokens(Map((0, 0), (0, 1), (0, 2), (0, 3))));
    }

    [Fact]
    public void AMapMoveReadsThePointEnteredAndOnlyThen()
    {
        Assert.Equal(["Elite+0:quest", "RestSite+1", "Boss+2"], RunNet.MapTokens(Map((0, 0), (1, 1)) with { Entering = true }));
        Assert.Equal(["RestSite+1", "Boss+2"], RunNet.MapTokens(Map((0, 0), (1, 1))));
    }
}
