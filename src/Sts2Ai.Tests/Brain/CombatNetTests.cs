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
    /// attack, orbs on and off the table past the places the net tells apart, and every card, power, relic and enemy state
    /// the observation gathers (keywords, Tinker, an enchantment spent, Momentum, calculated numbers, replays, star costs,
    /// a moved number, Swipe's card, a relic spent, an enemy lying dead, another character), every weight nonzero: a
    /// change to <c>learn/combat/encode.py</c> or to CombatNet that reads a state otherwise fails on loading.
    /// </summary>
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");

    /// <summary>A V for the tests, linear in what a combat's ending changes: half the HP share, and 0.1 for each potion held.</summary>
    private sealed class Linear : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => 0.5 * run.Hp / run.MaxHp + 0.1 * run.Potions.Count(p => p != null);
    }

    /// <summary>A V for the tests that is 1 only while every potion of <paramref name="held"/> is still held.</summary>
    private sealed class AllKept(int held) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => run.Potions.Count(p => p != null) == held ? 1 : 0;
    }

    /// <summary>The champion's own file: loading checks CombatNet gives its test states the trainer's endings.</summary>
    [Fact]
    public void ReadsC9_0sTestStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Repo("weights", "all", "c9-0.json")));
        using var net = CombatNet.Load(file, new Linear());
        Assert.Equal(16, file.Tests.Count);
    }

    [Fact]
    public void ReadsItsStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file, new Linear());
        Assert.Equal(file.Vocab["potions"].Count, file.Tests[0].PotionUsed.Count);
        Assert.True(file.Tests[0].PotionUsed.Distinct().Count() > 1);
        Assert.Contains(file.Tests, t => t.Obs.Potions.Count(p => p == "FIRE_POTION") == 2);
        Assert.Contains(file.Tests, t => t.Obs.Allies.Any(a => a.Hp > 0));
        Assert.Contains(file.Tests, t => t.Obs.Player.Orbs.Count > 10);
        Assert.Contains(file.Tests, t => t.Obs.Hand.Any(c => c.Vars?.ContainsKey("Damage") == true));
        Assert.Contains(file.Tests, t => t.Obs.Hand.Any(c => c.Keywords != null && c.Calc != null && c.Replay != null && c.Stars != null));
        Assert.Contains(file.Tests, t => t.Obs.Hand.Any(c => c.Spent == true && c.Enchant?.Extra != null));
        Assert.Contains(file.Tests, t => t.Obs.Draw.Any(c => c.Tinker != null));
        Assert.Contains(file.Tests, t => t.Obs.Player.Powers.Any(p => p.Detail != null) && t.Obs.Relics.Any(r => r.Spent == true));
        Assert.Contains(file.Tests, t => t.Obs.Enemies.Any(e => e.Powers.Any(p => p.Stolen != null)));
        Assert.Contains(file.Tests, t => t.Obs.Enemies.Any(e => e.Hp <= 0 && e.Intents?.Count > 0) && t.Obs.Player.Character == "IRONCLAD");
    }

    [Fact]
    public void AStateInProgressIsVOfTheRunsItsEndingsLeave()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file, new Linear());
        double width = (1 - file.LossMin) / file.Buckets;
        var potions = file.Vocab["potions"].ToList();
        foreach (var test in file.Tests)
        {
            int maxHp = test.Obs.Player.MaxHp;
            double share = test.Obs.Player.Hp / (double)maxHp, value = 0;
            for (int k = 1; k <= file.Buckets; k++)
                value += test.Outcome[k] * 0.5 * Math.Clamp((int)Math.Round((share - (file.LossMin + (k - 0.5) * width)) * maxHp), 1, maxHp) / maxHp;
            double kept = test.Obs.Potions.OfType<string>().Sum(p => 1 - test.PotionUsed[Math.Max(0, potions.IndexOf(p))]);
            double expected = value + (1 - test.Outcome[0]) * 0.1 * kept;
            Assert.InRange(((ICombatLeaf)net).Value(CombatStatus.InProgress, test.Obs, Fixtures.Around(test.Obs), test.History) - expected, -1e-3, 1e-3);
        }
    }

    [Fact]
    public void EachPotionIsKeptByItsOwnChance()
    {
        // Two Fire Potions: V is 1 only if both are kept, a chance of the first's times the second's.
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        var test = file.Tests.First(t => t.Obs.Potions.Count(p => p == "FIRE_POTION") == 2);
        int held = test.Obs.Potions.Count(p => p != null);
        using var net = CombatNet.Load(file, new AllKept(held));
        var potions = file.Vocab["potions"].ToList();
        double expected = 1 - test.Outcome[0];
        foreach (var p in test.Obs.Potions.OfType<string>()) expected *= 1 - test.PotionUsed[Math.Max(0, potions.IndexOf(p))];
        Assert.InRange(((ICombatLeaf)net).Value(CombatStatus.InProgress, test.Obs, Fixtures.Around(test.Obs), test.History) - expected, -1e-3, 1e-3);
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
    public void AWonCombatIsVOfItsRunAndALostOneNothing()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        var v = new Linear();
        using var net = CombatNet.Load(file, v);
        var (obs, history) = (file.Tests[0].Obs, file.Tests[0].History);
        var run = Fixtures.Around(obs) with { MaxHp = obs.Player.MaxHp + 10 };
        Assert.Equal(v.Value(run), ((ICombatLeaf)net).Value(CombatStatus.Won, obs, run, history), 9);
        Assert.Equal(0, ((ICombatLeaf)net).Value(CombatStatus.Lost, obs, run, history));
    }
}
