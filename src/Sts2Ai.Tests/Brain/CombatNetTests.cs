using Sts2Ai.Brain;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.Tests;

public class CombatNetTests
{
    private static string Repo(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Sts2Ai.sln"))) dir = dir.Parent!;
        return Path.Combine([dir.FullName, .. path]);
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-sl-tiny.json");

    /// <summary>
    /// An untrained SL combat net with small widths, its test states three real SL round starts (one with four rounds
    /// of an enemy's history, one with an ally), made by <c>learn/combat/export.py</c>'s net_file; loading checks
    /// CombatNet predicts each as the trainer did, so a change to <c>learn/combat/encode.py</c> or to CombatNet that
    /// reads a state otherwise fails here.
    /// </summary>
    [Fact]
    public void ReadsAnSlNetsStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file);
        Assert.Equal("SL", net.Env);
        Assert.Equal(3, file.Tests.Count);
        Assert.Equal(3, file.Tests.Select(t => Math.Round(t.PotionsUsed, 2)).Distinct().Count());
    }

    // OLD NETS: this test and the next one's c2 half; remove once no champion uses them.
    [Fact]
    public void StillReadsC2()
    {
        using var net = CombatNet.Load(ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Repo("weights", "silent", "c2.json"))));
        Assert.Null(net.Env);
    }

    [Fact]
    public void ANetPlaysOnlyInItsEnvironmentAndAnOldOneInEither()
    {
        var sl = LeafChoice.Read(null, Fixture);
        sl.Check(PlayEnvironment.SL);
        Assert.Contains("SL's endings, not NoSL's", Assert.Throws<NotSupportedException>(() => sl.Check(PlayEnvironment.NoSL)).Message);
        var c2 = LeafChoice.Read(null, Repo("weights", "silent", "c2.json"));
        c2.Check(PlayEnvironment.SL);
        c2.Check(PlayEnvironment.NoSL);
    }

    /// <summary>A V that likes HP, except at 30, and potions, half a floor each.</summary>
    private sealed class Dip : IRunValue
    {
        public string Name => "dip";

        public double Value(RunView run) => run.Floor + run.Hp / 10.0 - (run.Hp == 30 ? 5 : 0) + 0.5 * run.Potions.Count(p => p != null);
    }

    [Fact]
    public void VPricesADeathAtTheFloorAWinByItsHpNeverLowerForMoreAndAPotionByWhatVGivesUpWithIt()
    {
        var run = Fixtures.Floor1() with { Floor = 10, Hp = 40, MaxHp = 50, Potions = ["A", null, "B"] };
        var prices = PricedLeaf.Of(new Dip(), run);
        Assert.Equal(10, prices.Death);
        Assert.Equal(51, prices.Hp.Count);
        Assert.Equal(10 + 0.1 + 1, prices.Hp[0], 9); // alive with no HP reads as 1 HP
        Assert.Equal(prices.Hp[29], prices.Hp[30]); // V's dip at 30 does not lower the price of more HP
        Assert.Equal(10 + 5 + 1, prices.Hp[50], 9);
        Assert.Equal(0.5, prices.Potion, 9);
        Assert.Equal(2, prices.Potions);
    }

    [Fact]
    public void AtVsPricesALossIsTheDeathsPriceAWinItsHpsLessThePotionsUsedAndAStateInProgressBetween()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file);
        var (obs, history) = (file.Tests[0].Obs, file.Tests[0].History);
        int held = obs.Potions.Count(p => p != null);
        var prices = new EndPrices(5, Enumerable.Range(0, obs.Player.MaxHp + 1).Select(h => 10 + h / 10.0).ToList(), 0.3, held + 1);
        Assert.Equal(5, net.Value(CombatStatus.Lost, obs, history, prices));
        Assert.Equal(10 + obs.Player.Hp / 10.0 - 0.3, net.Value(CombatStatus.Won, obs, history, prices), 9); // one potion used since
        double going = net.Value(CombatStatus.InProgress, obs, history, prices);
        Assert.InRange(going, 5 - 1, 10 + obs.Player.MaxHp / 10.0);
        Assert.Equal(going, new PricedLeaf(net, prices).Value(CombatStatus.InProgress, obs, history));
    }
}
