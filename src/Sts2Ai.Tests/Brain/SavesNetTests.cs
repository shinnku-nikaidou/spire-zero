using Sts2Ai.Brain;
using Sts2Ai.Brain.Macro;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class SavesNetTests
{
    /// <summary>
    /// f with small widths, made by <c>learn/f/export.py</c>'s net_file: pure-v-tiny-win's body under a random head of
    /// f's (learn/f/net.py), its test runs pure-v-tiny-win's holding two potions, some in NoSL. Loading checks SavesNet gives
    /// each test run's potions the trainer's f, so a change to how a run is fed to f's graph fails here.
    /// </summary>
    public static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "f-tiny.json");

    /// <summary>The SL champion's own f: loading checks SavesNet gives its test runs' potions the trainer's f.</summary>
    [Fact]
    public void ReadsTheChampionsTestRunsAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<SavesFile>(File.ReadAllText(Fixtures.ChampionNet("f-net")));
        using var net = SavesNet.Load(file);
        Assert.NotEmpty(file.Tests);
    }

    [Fact]
    public void GivesEachPotionHeldItsFAsTheTrainerDidAtAnyHp()
    {
        var file = ProtocolJson.Deserialize<SavesFile>(File.ReadAllText(Fixture));
        using var net = SavesNet.Load(file);
        Assert.Contains(file.Tests, t => t.Env == "NoSL");
        var test = file.Tests[0];
        var saves = net.Saves(test.Run, PlayEnvironment.SL);
        Assert.Equal(test.Run.Potions.Count(p => p != null), saves.Count);
        Assert.All(saves, x => Assert.True(x >= 0));
        Assert.Equal(saves, net.Saves(test.Run with { Hp = 1, MaxHp = test.Run.MaxHp + 30 }, PlayEnvironment.SL));
        Assert.Empty(net.Saves(test.Run with { Potions = [null, null, null] }, PlayEnvironment.SL));
    }
}
