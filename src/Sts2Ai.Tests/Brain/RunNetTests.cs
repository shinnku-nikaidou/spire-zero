using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class RunNetTests
{
    private static string Repo(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Sts2Ai.sln"))) dir = dir.Parent!;
        return Path.Combine([dir.FullName, .. path]);
    }

    /// <summary>
    /// An untrained pure V net with small widths, its test runs real views (Fixtures' floor 1 and two of g3), made by
    /// <c>learn/v/export.py</c>'s net_file; loading checks RunNet gives each the trainer's value, so a change to
    /// <c>learn/v/encode.py</c> or to RunNet that reads a run otherwise fails here.
    /// </summary>
    [Fact]
    public void ReadsAPureVNetsRunsAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny.json")));
        using var net = RunNet.Load(file);
        Assert.Equal(3, file.Tests.Count);
        Assert.Equal(3, file.Tests.Select(t => Math.Round(t.Value, 3)).Distinct().Count());
    }

    /// <summary>
    /// The same net as a "win" net (plan v7), its hazard head's bias lowered by 6 so a run survives most floors, its
    /// test runs valued by the trainer at their chance to win (<c>learn/v/net.py</c>'s win_chance): loading checks
    /// RunNet's chance to survive every floor is the trainer's.
    /// </summary>
    [Fact]
    public void ValuesAWinNetsRunsAtTheTrainersChanceToWin()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json")));
        using var net = RunNet.Load(file);
        Assert.Equal("win", file.Objective);
        Assert.All(file.Tests, t => Assert.InRange(net.Value(t.Run), 0.01, 0.99));
        Assert.Equal(3, file.Tests.Select(t => Math.Round(t.Value, 2)).Distinct().Count());
    }

    /// <summary>The champion's own file: its test runs are the ones it plays from.</summary>
    [Fact]
    public void ReadsV7_1fsRunsAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Repo("weights", "silent", "v7-1f.json")));
        using var net = RunNet.Load(file);
        Assert.Equal("win", file.Objective);
        Assert.Contains(file.Vocab["map"], t => t.Contains("+0"));
    }

    private static MapPointView Point(int col, int row, string type, bool? quest = null, params (int Col, int Row)[] next) =>
        new() { Col = col, Row = row, Type = type, Quest = quest, Next = next.Select(n => new Coord(n.Col, n.Row)).ToList() };

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
        Visited = visited.Select(v => new Coord(v.Col, v.Row)).ToList(),
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

    // As learn/tests/test_v_encode.py's test of the point entered.
    [Fact]
    public void AMapMoveReadsThePointEnteredAndOnlyThen()
    {
        Assert.Equal(["Elite+0:quest", "RestSite+1", "Boss+2"], RunNet.MapTokens(Map((0, 0), (1, 1)) with { Entering = true }));
        Assert.Equal(["RestSite+1", "Boss+2"], RunNet.MapTokens(Map((0, 0), (1, 1))));
    }

    /// <summary>A net from before the entering tokens has none in its map table: it reads a map move as it always did.</summary>
    [Fact]
    public void ANetWhoseTableLacksThePointEnteredLeavesItOut()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny.json")))!;
        using var net = RunNet.Load(file);
        var run = file.Tests[0].Run;
        Assert.DoesNotContain(file.Vocab["map"], t => t.Contains("+0"));
        Assert.Equal(net.Value(run), net.Value(run with { Map = run.Map with { Entering = true } }));
    }
}
