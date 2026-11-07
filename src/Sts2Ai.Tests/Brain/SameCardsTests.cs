using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class SameCardsTests
{
    private static CardView Card(string id, uint? uid = null, int up = 0) => new() { Id = id, Uid = uid, Cost = 1, Up = up };

    [Fact]
    public void CopiesOfACardAreOneActionAtEachTarget()
    {
        var obs = Fixtures.Slugs() with { Hand = [Card("STRIKE", 1), Card("DEFEND", 2), Card("STRIKE", 3), Card("STRIKE", 4, up: 1)] };
        CombatAction[] legal = [CombatAction.Play(1, 7), CombatAction.Play(1, 8), CombatAction.Play(2), CombatAction.Play(3, 7),
            CombatAction.Play(3, 8), CombatAction.Play(4, 7), CombatAction.EndTurn];

        // The upgraded Strike is another card; the second plain Strike is the first one again.
        Assert.Equal([CombatAction.EndTurn, CombatAction.Play(1, 7), CombatAction.Play(1, 8), CombatAction.Play(2), CombatAction.Play(4, 7)],
            PimcBrain.Ordered(legal, obs));
    }

    [Fact]
    public void TheSameCardsInAnotherOrderOrCopyAreOneState()
    {
        var start = Fixtures.Slugs();
        var a = start with { Hand = [Card("DEFEND", 2), Card("STRIKE", 3)], Discard = [Card("STRIKE"), Card("DEFEND")], Potions = ["BLOCK", null, "FIRE"] };
        var b = start with { Hand = [Card("STRIKE", 1), Card("DEFEND", 2)], Discard = [Card("DEFEND"), Card("STRIKE")], Potions = [null, "FIRE", "BLOCK"] };
        var other = b with { Discard = [Card("DEFEND"), Card("DEFEND")] };

        Assert.Equal(PimcBrain.StateKey(a), PimcBrain.StateKey(b));
        Assert.NotEqual(PimcBrain.StateKey(a), PimcBrain.StateKey(other));
        Assert.NotEqual(ProtocolJson.Hash(a), ProtocolJson.Hash(b));
    }
}
