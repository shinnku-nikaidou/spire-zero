using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class DispatcherTests
{
    private sealed class Fixed(string pick) : IMacroPolicy, IChoicePolicy
    {
        public string Choose(MacroDecision decision) => pick;
        public IReadOnlyList<int> Choose(ChoiceDecision decision) => [];
    }

    private static Dispatcher Make(CombatRoute combat, string macroPick = "map:0,1")
    {
        var macro = new Fixed(macroPick);
        return new Dispatcher("run-1", PlayEnvironment.NoSL, combat, new("p0", macro), new("l0", macro), new("l0", macro));
    }

    private static CombatDecision Decision(CombatObservation obs) => new([CombatAction.Play(0, 1), CombatAction.EndTurn], () => obs);

    private static MacroDecision Map(string surface = Surfaces.Map) =>
        new(surface, ["map:0,1"], () => throw new InvalidOperationException("the observation is not needed without a log"));

    [Fact]
    public void GivesTheCombatBrainTheMovesSoFar()
    {
        var seen = new List<IReadOnlyList<EnemyTurns>>();
        var dispatcher = Make(new("test", (_, _, clone, history) =>
        {
            Assert.Null(clone);
            seen.Add(history);
            return new(CombatAction.EndTurn, [], []);
        }));
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        dispatcher.Choose(Decision(Fixtures.Slugs() with { Round = 2 }));

        Assert.Equal([1, 2], seen.Select(h => h[0].Turns.Count));
        Assert.Equal(2, dispatcher.Brains["test"]);
    }

    [Fact]
    public void AMacroDecisionEndsTheCombatsRecord()
    {
        var dispatcher = Make(CombatRoute.Of("test", new EndTurns()));
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        Assert.NotEmpty(dispatcher.History);

        Assert.Equal("map:0,1", dispatcher.Choose(Map()));
        Assert.Empty(dispatcher.History);
    }

    [Fact]
    public void RefusesAnIllegalPick()
    {
        var combat = Make(new("test", (_, _, _, _) => new(CombatAction.Play(9), [], [])));
        Assert.Throws<InvalidOperationException>(() => combat.Choose(Decision(Fixtures.Slugs())));
        Assert.Throws<InvalidOperationException>(() => Make(CombatRoute.Of("test", new EndTurns()), "map:9,9").Choose(Map()));
    }

    [Fact]
    public void HasNoFallbackForAnUncoveredSurface() =>
        Assert.Throws<InvalidOperationException>(() => Make(CombatRoute.Of("test", new EndTurns())).Choose(Map("crystal_ball")));

    [Fact]
    public void RefusesNodeCheckpointsInNoSL() =>
        Assert.Throws<InvalidOperationException>(() => Make(CombatRoute.Of("test", new EndTurns())).Checkpoint(new(0, "{}")));

    private sealed class EndTurns : ICombatPolicy
    {
        public CombatAction Choose(CombatDecision decision) => CombatAction.EndTurn;
    }
}
