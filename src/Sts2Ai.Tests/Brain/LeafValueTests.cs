using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

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
