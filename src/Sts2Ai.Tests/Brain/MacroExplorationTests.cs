using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class MacroExplorationTests
{
    /// <summary>A V that is the sum of what each card and relic is worth.</summary>
    private sealed class Worth(Dictionary<string, double> worth) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => run.Deck.Sum(c => worth.GetValueOrDefault(c.Id)) + run.Relics.Sum(r => worth.GetValueOrDefault(r.Id));
    }

    private static readonly Worth V = new(new() { ["A"] = 1, ["B"] = 3, ["C"] = 2.9, ["BAD"] = -2, ["WORSE"] = -3 });

    private static MacroDecision Treasure() => new(Surfaces.Treasure, ["relic:0", "relic:1", "relic:2"],
        () => new Observation { Run = Fixtures.Floor1(), Screen = new TreasureScreen { Relics = ["A", "B", "C"] } });

    private static ChoiceDecision Prompt(int min, params string[] cards) => new(new CardChoice
    {
        Options = cards.Select(id => new CardView { Id = id }).ToList(),
        Min = min,
        Max = 1,
        Context = new ChoiceContext { Purpose = "choose_a_card_screen", Cancelable = min == 0, InCombat = false },
    }, () => new Observation { Run = Fixtures.Floor1(), Screen = new TreasureScreen { Relics = [] } });

    [Fact]
    public void ARemovalIsWorthOnlyWhatTheGameLetsBeRemoved()
    {
        // Removing the Eternal BAD would be worth the most, but the game offers only A; without A the deck is worse.
        var deck = new List<CardView> { new() { Id = "A" }, new() { Id = "BAD", Keywords = ["Eternal", "Unplayable"] } };
        var shop = new MacroDecision(Surfaces.Shop, ["remove", "leave"], () => new Observation
        {
            Run = Fixtures.Floor1() with { Deck = deck },
            Screen = new ShopScreen { Entries = [], RemovalCost = 75 },
        });
        Assert.Equal("leave", new MacroBrain(V).Choose(shop));
    }

    [Fact]
    public void WithoutExplorationItTakesTheBest()
    {
        var l0 = new MacroBrain(V);
        Assert.Equal("relic:1", l0.Choose(Treasure()));
        Assert.Equal([1], l0.Choose(Prompt(0, "A", "B")));
        Assert.Empty(l0.Choose(Prompt(0, "BAD", "WORSE")));
        Assert.Equal([0], l0.Choose(Prompt(1, "BAD", "WORSE")));
        Assert.False(l0.Explored);
    }

    [Fact]
    public void ExploringItDrawsByVAndTheLogMarksWhatIsNotTheBest()
    {
        var l0 = new MacroBrain(V, new Softmax(1, new Random(1)));
        var dispatcher = new Dispatcher("run-1", PlayEnvironment.NoSL, CombatRoute.Of("test", new PlayPolicy(new Random(1))),
            new("p0", PromptBrain.Instance), new("l0", l0), new("l0", l0), log: true);
        var picks = Enumerable.Range(0, 1000).Select(_ => dispatcher.Choose(Treasure())).ToList();

        // By their logs, weights 1 : 3 : 2.9 for A, B, C.
        Assert.InRange(picks.Count(p => p == "relic:0"), 110, 180);
        Assert.InRange(picks.Count(p => p == "relic:1"), 390, 480);
        Assert.All(dispatcher.Log, e => Assert.Equal(e.Chosen != "relic:1" ? true : null, e.Explored));
    }

    [Fact]
    public void ExploringReadsVsRatiosAtAnySizeAndNeverDrawsWhatIsWorthNothing()
    {
        var l0 = new MacroBrain(new Worth(new() { ["A"] = 1e-7, ["B"] = 3e-7, ["C"] = 0 }), new Softmax(1, new Random(1)));
        var dispatcher = new Dispatcher("run-1", PlayEnvironment.NoSL, CombatRoute.Of("test", new PlayPolicy(new Random(1))),
            new("p0", PromptBrain.Instance), new("l0", l0), new("l0", l0), log: true);
        var picks = Enumerable.Range(0, 1000).Select(_ => dispatcher.Choose(Treasure())).ToList();

        Assert.InRange(picks.Count(p => p == "relic:0"), 200, 300);
        Assert.Equal(0, picks.Count(p => p == "relic:2"));
    }

    private static List<CardView> Cards(string[] ids) => ids.Select(id => new CardView { Id = id }).ToList();

    private static MacroDecision Rewards(params string[] cards) => new(Surfaces.Rewards, ["reward:0", "rewards_done"], () => new Observation
    {
        Run = Fixtures.Floor1(),
        Screen = new RewardsScreen { Rewards = [new RewardView { Index = 0, Kind = "card", Cards = Cards(cards) }] },
    });

    private static MacroDecision CardReward(params string[] cards) => new(Surfaces.CardReward,
        [.. cards.Select((_, i) => $"card:{i}"), "card_alt:Skip"],
        () => new Observation { Run = Fixtures.Floor1(), Screen = new CardRewardScreen { Cards = Cards(cards), Alternatives = ["Skip"] } });

    [Fact]
    public void DrawingEvenlyCardRewardsTakeAnOutcomeDrawnAmongTheirCardsAndSkippingAndTheCardScreenKeepsTheDraw()
    {
        var picks = new List<(string Pick, bool Explored)>();
        for (int seed = 0; seed < 4000; seed++)
        {
            var l0 = new MacroBrain(V, uniform: new Uniform(1, new Random(seed)));
            if (l0.Choose(Rewards("WORSE", "BAD", "A")) == "rewards_done")
                picks.Add(("skip", l0.Explored));
            else
                picks.Add((l0.Choose(CardReward("WORSE", "BAD", "A")), l0.Explored));
        }
        // Every outcome has 1/4 whatever V says (it would open the reward and take A); skipping is never opening it.
        foreach (var outcome in new[] { "card:0", "card:1", "card:2", "skip" })
            Assert.InRange(picks.Count(p => p.Pick == outcome), 900, 1100);
        Assert.All(picks, p => Assert.True(p.Explored));
        Assert.DoesNotContain(picks, p => p.Pick == "card_alt:Skip");
    }

    /// <summary>One decision of each kind outside card rewards, each offering the potion belt too; V's first is option 0 or 1.</summary>
    private static IEnumerable<MacroDecision> EveryKind()
    {
        static Observation On(ScreenView screen) => new() { Run = Fixtures.Floor1(), Screen = screen };
        const string belt = "discard_potion:0";
        yield return new(Surfaces.Treasure, ["relic:0", "relic:1", "relic:2", belt], () => On(new TreasureScreen { Relics = ["A", "B", "C"] }));
        yield return new(Surfaces.Shop, ["buy:0", "buy:1", "leave", belt], () => On(new ShopScreen
        {
            Entries = [new() { Slot = 0, Kind = "card", Card = new() { Id = "A" }, Cost = 1 }, new() { Slot = 1, Kind = "card", Card = new() { Id = "B" }, Cost = 1 }],
        }));
        yield return new(Surfaces.Rest, ["rest:HEAL", "rest:SMITH", belt], () => On(new RestScreen { Options = [new("HEAL", true, 10), new("SMITH", true)] }));
        yield return new(Surfaces.Event, ["event:0", "event:1", "event:2", belt], () => On(new EventScreen
        {
            Id = "E", Finished = false, Options = [new() { Index = 0, Key = "a" }, new() { Index = 1, Key = "b" }, new() { Index = 2, Key = "c" }],
        }));
        yield return new(Surfaces.Map, ["map:0,1", "map:1,1", belt], () => On(new MapScreen { Travelable = [new(0, 1), new(1, 1)] }));
    }

    [Fact]
    public void AtEpsilonOneEveryKindOfDecisionIsDrawnEvenlyAmongItsOptionsButThePotionBelt()
    {
        foreach (var decision in EveryKind())
        {
            var options = decision.Legal.Where(a => !a.StartsWith("discard_potion")).ToList();
            var picks = Enumerable.Range(0, 600).Select(seed =>
            {
                var l0 = new MacroBrain(V, uniform: new Uniform(1, new Random(seed)));
                var pick = l0.Choose(decision);
                Assert.True(l0.Explored);
                return pick;
            }).ToList();
            foreach (var option in options)
                Assert.InRange(picks.Count(p => p == option), 600 / options.Count - 70, 600 / options.Count + 70);
            Assert.Equal(picks.Count, picks.Count(options.Contains));
        }
    }

    [Fact]
    public void AtEpsilonOneAPromptsPicksAreDrawnEvenlyAndAtEpsilonZeroPointOneMostAnswersAreVs()
    {
        var picks = Enumerable.Range(0, 900).Select(seed => new MacroBrain(V, uniform: new Uniform(1, new Random(seed))).Choose(Prompt(0, "BAD", "WORSE", "A"))).ToList();
        // No pick or one: half each; one: each card a third of that.
        Assert.InRange(picks.Count(p => p.Count == 0), 380, 520);
        foreach (int card in new[] { 0, 1, 2 }) Assert.InRange(picks.Count(p => p.SequenceEqual([card])), 110, 190);
        Assert.All(Enumerable.Range(0, 100), seed => Assert.Single(new MacroBrain(V, uniform: new Uniform(1, new Random(seed))).Choose(Prompt(1, "BAD", "WORSE"))));

        var l0 = new MacroBrain(V, uniform: new Uniform(0.1, new Random(1)));
        var treasure = Enumerable.Range(0, 1000).Select(_ => (l0.Choose(Treasure()), l0.Explored)).ToList();
        // V's best 90 % plus a third of the draws.
        Assert.InRange(treasure.Count(p => p.Item1 == "relic:1"), 900, 960);
        Assert.InRange(treasure.Count(p => p.Explored), 70, 130);
    }

    [Fact]
    public void ExploringAPromptItMayStopOrPickButNotBelowTheMinimum()
    {
        // Stopping is worth 1, what its relic gives; so by their logs the weights stay those below.
        var worth = new Worth(new() { [Fixtures.Floor1().Relics[0].Id] = 1, ["BAD"] = Math.Exp(-2) - 1, ["WORSE"] = Math.Exp(-3) - 1 });
        var l0 = new MacroBrain(worth, new Softmax(1, new Random(1)));
        var skippable = Enumerable.Range(0, 1000).Select(_ => (l0.Choose(Prompt(0, "BAD", "WORSE")), l0.Explored)).ToList();
        // Weights 1 : e^-2 : e^-3 for stopping, BAD, WORSE.
        Assert.InRange(skippable.Count(p => p.Item1.Count == 1), 100, 220);
        Assert.All(skippable, p => Assert.Equal(p.Item1.Count == 1, p.Explored));
        Assert.All(Enumerable.Range(0, 100), _ => Assert.Single(l0.Choose(Prompt(1, "BAD", "WORSE"))));
    }
}
