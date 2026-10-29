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
        Assert.Null(file.Features);
        Assert.Equal(3, file.Tests.Count);
        Assert.Equal(3, file.Tests.Select(t => Math.Round(t.Value, 3)).Distinct().Count());
    }

    [Fact]
    public void StillReadsAV2Net()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Repo("weights", "silent", "n2-b0.03.json")));
        using var net = RunNet.Load(file);
        Assert.Null(file.State);
        Assert.NotEmpty(file.Features!);
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
}
