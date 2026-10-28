using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class MoveRecordTests
{
    [Fact]
    public void RecordsEachLivingEnemyOncePerRound()
    {
        var record = new MoveRecord();
        var obs = Fixtures.Slugs();
        record.Witness(obs);
        record.Witness(obs);
        record.Witness(obs with { Round = 2 });

        Assert.Equal([1u, 2u], record.History.Select(e => e.Uid));
        Assert.All(record.History, e => Assert.Equal([1, 2], e.Turns.Select(t => t.Round)));
        Assert.Equal("Attack", record.History[0].Turns[0].Intents[0].Type);
        Assert.Equal("Debuff", record.History[1].Turns[0].Intents[0].Type);
    }

    [Fact]
    public void SkipsTheDead()
    {
        var record = new MoveRecord();
        record.Witness(Fixtures.Slugs());
        record.Witness(Fixtures.Slugs().WithEnemyHp(1, 0) with { Round = 2 });

        Assert.Equal([1], record.History.Single(e => e.Uid == 1).Turns.Select(t => t.Round));
        Assert.Equal([1, 2], record.History.Single(e => e.Uid == 2).Turns.Select(t => t.Round));
    }

    [Fact]
    public void StartsOverWhenTheRoundGoesBack()
    {
        var record = new MoveRecord();
        record.Witness(Fixtures.Slugs() with { Round = 3 });
        record.Witness(Fixtures.Slugs());

        Assert.All(record.History, e => Assert.Equal([1], e.Turns.Select(t => t.Round)));
    }

    [Fact]
    public void ClearForgetsTheRound()
    {
        var record = new MoveRecord();
        record.Witness(Fixtures.Slugs());
        record.Clear();
        Assert.Empty(record.History);

        record.Witness(Fixtures.Slugs());
        Assert.Equal(2, record.History.Count);
    }
}

public class LeafValueTests
{
    private static readonly LeafWeights W = LeafValue.Default;

    [Fact]
    public void ALossIsWorthNothing() => Assert.Equal(0, LeafValue.Of(CombatStatus.Lost, Fixtures.Slugs()));

    [Fact]
    public void AWinScoresByTheHpAndPotionsLeft()
    {
        var obs = Fixtures.Slugs();
        Assert.Equal(W.WinBase + W.PerHp, LeafValue.Of(CombatStatus.Won, obs), 9);

        var hurt = obs with { Player = obs.Player with { Hp = 35 }, Potions = ["FIRE_POTION", null, null] };
        Assert.Equal(W.WinBase + W.PerHp / 2 + W.PerPotion, LeafValue.Of(CombatStatus.Won, hurt), 9);
    }

    [Fact]
    public void AFightInProgressIsWorthLessThanItsWinAtTheSameHp()
    {
        var obs = Fixtures.Slugs();
        double value = LeafValue.Of(CombatStatus.InProgress, obs);
        Assert.InRange(value, 0, LeafValue.Of(CombatStatus.Won, obs));
    }

    [Fact]
    public void AProjectedDeathStaysAboveALossAndKeepsItsOrder()
    {
        var obs = Fixtures.Slugs();
        var dying = obs with { Player = obs.Player with { Hp = 5 } };
        var dead = obs with { Player = obs.Player with { Hp = 1 } };
        Assert.True(LeafValue.Of(CombatStatus.InProgress, dying) > LeafValue.Of(CombatStatus.InProgress, dead));
        Assert.True(LeafValue.Of(CombatStatus.InProgress, dead) > 0);
    }

    [Fact]
    public void DamageOnAnEnemyRaisesTheValue()
    {
        var obs = Fixtures.Slugs();
        Assert.True(LeafValue.Of(CombatStatus.InProgress, obs.WithEnemyHp(1, 5)) > LeafValue.Of(CombatStatus.InProgress, obs));
        Assert.Equal(LeafValue.Of(CombatStatus.InProgress, obs), new HandLeaf().Value(CombatStatus.InProgress, obs, []));
    }
}

public class BudgetTests
{
    [Theory]
    [InlineData("Monster", 300)]
    [InlineData(null, 300)]
    [InlineData("Elite", 600)]
    [InlineData("Boss", 450)]
    public void TheRoomsMultipleScalesTheNodes(string? room, int nodes)
    {
        var budget = new Budget(Elite: 2, Boss: 1.5).For(room);
        Assert.Equal(new Budget(NodesPerWorld: nodes), budget);
    }

