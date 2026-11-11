using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Harness;

public class DispatcherTests
{
    private sealed class Fixed(string pick) : IMacroPolicy, IChoicePolicy
    {
        public MacroAnswer Choose(MacroDecision decision) => new(MacroAction.Parse(pick));
        public ChoiceAnswer Choose(ChoiceDecision decision) => new([]);
    }

    private static Dispatcher Make(CombatRoute combat, string macroPick = "map:0,1")
    {
        var macro = new Fixed(macroPick);
        return new Dispatcher("run-1", PlayEnvironment.NoSL, combat, new("p0", macro), new("l0", macro), new("l0", macro));
    }

    private static CombatDecision Decision(CombatObservation obs) => new([CombatAction.Play(0, 1), CombatAction.EndTurn], () => obs);

    private static MacroDecision Map(string surface = Surfaces.Map) =>
        new(surface, Fixtures.Acts("map:0,1"), () => throw new InvalidOperationException("the observation is not needed without a log"));

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

        Assert.Equal("map:0,1", dispatcher.Choose(Map()).Action.ToString());
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

    [Fact]
    public void AnSLNodesCloneIsItsCheckpointAndTheDecisionsSince()
    {
        NodeClone? seen = null;
        var macro = new Fixed("map:0,1");
        var dispatcher = new Dispatcher("run-1", PlayEnvironment.SL, new("test", (_, _, clone, _) =>
        {
            seen = clone;
            return new(CombatAction.EndTurn, [], []);
        }), new("p0", macro), new("l0", macro), new("l0", macro));
        dispatcher.Checkpoint(new(0, "{}", [0, 1]));
        dispatcher.Act(new(0, 0, "event event:1"));
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        Assert.True(seen is { Save: "{}" } && seen.Trace.SequenceEqual(["event event:1"]));
        // The run's rule goes with the clone, so that its fights and probes play by it.
        Assert.Equal([0, 1], seen.AutoWin);
    }

    private static ChoiceDecision Prompt(bool inCombat = true) => new(new CardChoice
    {
        Options = [new CardView { Id = "A" }, new CardView { Id = "B" }, new CardView { Id = "C" }],
        Min = 0,
        Max = 1,
        Context = new() { Purpose = "hand", Cancelable = false, InCombat = inCombat },
    });

    [Fact]
    public void AnswersThePromptOfTheActionWithTheBrainsPickAndHidesItFromTheHost()
    {
        var dispatcher = Make(new("test", (_, _, _, _) => new(CombatAction.Play(0, 1) with { Pick = "2" }, [], [])));

        Assert.Equal(CombatAction.Play(0, 1), dispatcher.Choose(Decision(Fixtures.Slugs())));
        Assert.Equal([2], dispatcher.Choose(Prompt()).Picks);
        // Only the action's first prompt: the next goes to the prompt policy, as does one out of combat, or one after another action.
        Assert.Empty(dispatcher.Choose(Prompt()).Picks);
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        Assert.Empty(dispatcher.Choose(Prompt(inCombat: false)).Picks);
    }

    [Fact]
    public void APickThePromptDoesNotAcceptGoesToThePolicy()
    {
        var dispatcher = Make(new("test", (_, _, _, _) => new(CombatAction.Play(0, 1) with { Pick = "7" }, [], [])));

        dispatcher.Choose(Decision(Fixtures.Slugs()));
        Assert.Empty(dispatcher.Choose(Prompt()).Picks);
    }

    private sealed class EndTurns : ICombatPolicy
    {
        public CombatAction Choose(CombatDecision decision) => CombatAction.EndTurn;
    }
}
