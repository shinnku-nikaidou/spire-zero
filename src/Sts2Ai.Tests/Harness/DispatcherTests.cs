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
        return new Dispatcher("run-1", PlayEnvironment.NoSL, combat, new("l0", macro), new("l0", macro));
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
        }), new("l0", macro), new("l0", macro));
        dispatcher.Checkpoint(new(0, "{}", [0, 1]));
        dispatcher.Steps(["event event:1", "rewards reward:0"]);
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        Assert.True(seen is { Save: "{}" } && seen.Trace.SequenceEqual(["event event:1", "rewards reward:0"]));
        // The run's rule goes with the clone, so that its fights and probes play by it.
        Assert.Equal([0, 1], seen.AutoWin);
        // Each decision brings the steps whole: a player who went back sends fewer.
        dispatcher.Steps(["event event:1"]);
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        Assert.True(seen.Trace.SequenceEqual(["event event:1"]));
        Assert.Throws<InvalidDataException>(() => dispatcher.Steps(null));
    }

    [Fact]
    public void RefusesNodeStepsInNoSL() =>
        Assert.Throws<InvalidOperationException>(() => Make(CombatRoute.Of("test", new EndTurns())).Steps(["event event:1"]));

    private static readonly CardChoice Asked = new()
    {
        Options = [new CardView { Id = "A" }, new CardView { Id = "B" }, new CardView { Id = "C" }],
        Min = 0,
        Max = 1,
        Context = new() { Purpose = "hand", Cancelable = false, InCombat = true },
    };

    /// <summary>A card prompt as a host shows it: over the combat when one is going on. <paramref name="autoWin"/> is the
    /// run's rule.</summary>
    private static ChoiceDecision Prompt(bool inCombat = true, IReadOnlyList<int>? autoWin = null, string room = "Monster")
    {
        var choice = Asked with { Context = Asked.Context with { InCombat = inCombat } };
        var run = Fixtures.Around(Fixtures.Slugs()) with { AutoWin = autoWin };
        return new(choice, () => new Observation { Run = run, Screen = new CardChoiceScreen { Choice = choice, Combat = inCombat ? Fixtures.Slugs() with { Room = room } : null } });
    }

    [Fact]
    public void ACardPromptDuringACombatIsTheCombatBrainsDecision()
    {
        var seen = new List<(CardChoice? Prompt, int Legal, int Rounds)>();
        var dispatcher = Make(new("test", (decision, _, _, history) =>
        {
            seen.Add((decision.Observation.Prompt, decision.Legal.Count, history[0].Turns.Count));
            return new(decision.Observation.Prompt != null ? CombatAction.Pick([2]) : CombatAction.Play(0, 1), [CombatAction.EndTurn], []);
        }));

        Assert.Equal(CombatAction.Play(0, 1), dispatcher.Choose(Decision(Fixtures.Slugs())));
        var (answer, note) = dispatcher.Decide(Prompt());
        Assert.Equal([2], answer.Picks);
        // The brain saw the combat with the prompt on it and no play to make, and the combat's record of moves.
        Assert.Equal([(null, 2, 1), (Asked, 0, 1)], seen);
        Assert.Equal(2, dispatcher.Brains["test"]);
        // The host shows the prompt's screen: the plays the plan goes on with are not for it.
        Assert.Null(note.Plan);
        // A prompt outside combat is the macro side's.
        Assert.Empty(dispatcher.Choose(Prompt(inCombat: false)).Picks);
        Assert.Equal(1, dispatcher.Brains["l0"]);
    }

    [Fact]
    public void ThePlansRootedAtACardPromptAreCountedByWhyTheyWereMade()
    {
        // A route that searches at every prompt (a search leaves candidates) and says what its plans cost.
        bool replanned = false;
        var costs = new PlanTally(Plans: 4, Nodes: 1200);
        var searched = new[] { new Candidate("pick:2", 0.5, 0, 1) };
        var dispatcher = Make(new("test", (decision, _, _, _) => decision.Observation.Prompt != null
            ? new(CombatAction.Pick([2]), [], searched, Replanned: replanned)
            : new(CombatAction.Play(0, 1), [], []), Costs: () => costs));

        // The combat's first decision is a prompt: it opens the combat.
        dispatcher.Decide(Prompt());
        // After a play, a prompt the plan had no answer for; then one where a step did not hold.
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        dispatcher.Decide(Prompt());
        replanned = true;
        dispatcher.Decide(Prompt());
        // A decision outside combat ends the combat: the next prompt opens another.
        dispatcher.Choose(Map());
        replanned = false;
        dispatcher.Decide(Prompt());

        Assert.Equal(costs with { Replans = 1, PromptOpens = 2, PromptAfterPlan = 1, PromptReplanned = 1 }, dispatcher.Search);
        // A brain that keeps no count has none.
        Assert.Null(Make(CombatRoute.Of("test", new EndTurns())).Search);
    }

    [Fact]
    public void ACardPromptInAFightTheRunsRuleWinsIsTheMacroSides()
    {
        int fights = 0;
        var dispatcher = Make(new("test", (_, _, _, _) =>
        {
            fights++;
            return new(CombatAction.Pick([2]), [], []);
        }));
        int act = Fixtures.Floor1().Act;

        // The fight is won at its start, before any play: there is no combat to search, and the combat brain is not asked.
        Assert.Empty(dispatcher.Choose(Prompt(autoWin: [act])).Picks);
        Assert.Equal((0, 1), (fights, dispatcher.Brains["l0"]));
        // The rule leaves elites and bosses to be fought, and so does an act it does not name.
        Assert.Equal([2], dispatcher.Choose(Prompt(autoWin: [act], room: "Elite")).Picks);
        Assert.Equal([2], dispatcher.Choose(Prompt(autoWin: [act + 1])).Picks);
        Assert.Equal((2, 1), (fights, dispatcher.Brains["l0"]));
    }

    [Fact]
    public void AnAnswerThePromptDoesNotAcceptIsRefused()
    {
        var plays = Make(new("test", (_, _, _, _) => new(CombatAction.EndTurn, [], [])));
        Assert.Throws<InvalidOperationException>(() => plays.Choose(Prompt()));
        var wrong = Make(new("test", (_, _, _, _) => new(CombatAction.Pick([7]), [], [])));
        Assert.Throws<InvalidOperationException>(() => wrong.Choose(Prompt()));
        // And an answer where a play was asked for.
        Assert.Throws<InvalidOperationException>(() => wrong.Choose(Decision(Fixtures.Slugs())));
    }

    [Fact]
    public void APlaceholderPolicyAnswersACombatsPromptsWithTheFirstOptions()
    {
        var dispatcher = Make(CombatRoute.Of("ends", new EndTurns()));

        Assert.Equal(CombatAction.EndTurn, dispatcher.Choose(Decision(Fixtures.Slugs())));
        Assert.Empty(dispatcher.Choose(Prompt()).Picks);
    }

    private sealed class EndTurns : ICombatPolicy
    {
        public CombatAction Choose(CombatDecision decision) => CombatAction.EndTurn;
    }
}