    [Fact]
    public void NamesShowOnlyWhatIsNotTheDefault()
    {
        Assert.Equal("8x300", new Budget().ToString());
        Assert.Equal("4x100 elite x2 halving", new Budget(4, 100, Elite: 2, Halving: true).ToString());
        Assert.Equal("beam 16x300 boss x2", new Budget(Boss: 2, Beam: 16).ToString());
    }
}

public class PimcMemoTests
{
    /// <summary>
    /// A turn of two cards, A and B, where A rolls once if played first and three times if second, and the enemies'
    /// turn then hits for 10 per roll. Played in either order, the two cards show the same state, but not the same
    /// future.
    /// </summary>
    private sealed class Rolls : ICombatWorld
    {
        private static readonly CombatObservation Start = Fixtures.Slugs() with
        {
            Round = 1,
            Hand = [new CardView { Id = "A", Uid = 1 }, new CardView { Id = "B", Uid = 2 }],
        };

        private readonly List<string> _steps = [];

        private int Count => _steps.IndexOf("A") switch { 0 => 1, 1 => 3, _ => 0 };
        private bool Ended => _steps.Contains("end");

        public CombatObservation Observe() => Start with
        {
            Round = Ended ? 2 : 1,
            Player = Start.Player with { Hp = Ended ? 100 - 10 * Count : 100, MaxHp = 100 },
            Hand = Start.Hand.Where(c => !_steps.Contains(c.Id)).ToList(),
        };

        public IReadOnlyList<CombatAction> Legal() => Ended ? [] :
            [CombatAction.EndTurn, .. Observe().Hand.Select(c => CombatAction.Play(c.Uid!.Value))];

        public CombatStatus Status => CombatStatus.InProgress;
        public ulong HiddenKey() => (ulong)Count;

        public void Execute(CombatAction action) =>
            _steps.Add(action.Kind == CombatActionKind.EndTurn ? "end" : Start.Hand.First(c => c.Uid == action.Card).Id);

        public WorldSnapshot Snapshot() => new(_steps.ToList());

        public void Restore(WorldSnapshot snapshot)
        {
            _steps.Clear();
            _steps.AddRange(snapshot.Steps);
        }
    }

    /// <summary>The HP after the enemies' turn, and 50 for each card played.</summary>
    private sealed class Leaf : ICombatLeaf
    {
        public double Value(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history) => obs.Player.Hp + 50 * (2 - obs.Hand.Count);
    }

    private static readonly CombatAction A = CombatAction.Play(1), B = CombatAction.Play(2);

    [Fact]
    public void LinesThatLookTheSameButRolledDifferentlyAreNotMerged()
    {
        var pimc = new PimcBrain(new Leaf());
        // A then B: one roll, 190. B then A: three rolls, 170. Searched in either order.
        Assert.Equal([190.0, 170.0], pimc.SearchWorld(new Rolls(), 1, [A, B], 1000).Select(l => l.Value));
        Assert.Equal([170.0, 190.0], pimc.SearchWorld(new Rolls(), 1, [B, A], 1000).Select(l => l.Value));
    }
}

public class SlPlannerTests
{
    /// <summary>
    /// A small exact combat: 2 energy and 3 cards a turn from a fixed draw order, Strikes (1 energy, 6 damage to the one
    /// enemy) and Defends (1 energy, 5 block this turn), against an enemy with 24 HP whose attacks are known for every
    /// round, 4 then 13 then 4 again. HP never comes back, so the planner's bound holds and a full search is exact.
    /// </summary>
    private sealed class Toy : ICombatWorld
    {
        private static readonly string[] Deck = ["S", "D", "S", "S", "D", "D", "S", "S", "D", "S", "D", "S", "S", "D", "S"];
        private static readonly int[] Attacks = [4, 13, 4];
        private static readonly CombatObservation Start = Fixtures.Slugs();

        private readonly List<string> _steps = [];
        private int _hp, _enemy, _block, _energy, _round, _drawn;
        private List<(uint Uid, string Id)> _hand = [];

        private readonly int _enemyHp;

        public Toy(int enemyHp = 24)
        {
            _enemyHp = enemyHp;
            Reset();
        }

        private void Reset()
        {
            (_hp, _enemy, _block, _energy, _round, _drawn) = (20, _enemyHp, 0, 2, 1, 0);
            _hand = [];
            Draw();
        }

