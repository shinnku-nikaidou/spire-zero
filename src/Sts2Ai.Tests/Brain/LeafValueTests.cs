using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class LeafValueTests
{
    [Fact]
    public void ALossIsWorthNothing() => Assert.Equal(0, LeafValue.Of(CombatStatus.Lost, Fixtures.Slugs()));

    [Fact]
    public void AWinScoresByTheHpAndPotionsLeft()
    {
        var obs = Fixtures.Slugs();
        Assert.Equal(LeafValue.WinBase + LeafValue.PerHp, LeafValue.Of(CombatStatus.Won, obs), 9);

        var hurt = obs with { Player = obs.Player with { Hp = 35 }, Potions = ["FIRE_POTION", null, null] };
        Assert.Equal(LeafValue.WinBase + LeafValue.PerHp / 2 + LeafValue.PerPotion, LeafValue.Of(CombatStatus.Won, hurt), 9);
    }

    [Fact]
    public void AFightStillGoingIsTheCombatNetsToValue() =>
        Assert.Throws<ArgumentException>(() => LeafValue.Of(CombatStatus.InProgress, Fixtures.Slugs()));
}
