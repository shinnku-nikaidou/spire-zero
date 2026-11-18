using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

/// <summary>
/// What of NoSL's combat brain stays in C# since its search went to the engine (search/pimc.rs, whose tests carry the
/// search's from LoopSearchTests and PimcMemoTests): the plan a search leaves and how it is followed, the answers to a
/// prompt no search weighs, and the answers as actions.
/// </summary>
public class PimcRouteTests
{
    private static readonly CombatAction R = CombatAction.Play(1), H = CombatAction.Play(2, 1);

    [Fact]
    public void PimcKeepsOnlyAPlanThatRepeats()
    {
        // A search whose line repeats a cycle: its plan is followed at each observation it expects, not searched again.
        var obs = Fixtures.Slugs();
        ulong hash = ProtocolJson.Hash(obs);
        int searches = 0;
        CombatDecisionResult Repeating()
        {
            searches++;
            return new(R, [R, R, H], [], [hash, hash, hash], Repeats: true);
        }
        var repeating = new TurnPlan(all: false);
        foreach (var expected in new[] { R, R, R, H })
            Assert.Equal(expected, repeating.Decide(obs, [R, H, CombatAction.EndTurn], Repeating).First);
        Assert.Equal(1, searches);

        // A plan that does not repeat is not kept: the next decision is searched.
        var ordinary = new TurnPlan(all: false);
        CombatDecisionResult Plain()
        {
            searches++;
            return new(CombatAction.EndTurn, [CombatAction.EndTurn], [], [hash]);
        }
        ordinary.Decide(obs, [CombatAction.EndTurn], Plain);
        ordinary.Decide(obs, [CombatAction.EndTurn], Plain);
        Assert.Equal(3, searches);
    }

    private static ChoiceDecision Prompt(int min = 0, int max = 1, params string[] ids) => new(new CardChoice
    {
        Options = [.. (ids.Length > 0 ? ids : ["A", "B", "C"]).Select(id => new CardView { Id = id })],
        Min = min,
        Max = max,
        Context = new() { Purpose = "hand", Cancelable = false, InCombat = true },
    });

    [Fact]
    public void AnUnlimitedBudgetTakesEveryAnswerOfAPrompt()
    {
        // A prompt of three answers: with no limit on the nodes, all three are tried, nothing cut.
        Assert.Equal(3, PromptAnswers.Of(Prompt(1, 1).Choice, SlPlanner.Unlimited, out bool cut).Count);
        Assert.False(cut);
    }

    [Fact]
    public void AStateAtAPromptTakesOnlyItsAnswers()
    {
        var obs = Fixtures.Slugs() with { Prompt = Prompt(1, 1).Choice };

        Assert.True(obs.Takes(CombatAction.Pick([1]), []));
        Assert.False(obs.Takes(CombatAction.Pick([0, 1]), []));
        Assert.False(obs.Takes(CombatAction.EndTurn, [CombatAction.EndTurn]));
        // And where no prompt waits, no answer is taken.
        Assert.False(Fixtures.Slugs().Takes(CombatAction.Pick([]), [CombatAction.EndTurn]));
    }

    [Fact]
    public void TheAnswersAPlanStartsWithAreGivenWhenTheirPromptsComeEvenIfThePlanIsNotKept()
    {
        var obs = Fixtures.Slugs();
        var asked = obs with { Prompt = Prompt().Choice };
        int searches = 0;
        CombatDecisionResult Search()
        {
            searches++;
            // PIMC's worlds did not agree: no hashes, so the plan is not kept.
            return new(R, [CombatAction.Pick([2]), H, CombatAction.EndTurn], []);
        }
        var plan = new TurnPlan(all: false);

        Assert.Equal(R, plan.Decide(obs, [R, H], Search).First);
        Assert.Equal(CombatAction.Pick([2]), plan.Decide(asked, [], Search).First);
        Assert.Equal(1, searches);
        // The plays after the answer were not kept.
        Assert.Equal(R, plan.Decide(obs, [R, H], Search).First);
        Assert.Equal(2, searches);

        // An answer the prompt does not take is not given, and that is no plan gone wrong.
        var narrow = obs with { Prompt = Prompt(1, 1, "A", "B").Choice };
        var searched = plan.Decide(narrow, [], () => new(CombatAction.Pick([0]), [], []));
        Assert.Equal(CombatAction.Pick([0]), searched.First);
        Assert.False(searched.Replanned);
    }

    [Fact]
    public void APromptNoSearchWeighsTakesTheFirstOptionsItNeeds()
    {
        Assert.Empty(FirstPicks.Instance.Choose(Prompt()).Picks);
        Assert.Equal([0, 1], FirstPicks.Instance.Choose(Prompt(min: 2, max: 3)).Picks);
        Assert.Equal([0], FirstPicks.Instance.Choose(Prompt(min: 2, max: 2, "A")).Picks);
        // PIMC has no world at a prompt: one its plan did not answer gets the first options.
        Assert.Equal(CombatAction.Pick([0, 1]), PimcBrain.Unplanned(Fixtures.Slugs() with { Prompt = Prompt(min: 2, max: 3).Choice })!.First);
        Assert.Null(PimcBrain.Unplanned(Fixtures.Slugs()));
    }

    [Fact]
    public void APromptsAnswersPickCardsThePlayerCanTellApart()
    {
        static List<string> Answers(ChoiceDecision prompt) => [.. prompt.Choice.Answers().Select(a => string.Join(",", a))];

        Assert.Equal(["", "0", "1", "2"], Answers(Prompt()));
        // Two copies of A are one card twice: picking one is picking the first, the fewest picks first.
        Assert.Equal(["0", "2", "0,1", "0,2"], Answers(Prompt(1, 2, "A", "A", "B")));
        // The first answer is the one nothing has to weigh, and a prompt with one answer has nothing to decide.
        Assert.Equal(FirstPicks.Of(Prompt(1, 2, "A", "A", "B").Choice), CombatAction.Pick(Prompt(1, 2, "A", "A", "B").Choice.Answers().First()));
        Assert.Equal(CombatAction.Pick([0, 1]), PimcBrain.Forced(Fixtures.Slugs() with { Prompt = Prompt(2, 2, "A", "A").Choice }, []));
        Assert.Null(PimcBrain.Forced(Fixtures.Slugs() with { Prompt = Prompt().Choice }, []));
    }

    [Fact]
    public void AnAnswerToAPromptIsACombatAction()
    {
        foreach (var action in new[] { CombatAction.Pick([2]), CombatAction.Pick([3, 0]), CombatAction.Pick([]), CombatAction.Play(5, 1), CombatAction.Potion(1), CombatAction.EndTurn })
        {
            Assert.Equal(action, CombatAction.Parse(action.ToString()));
            Assert.Equal(action, ProtocolJson.Deserialize<CombatAction>(ProtocolJson.Serialize(action)));
        }
        // One answer has one form: its picks in order.
        Assert.Equal("pick:0,3", CombatAction.Pick([3, 0]).ToString());
        Assert.Equal("pick:", CombatAction.Pick([]).ToString());
        Assert.Equal([0, 3], CombatAction.Parse("pick:0,3").Picks);
        Assert.Empty(CombatAction.Parse("pick:").Picks);
        Assert.False(CombatAction.TryParse("pick:x", out _));
        Assert.False(CombatAction.TryParse("pick:1,", out _));
        Assert.False(CombatAction.TryParse("play:1@2", out _));
    }
}
