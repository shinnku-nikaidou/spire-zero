using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class TurnPlanTests
{
    private static CombatDecisionResult Planned(params (CombatAction Action, ulong Hash)[] steps) =>
        new(CombatAction.EndTurn, [.. steps.Select(s => s.Action)], [], [.. steps.Select(s => s.Hash)]);

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