        private void Draw()
        {
            for (int i = 0; i < 3; i++, _drawn++) _hand.Add(((uint)_drawn + 10, Deck[_drawn % Deck.Length]));
        }

        private int Attack => Attacks[(_round - 1) % Attacks.Length];

        public CombatObservation Observe() => Start with
        {
            Round = _round,
            Player = Start.Player with { Hp = Math.Max(0, _hp), MaxHp = 20, Block = _block, Energy = _energy },
            Hand = _hand.Select(c => new CardView { Id = c.Id, Uid = c.Uid }).ToList(),
            Draw = [],
            Discard = [],
            Potions = [null, null, null],
            Enemies = [Start.Enemies[0] with { Uid = 1, Hp = Math.Max(0, _enemy), MaxHp = _enemyHp, Block = 0, Powers = [],
                Intents = [new IntentView { Type = "Attack", Damage = Attack, Hits = 1 }] }],
        };

        public IReadOnlyList<CombatAction> Legal() => Status != CombatStatus.InProgress ? [] :
            [CombatAction.EndTurn, .. _energy > 0 ? _hand.Select(c => CombatAction.Play(c.Uid, c.Id == "S" ? 1u : null)) : []];

        public CombatStatus Status => _hp <= 0 ? CombatStatus.Lost : _enemy <= 0 ? CombatStatus.Won : CombatStatus.InProgress;

        public ulong HiddenKey() => (ulong)_drawn;

        public void Execute(CombatAction action)
        {
            if (!Legal().Contains(action)) throw new InvalidOperationException($"{action} is not legal");
            _steps.Add(action.ToString());
            if (action.Kind == CombatActionKind.EndTurn)
            {
                _hp -= Math.Max(0, Attack - _block);
                (_block, _energy, _round) = (0, 2, _round + 1);
                _hand = [];
                Draw();
                return;
            }
            var card = _hand.First(c => c.Uid == action.Card);
            _hand.Remove(card);
            _energy--;
            if (card.Id == "S") _enemy -= 6;
            else _block += 5;
        }

        public WorldSnapshot Snapshot() => new(_steps.ToList());

        public void Restore(WorldSnapshot snapshot)
        {
            _steps.Clear();
            Reset();
            foreach (var step in snapshot.Steps) Execute(CombatAction.Parse(step));
        }
    }

    private static readonly HandLeaf Leaf = new();

    /// <summary>The best end of the combat by trying every line; each state once, as the toy shows all of it but the draw.</summary>
    private static double BruteForce(Toy world, Dictionary<(ulong, ulong), double> known)
    {
        if (world.Status != CombatStatus.InProgress) return Leaf.Value(world.Status, world.Observe(), []);
        var key = (ProtocolJson.Hash(world.Observe()), world.HiddenKey());
        if (known.TryGetValue(key, out var value)) return value;
        var here = world.Snapshot();
        double best = double.NegativeInfinity;
        foreach (var action in world.Legal())
        {
            world.Restore(here);
            world.Execute(action);
            best = Math.Max(best, BruteForce(world, known));
        }
        world.Restore(here);
        return known[key] = best;
    }

    /// <summary>Plays the plan from the start: each step at the observation it expects, to the end it claims.</summary>
    private static void AssertReplays(SlPlan plan, int enemyHp = 24)
    {
        var world = new Toy(enemyHp);
        var steps = plan.Result.Plan.Prepend(plan.Result.First).ToList();
        var hashes = plan.Result.PlanHashes!;
        Assert.Equal(steps.Count - 1, hashes.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            if (i > 0) Assert.Equal(hashes[i - 1], ProtocolJson.Hash(world.Observe()));
            world.Execute(steps[i]);
        }
        // A losing plan is played for its first turn only, unless proved; InProgress checks the steps alone.
        if (plan.End != CombatStatus.Won && !(plan.End == CombatStatus.Lost && plan.Proved)) return;
        Assert.Equal(plan.End, world.Status);
        Assert.Equal(plan.Value, Leaf.Value(world.Status, world.Observe(), []), 9);
    }

