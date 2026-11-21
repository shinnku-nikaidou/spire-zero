using Sts2Ai.Brain;
using Sts2Ai.Brain.Combat;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class CombatNetTests
{
    /// <summary>
    /// An untrained net with small widths, made by <c>learn/combat/export.py</c>'s net_file: real SL round starts, with four
    /// rounds of an enemy's history, an ally, two potions of one id, a potion off the table, a state at 5 % HP and a multi-hit
    /// attack, orbs on and off the table past the places the net tells apart, and every card, power, relic and enemy state
    /// the observation gathers (keywords, Tinker, an enchantment spent, Momentum, calculated numbers, replays, star costs,
    /// a moved number, Swipe's card, a relic spent, an enemy lying dead, another character, arrows held, shown and stuck in
    /// an enemy, a card that has grown), every weight nonzero: a
    /// change to how CombatNet feeds a state's tokens to the graph fails on loading.
    /// </summary>
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");

    /// <summary>A V for the tests, linear in what a combat's ending changes: half the HP share, and 0.1 for each potion held.</summary>
    private sealed class Linear : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => 0.5 * run.Hp / run.MaxHp + 0.1 * run.Potions.Count(p => p != null);
    }

    /// <summary>A V for the tests that rates each potion held below an empty slot: half the HP share, less 0.1 for each.</summary>
    private sealed class Disliked : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => 0.5 * run.Hp / run.MaxHp - 0.1 * run.Potions.Count(p => p != null);
    }

    /// <summary>A V for the tests that is 1 only while every potion of <paramref name="held"/> is still held.</summary>
    private sealed class AllKept(int held) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => run.Potions.Count(p => p != null) == held ? 1 : 0;
    }

    /// <summary>
    /// The engine's searches price a fight's end by its score (plans/2026-11-21-1655-fight-score-f.md): with f, the HP
    /// plus max HP plus each potion's f, the same f at any HP; without f, V.
    /// </summary>
    [Fact]
    public void AFightsEndIsItsHpAndMaxHpAndItsPotionsFWithF()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var f = SavesNet.Read(SavesNetTests.Fixture);
        using var net = CombatNet.Load(file, new Linear(), f);
        var run = Fixtures.Floor1() with { Hp = 20, MaxHp = 70, Potions = ["FIRE_POTION", null, "BLOCK_POTION"] };
        double kept = f.Saves(run, PlayEnvironment.SL).Sum();
        Assert.True(kept > 0);
        Assert.Equal(20 + 70 + kept, net.FightValue(run), 9);
        Assert.Equal(55 + 71 + kept, net.FightValue(run with { Hp = 55, MaxHp = 71 }), 9);
        var one = run with { Potions = ["FIRE_POTION", null, null] };
        Assert.Equal(20 + 70 + f.Saves(one, PlayEnvironment.SL).Sum(), net.FightValue(one), 9);
        Assert.Equal(f.Saves(run, PlayEnvironment.NoSL).Sum(), ((CombatNet)net.In(PlayEnvironment.NoSL)).FightValue(run) - 90, 9);
        using var plain = CombatNet.Load(file, new Linear());
        Assert.Equal(new Linear().Value(run), plain.FightValue(run));
    }

    /// <summary>The champion's own file: loading checks CombatNet gives its test states the trainer's endings.</summary>
    [Fact]
    public void ReadsTheChampionsTestStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixtures.ChampionNet("combat-net")));
        using var net = CombatNet.Load(file, new Linear());
        Assert.Equal(16, file.Tests.Count);
    }

    [Fact]
    public void ReadsItsStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file, new Linear());
        Assert.Equal(file.PotionGroups * file.Vocab["potions"].Count, file.Tests[0].PotionUsed.Count);
        Assert.True(file.Tests[0].PotionUsed.Distinct().Count() > 1);
        // Each group of buckets has potion chances of its own.
        Assert.NotEqual(file.Tests[0].PotionUsed.Take(file.Vocab["potions"].Count), file.Tests[0].PotionUsed.Skip(file.Vocab["potions"].Count).Take(file.Vocab["potions"].Count));
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

    /// <summary>States of every width through several batches, finished combats among them: each is worth what it is
    /// worth alone, to a float's precision, and the same states in the same order give the same numbers to the last bit.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStateIsWorthInABatchWhatItIsWorthAlone(bool champion)
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(champion ? Fixtures.ChampionNet("combat-net") : Fixture));
        using var net = CombatNet.Load(file, new Linear());
        var states = new List<LeafState>();
        for (int i = 0; i < 3 * CombatNet.Batch; i++)
        {
            var test = file.Tests[(i * 5 + i / 7) % file.Tests.Count];
            states.Add(new(CombatStatus.InProgress, test.Obs, Fixtures.Around(test.Obs), test.History));
            if (i % 5 == 0) states.Add(new(CombatStatus.Won, test.Obs, Fixtures.Around(test.Obs), test.History));
            if (i % 7 == 0) states.Add(new(CombatStatus.Lost, test.Obs, Fixtures.Around(test.Obs), test.History));
        }

        var together = net.Outcomes(states);

        Assert.Equal(states.Count, together.Count);
        for (int i = 0; i < states.Count; i++)
        {
            var alone = net.Outcome(states[i].Status, states[i].Obs, states[i].Run, states[i].History);
            Assert.InRange(together[i].Value - alone.Value, -1e-5, 1e-5);
            Assert.InRange(together[i].Win - alone.Win, -1e-5, 1e-5);
            Assert.InRange(together[i].Hp - alone.Hp, -1e-3, 1e-3);
        }
        Assert.Equal(together, net.Outcomes(states));
    }

    [Fact]
    public void AStateInProgressIsVOfTheRunsItsEndingsLeave()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file, new Linear());
        double width = (1 - file.LossMin) / file.Buckets;
        var potions = file.Vocab["potions"].ToList();
        int perGroup = file.Buckets / file.PotionGroups;
        foreach (var test in file.Tests)
        {
            int maxHp = test.Obs.Player.MaxHp;
            double share = test.Obs.Player.Hp / (double)maxHp, expected = 0;
            for (int k = 1; k <= file.Buckets; k++)
            {
                int g = (k - 1) / perGroup;
                double kept = test.Obs.Potions.OfType<string>().Sum(p => 1 - test.PotionUsed[g * potions.Count + Math.Max(0, potions.IndexOf(p))]);
                double hp = Math.Clamp((int)Math.Round((share - (file.LossMin + (k - 0.5) * width)) * maxHp), 1, maxHp) / (double)maxHp;
                expected += test.Outcome[k] * (0.5 * hp + 0.1 * kept);
            }
            Assert.InRange(((ICombatLeaf)net).Value(CombatStatus.InProgress, test.Obs, Fixtures.Around(test.Obs), test.History) - expected, -1e-3, 1e-3);
        }
    }

    [Fact]
    public void EachPotionIsKeptByItsOwnChance()
    {
        // Two Fire Potions: V is 1 only if both are kept, at each bucket its chance times the first's and the second's in its group.
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        var test = file.Tests.First(t => t.Obs.Potions.Count(p => p == "FIRE_POTION") == 2);
        int held = test.Obs.Potions.Count(p => p != null);
        using var net = CombatNet.Load(file, new AllKept(held));
        var potions = file.Vocab["potions"].ToList();
        int perGroup = file.Buckets / file.PotionGroups;
        double expected = 0;
        for (int k = 1; k <= file.Buckets; k++)
        {
            double all = test.Outcome[k];
            foreach (var p in test.Obs.Potions.OfType<string>()) all *= 1 - test.PotionUsed[(k - 1) / perGroup * potions.Count + Math.Max(0, potions.IndexOf(p))];
            expected += all;
        }
        Assert.InRange(((ICombatLeaf)net).Value(CombatStatus.InProgress, test.Obs, Fixtures.Around(test.Obs), test.History) - expected, -1e-3, 1e-3);
    }

    [Fact]
    public void APotionVRatesBelowAnEmptySlotIsPricedAsTheEmptySlot()
    {
        // The game lets a potion be thrown away for nothing, so whatever potions the endings keep, each is worth V of the
        // run with none: the HP alone.
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file, new Disliked());
        double width = (1 - file.LossMin) / file.Buckets;
        foreach (var test in file.Tests.Where(t => t.Obs.Potions.Any(p => p != null)))
        {
            int maxHp = test.Obs.Player.MaxHp;
            double share = test.Obs.Player.Hp / (double)maxHp, expected = 0;
            for (int k = 1; k <= file.Buckets; k++)
                expected += test.Outcome[k] * 0.5 * Math.Clamp((int)Math.Round((share - (file.LossMin + (k - 0.5) * width)) * maxHp), 1, maxHp) / maxHp;
            Assert.InRange(((ICombatLeaf)net).Value(CombatStatus.InProgress, test.Obs, Fixtures.Around(test.Obs), test.History) - expected, -1e-3, 1e-3);
            var run = Fixtures.Around(test.Obs);
            Assert.Equal(new Disliked().Value(run with { Potions = [.. run.Potions.Select(_ => (string?)null)] }),
                ((ICombatLeaf)net).Value(CombatStatus.Won, test.Obs, run, test.History), 9);
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
