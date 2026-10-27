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