    [Fact]
    public void AFullSearchFindsTheBestLine()
    {
        var world = new Toy();
        double best = BruteForce(world, []);
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 1000));

        Assert.True(plan.Proved);
        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(best, plan.Value, 9);
        AssertReplays(plan);
    }

    [Fact]
    public void ANarrowBeamProvesNothingAndStillPlaysItsLine()
    {
        var world = new Toy();
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 4, Beam: 1));

        Assert.False(plan.Proved);
        AssertReplays(plan);
    }

    [Fact]
    public void ALosingPlanIsPlayedForOneTurn()
    {
        var world = new Toy(enemyHp: 500);
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 2));

        Assert.Equal(CombatStatus.Lost, plan.End);
        Assert.False(plan.Proved);
        var steps = plan.Result.Plan.Prepend(plan.Result.First).ToList();
        Assert.Equal(CombatActionKind.EndTurn, steps[^1].Kind);
        Assert.Single(steps, a => a.Kind == CombatActionKind.EndTurn);
        Assert.Equal(steps.Count - 1, plan.Result.PlanHashes!.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void ALosingPlanIsPlayedForItsReplanTurns(int replan)
    {
        var world = new Toy(enemyHp: 500);
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 2, Replan: replan));

        Assert.Equal(CombatStatus.Lost, plan.End);
        Assert.False(plan.Proved);
        var steps = plan.Result.Plan.Prepend(plan.Result.First).ToList();
        int ends = steps.Count(a => a.Kind == CombatActionKind.EndTurn);
        if (replan > 0) Assert.Equal(replan, ends);
        else Assert.True(ends > 2);
        // Played to its end, the plan dies where it claims.
        if (replan == 0)
        {
            var replay = new Toy(enemyHp: 500);
            foreach (var step in steps) replay.Execute(step);
            Assert.Equal(CombatStatus.Lost, replay.Status);
        }
    }

    [Fact]
    public void EachEffortIsAPlannerBudget()
    {
        Assert.Equal(new Budget(NodesPerWorld: 300, Beam: 8, Replan: 1), Budget.Effort("high"));
        Assert.All(Budget.Efforts, e => Assert.True(Budget.Effort(e).Beam > 0));
        Assert.Equal("beam 8x300 replan 0", Budget.Effort("medium").ToString());
        Assert.Throws<ArgumentException>(() => Budget.Effort("ultra"));
    }

    [Fact]
    public void AProvedLossIsPlayedToTheEnd()
    {
        var world = new Toy(enemyHp: 500);
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 1000));

        Assert.True(plan.Proved);
        Assert.Equal(CombatStatus.Lost, plan.End);
        Assert.True(plan.Result.Plan.Count(a => a.Kind == CombatActionKind.EndTurn) >= 1);
        AssertReplays(plan, enemyHp: 500);
    }

    [Fact]
    public void ItsCandidatesStartWithItsPick()
    {
        var world = new Toy();
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 1000));
        Assert.Equal(plan.Result.First.ToString(), plan.Result.Candidates[0].Action);
        Assert.Equal(plan.Value, plan.Result.Candidates[0].Value, 9);
    }

    [Fact]
    public void ARecordGoesOnFromItsHistory()
    {
        var record = new MoveRecord();
        record.Witness(Fixtures.Slugs());
        var copy = new MoveRecord(record.History);
        copy.Witness(Fixtures.Slugs());
        copy.Witness(Fixtures.Slugs() with { Round = 2 });

        Assert.All(copy.History, e => Assert.Equal([1, 2], e.Turns.Select(t => t.Round)));
        Assert.All(record.History, e => Assert.Equal([1], e.Turns.Select(t => t.Round)));
    }
}

public class TurnPlanTests
{
    private static CombatDecisionResult Planned(params (CombatAction Action, ulong Hash)[] steps) =>
        new(CombatAction.EndTurn, steps.Select(s => s.Action).ToList(), [], steps.Select(s => s.Hash).ToList());

    [Fact]
    public void AStepThatDoesNotHoldIsReplannedAndMarked()
    {
        var obs = Fixtures.Slugs();
        var play = CombatAction.Play(1);
        var plan = new TurnPlan();
        int searches = 0;
        plan.Decide(obs, [play], () => { searches++; return Planned((play, ProtocolJson.Hash(obs)), (play, 7)); });

        var followed = plan.Decide(obs, [play], () => throw new InvalidOperationException("searched"));
        Assert.False(followed.Replanned);
        var replanned = plan.Decide(obs, [play], () => { searches++; return Planned(); });
        Assert.True(replanned.Replanned);
        Assert.False(plan.Decide(obs, [play], () => { searches++; return Planned(); }).Replanned);
        Assert.Equal(3, searches);
    }
}
