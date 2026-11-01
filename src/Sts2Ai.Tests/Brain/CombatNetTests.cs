using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class CombatNetTests
{
    private static string Repo(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Sts2Ai.sln"))) dir = dir.Parent!;
        return Path.Combine([dir.FullName, .. path]);
    }

    /// <summary>
    /// An untrained net with small widths, made by <c>learn/combat/export.py</c>'s net_file: real SL round starts, with four
    /// rounds of an enemy's history, an ally, two potions of one id, a potion off the table, a state at 5 % HP and a multi-hit
    /// attack, orbs on and off the table past the places the net tells apart: a change to <c>learn/combat/encode.py</c> or to CombatNet that reads a state otherwise fails on loading.
    /// </summary>
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");

    /// <summary>The champion's own file: its test states are the ones it plays from.</summary>
    [Fact]
    public void ReadsC8_1sStatesAsC7_2sTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Repo("weights", "silent", "c8-1.json")));
        using var net = CombatNet.Load(file);
        Assert.Equal(16, file.Tests.Count);
    }

    [Fact]
    public void ReadsItsStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file);
        Assert.Equal(file.Vocab["potions"].Count, file.Tests[0].PotionUsed.Count);
        Assert.True(file.Tests[0].PotionUsed.Distinct().Count() > 1);
        Assert.Contains(file.Tests, t => t.Obs.Potions.Count(p => p == "FIRE_POTION") == 2);
        Assert.Contains(file.Tests, t => t.Obs.Allies.Any(a => a.Hp > 0));
        Assert.Contains(file.Tests, t => t.Obs.Player.Orbs.Count > 10);
        Assert.Contains(file.Tests, t => t.Obs.Hand.Any(c => c.Vars?.ContainsKey("Damage") == true));
    }

    [Fact]
    public void AStateInProgressIsItsEndingsAndThePotionsKept()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file);
        double width = (1 - file.LossMin) / file.Buckets;
        foreach (var test in file.Tests)
        {
            double share = test.Obs.Player.Hp / (double)test.Obs.Player.MaxHp, value = 0;
            for (int k = 1; k <= file.Buckets; k++) value += test.Outcome[k] * (0.2 + 0.6 * (share - (file.LossMin + (k - 0.5) * width)));
            double kept = test.Obs.Potions.OfType<string>().Sum(p => 1 - test.PotionUsed[Math.Max(0, file.Vocab["potions"].ToList().IndexOf(p))]);
            Assert.InRange(net.Value(CombatStatus.InProgress, test.Obs, test.History) - (value + (1 - test.Outcome[0]) * 0.02 * kept), -1e-3, 1e-3);
        }
    }

    [Fact]
    public void ABucketTheHpHeldRulesOutHasNoWeight()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        double width = (1 - file.LossMin) / file.Buckets;
        var low = file.Tests.First(t => t.Obs.Player.Hp * 20 <= t.Obs.Player.MaxHp);
        double share = low.Obs.Player.Hp / (double)low.Obs.Player.MaxHp;
        for (int k = 0; k < file.Buckets; k++)
            if (file.LossMin + k * width >= share) Assert.Equal(0, low.Outcome[1 + k]);
        Assert.True(low.Outcome[1] > 0);
    }

    [Fact]
    public void AFinishedStateAndTheBoundAreLeafValues()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file);
        var (obs, history) = (file.Tests[0].Obs, file.Tests[0].History);
        Assert.Equal(LeafValue.Of(CombatStatus.Won, obs), net.Value(CombatStatus.Won, obs, history));
        Assert.Equal(0, net.Value(CombatStatus.Lost, obs, history));
    }
}
