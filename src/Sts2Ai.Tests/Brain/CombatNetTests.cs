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
        Assert.Equal(3, file.Tests.Count);
        Assert.Equal(3, file.Tests.Select(t => Math.Round(t.PotionsUsed, 2)).Distinct().Count());
    }

    // OLD NETS: this test and the next one's c2 half; remove once no champion uses them.
    [Fact]
    public void StillReadsC2()
    {
        using var net = CombatNet.Load(ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Repo("weights", "silent", "c2.json"))));
    }

    /// <summary>
    /// An untrained net of target each_potion with small widths, made by <c>learn/combat/export.py</c>'s net_file: c2's
    /// test states, with two potions of one id, a potion off the table, a state at 5 % HP and a multi-hit attack: a change
    /// to what either side reads fails on loading.
    /// </summary>
    private static string EachPotionFixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-each-potion-tiny.json");

    [Fact]
    public void ReadsAnEachPotionNetsStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(EachPotionFixture));
        using var net = CombatNet.Load(file);
        Assert.Equal(file.Vocab["potions"].Count, file.Tests[0].PotionUsed!.Count);
        Assert.True(file.Tests[0].PotionUsed!.Distinct().Count() > 1);
        Assert.Contains(file.Tests, t => t.Obs.Potions.Count(p => p == "FIRE_POTION") == 2);
    }

    [Fact]
    public void AStateInProgressIsItsEndingsAndThePotionsKept()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(EachPotionFixture));
        using var net = CombatNet.Load(file);
        double width = (1 - file.LossMin) / file.Buckets;
        foreach (var test in file.Tests)
        {
            double share = test.Obs.Player.Hp / (double)test.Obs.Player.MaxHp, value = 0;
            for (int k = 1; k <= file.Buckets; k++) value += test.Outcome![k] * (0.2 + 0.6 * (share - (file.LossMin + (k - 0.5) * width)));
            double kept = test.Obs.Potions.OfType<string>().Sum(p => 1 - test.PotionUsed![Math.Max(0, file.Vocab["potions"].ToList().IndexOf(p))]);
            Assert.InRange(net.Value(CombatStatus.InProgress, test.Obs, test.History) - (value + (1 - test.Outcome![0]) * 0.02 * kept), -1e-3, 1e-3);
        }
    }

    [Fact]
    public void ABucketTheHpHeldRulesOutHasNoWeight()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(EachPotionFixture));
        double width = (1 - file.LossMin) / file.Buckets;
        var low = file.Tests.First(t => t.Obs.Player.Hp * 20 <= t.Obs.Player.MaxHp);
        double share = low.Obs.Player.Hp / (double)low.Obs.Player.MaxHp;
        for (int k = 0; k < file.Buckets; k++)
            if (file.LossMin + k * width >= share) Assert.Equal(0, low.Outcome![1 + k]);
        Assert.True(low.Outcome![1] > 0);
    }

    [Fact]
    public void AFinishedStateAndTheBoundAreLeafValues()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(EachPotionFixture));
        using var net = CombatNet.Load(file);
        var (obs, history) = (file.Tests[0].Obs, file.Tests[0].History);
        Assert.Equal(LeafValue.Of(CombatStatus.Won, obs), net.Value(CombatStatus.Won, obs, history));
        Assert.Equal(0, net.Value(CombatStatus.Lost, obs, history));
    }

    /// <summary>A V that likes HP, except at 30, and potions, half a floor each.</summary>
    private sealed class Dip : IRunValue
    {
        public string Name => "dip";

        public double Value(RunView run) => run.Floor + run.Hp / 10.0 - (run.Hp == 30 ? 5 : 0) + 0.5 * run.Potions.Count(p => p != null);
    }

    /// <summary>
    /// An untrained worth net with small widths, its test states c2's with a modifier's amount, a changed cost, a relic's
    /// counter, status and melting and a multi-hit attack added, made by <c>learn/combat/export.py</c>'s net_file: a
    /// change to what either side reads fails here.
    /// </summary>
    private static string WorthFixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-worth-tiny.json");

    [Fact]
    public void ReadsAWorthNetsStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(WorthFixture));
        using var net = CombatNet.Load(file);
        Assert.True(net.IsWorth);
        Assert.Equal(4, file.WorthCap);
        Assert.True(file.Tests.Select(t => Math.Round(t.Worth, 3)).Distinct().Count() > 1);
        var (obs, history) = (file.Tests[0].Obs, file.Tests[0].History);
        Assert.Throws<InvalidOperationException>(() => net.Value(CombatStatus.InProgress, obs, history));
    }

    [Fact]
    public void VPricesAWinByItsHpNeverLowerForMoreAndByThePotionsItKeeps()
    {
        var run = Fixtures.Floor1() with { Floor = 10, Hp = 40, MaxHp = 50, Potions = ["B", null, "A"] };
        var prices = PricedLeaf.Of(new Dip(), run);
        Assert.Equal(10 + 4 + 1, prices.Before, 9);
        Assert.Equal(["", "A", "B", "A,B"], prices.Hp.Keys);
        Assert.Equal(51, prices.Hp["A,B"].Count);
        Assert.Equal(10 + 0.1 + 1, prices.Hp["A,B"][0], 9); // alive with no HP reads as 1 HP
        Assert.Equal(prices.Hp["A,B"][29], prices.Hp["A,B"][30]); // V's dip at 30 does not lower the price of more HP
        Assert.Equal(10 + 5, prices.Hp[""][50], 9);
        Assert.Equal(10 + 5 + 0.5, prices.Hp["A"][50], 9);
    }

    [Fact]
    public void AtVsPricesALossIsNothingAWinItsHpAndPotionsAndAStateInProgressTheNetsShare()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(WorthFixture));
        using var net = CombatNet.Load(file);
        var (obs, history) = (file.Tests[0].Obs, file.Tests[0].History);
        var table = (IReadOnlyList<double>)Enumerable.Range(0, obs.Player.MaxHp + 1).Select(h => 10 + h / 10.0).ToList();
        var prices = new EndPrices(7, new Dictionary<string, IReadOnlyList<double>> { [""] = table, ["A"] = table.Select(x => x + 1).ToList() }, ["A"]);
        var leaf = new PricedLeaf(net, prices);
        Assert.Equal(0, leaf.Value(CombatStatus.Lost, obs, history));
        var plain = obs with { Potions = [null, null, null] };
        Assert.Equal(10 + obs.Player.Hp / 10.0, leaf.Value(CombatStatus.Won, plain, history), 9);
        Assert.Equal(11 + obs.Player.Hp / 10.0, leaf.Value(CombatStatus.Won, plain with { Potions = ["A", null, null] }, history), 9);
        Assert.Equal(10 + obs.Player.Hp / 10.0, leaf.Value(CombatStatus.Won, plain with { Potions = ["GAINED", null, null] }, history), 9);
        Assert.Equal(7 * net.Worth(obs, history), leaf.Value(CombatStatus.InProgress, obs, history), 9);
    }
}
