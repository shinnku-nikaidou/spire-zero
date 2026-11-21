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
    /// the trainer's value, so a change to how RunNet feeds a run's tokens to the graph fails here. Carried to the
    /// environment's mark and the event-option inputs with random weights for them, it has the first run again in NoSL,
    /// and in NoSL with an event option pending that names cards, relics, a potion and a number.
    /// </summary>
    [Fact]
    public void ValuesATinyNetsRunsAtTheTrainersChanceToWin()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json")));
        using var net = RunNet.Load(file);
        Assert.All(file.Tests, t => Assert.InRange(net.Value(t.Run, t.Env is null ? PlayEnvironment.SL : PlayEnvironment.NoSL), 0.01, 0.99));
        var plain = file.Tests.Where(t => t.Env == null).ToList();
        Assert.Equal(3, plain.Count);
        Assert.Equal(3, plain.Select(t => Math.Round(t.Value, 4)).Distinct().Count());
        var noSl = file.Tests.Single(t => t.Env == "NoSL" && t.Run.Pending == null);
        var pending = file.Tests.Single(t => t.Run.Pending != null);
        Assert.Equal("NoSL", pending.Env);
        // the mark and the option each move the value: the graph reads them
        Assert.NotEqual(net.Value(noSl.Run), noSl.Value, 4);
        Assert.NotEqual(noSl.Value, pending.Value, 4);
        Assert.Equal(noSl.Value, net.In(PlayEnvironment.NoSL).Value(noSl.Run), 4);
    }

    /// <summary>
    /// pure-v-tiny-win carried into the fight score's structure (plans/2026-11-21-1604-fight-score.md; learn/carry.py),
    /// its new heads random: loading checks RunNet gives each test run the trainer's value and each potion it holds the
    /// trainer's f, so a change to how RunNet asks the graph about potions fails here. f reads no HP, and a net of
    /// before says none.
    /// </summary>
    [Fact]
    public void SaysWhatEachPotionWillSaveAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fight-v-tiny.json")));
        using var net = RunNet.Load(file);
        var holding = file.Tests.First(t => t.Run.Potions.Any(p => p != null) && t.Env == null);
        var saves = net.Saves(holding.Run)!;
        Assert.Equal(holding.Run.Potions.Count(p => p != null), saves.Count);
        Assert.All(saves, f => Assert.True(f > 0));
        Assert.Equal(saves, net.Saves(holding.Run with { Hp = holding.Run.Hp / 2 }));
        var before = ProtocolJson.Deserialize<NetFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json")));
        using var old = RunNet.Load(before);
        Assert.Null(old.Saves(holding.Run));
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
