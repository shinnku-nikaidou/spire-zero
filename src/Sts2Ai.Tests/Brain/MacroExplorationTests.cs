using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class MacroExplorationTests
{
    /// <summary>A V that is the sum of what each card and relic is worth.</summary>
    private sealed class Worth(Dictionary<string, double> worth) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => run.Deck.Sum(c => worth.GetValueOrDefault(c.Id)) + run.Relics.Sum(r => worth.GetValueOrDefault(r.Id));
    }

    private static readonly Worth V = new(new() { ["A"] = 1, ["B"] = 3, ["C"] = 2.9, ["BAD"] = -2, ["WORSE"] = -3 });

    private static MacroDecision Treasure() => new(Surfaces.Treasure, Fixtures.Acts("relic:0", "relic:1", "relic:2"),
        () => new Observation { Run = Fixtures.Floor1(), Screen = new TreasureScreen { Relics = ["A", "B", "C"] } });

    private static ChoiceDecision Prompt(int min, params string[] cards) => new(new CardChoice
    {
        Options = [.. cards.Select(id => new CardView { Id = id })],
        Min = min,
        Max = 1,
        Context = new ChoiceContext { Purpose = "choose_a_card_screen", Cancelable = min == 0, InCombat = false },
    }, () => new Observation { Run = Fixtures.Floor1(), Screen = new TreasureScreen { Relics = [] } });

    [Fact]
    public void ARemovalIsWorthOnlyWhatTheGameLetsBeRemoved()
    {
        // Removing the Eternal BAD would be worth the most, but the game offers only A; without A the deck is worse.
        var deck = new List<CardView> { new() { Id = "A" }, new() { Id = "BAD", Keywords = ["Eternal", "Unplayable"] } };
        var shop = new MacroDecision(Surfaces.Shop, Fixtures.Acts("remove", "leave"), () => new Observation
        {
            Run = Fixtures.Floor1() with { Deck = deck },
            Screen = new ShopScreen { Entries = [], RemovalCost = 75 },
        });
        Assert.Equal(MacroAction.Leave, new MacroBrain(V).Choose(shop).Action);
    }

    [Fact]
    public void WithoutExplorationItTakesTheBest()
    {
        var l0 = new MacroBrain(V);
        Assert.Equal(new MacroAnswer(MacroAction.Of(MacroAction.Relic, 1)).Action, l0.Choose(Treasure()).Action);
        Assert.False(l0.Choose(Treasure()).Explored);
        Assert.Equal([1], l0.Choose(Prompt(0, "A", "B")).Picks);
        Assert.Empty(l0.Choose(Prompt(0, "BAD", "WORSE")).Picks);
        var forced = l0.Choose(Prompt(1, "BAD", "WORSE"));
        Assert.Equal([0], forced.Picks);
        Assert.False(forced.Explored);
    }

    private static List<CardView> Cards(string[] ids) => [.. ids.Select(id => new CardView { Id = id })];

    private static MacroDecision Rewards(params string[] cards) => new(Surfaces.Rewards, Fixtures.Acts("reward:0", "rewards_done"), () => new Observation
    {
        Run = Fixtures.Floor1(),
        Screen = new RewardsScreen { Rewards = [new RewardView { Index = 0, Kind = "card", Cards = Cards(cards) }] },
    });

    private static MacroDecision CardReward(params string[] cards) => new(Surfaces.CardReward,
        [.. cards.Select((_, i) => MacroAction.Of(MacroAction.Card, i)), new MacroAction(MacroAction.CardAlt, "Skip")],
        () => new Observation { Run = Fixtures.Floor1(), Screen = new CardRewardScreen { Cards = Cards(cards), Alternatives = ["Skip"] } });

    [Fact]
    public void DrawingEvenlyCardRewardsTakeAnOutcomeDrawnAmongTheirCardsAndSkippingAndTheCardScreenKeepsTheDraw()
    {
        var picks = new List<(string Pick, bool Explored)>();
        for (int seed = 0; seed < 4000; seed++)
        {
            var l0 = new MacroBrain(V, uniform: new Uniform(1, new Random(seed)));
            var rewards = l0.Choose(Rewards("WORSE", "BAD", "A"));
            if (rewards.Action == MacroAction.RewardsDone)
            {
                picks.Add(("skip", rewards.Explored));
                continue;
            }
            var card = l0.Choose(CardReward("WORSE", "BAD", "A"));
            picks.Add((card.Action.ToString(), card.Explored));
        }
        // Every outcome has 1/4 whatever V says (it would open the reward and take A); skipping is never opening it.
        foreach (var outcome in new[] { "card:0", "card:1", "card:2", "skip" })
            Assert.InRange(picks.Count(p => p.Pick == outcome), 900, 1100);
        Assert.All(picks, p => Assert.True(p.Explored));
        Assert.DoesNotContain(picks, p => p.Pick == "card_alt:Skip");
    }

    /// <summary>One decision of each kind outside card rewards, each offering the potion belt too (a potion to drink, and its discard, which
    /// <see cref="MacroBrain.Offered"/> leaves out); V's first is option 0 or 1.</summary>
    private static IEnumerable<MacroDecision> EveryKind()
    {
        static Observation On(ScreenView screen) => new() { Run = Fixtures.Floor1() with { Potions = ["FRUIT_JUICE", null, null] }, Screen = screen };
        const string drink = "potion:0", belt = "discard_potion:0";
        yield return new(Surfaces.Treasure, Fixtures.Acts("relic:0", "relic:1", "relic:2", drink, belt), () => On(new TreasureScreen { Relics = ["A", "B", "C"] }));
        yield return new(Surfaces.Shop, Fixtures.Acts("buy:0", "buy:1", "leave", drink, belt), () => On(new ShopScreen
        {
            Entries = [new() { Slot = 0, Kind = "card", Card = new() { Id = "A" }, Cost = 1 }, new() { Slot = 1, Kind = "card", Card = new() { Id = "B" }, Cost = 1 }],
        }));
        yield return new(Surfaces.Rest, Fixtures.Acts("rest:HEAL", "rest:SMITH", drink, belt), () => On(new RestScreen { Options = [new("HEAL", true, 10), new("SMITH", true)] }));
        yield return new(Surfaces.Event, Fixtures.Acts("event:0", "event:1", "event:2", drink, belt), () => On(new EventScreen
        {
            Id = "E", Finished = false, Options = [new() { Index = 0, Key = "a" }, new() { Index = 1, Key = "b" }, new() { Index = 2, Key = "c" }],
        }));
        yield return new(Surfaces.Map, Fixtures.Acts("map:0,1", "map:1,1", drink, belt), () => On(new MapScreen { Travelable = [new(0, 1), new(1, 1)] }));
    }

    [Fact]
    public void AtEpsilonOneEveryKindOfDecisionIsDrawnEvenlyAmongItsOptionsAPotionToDrinkAmongThem()
    {
        foreach (var decision in EveryKind())
        {
            var options = decision.Legal.Where(a => a.Verb != MacroAction.DiscardPotion).ToList();
            var picks = Enumerable.Range(0, 600).Select(seed =>
            {
                var l0 = new MacroBrain(V, uniform: new Uniform(1, new Random(seed)));
                var answer = l0.Choose(decision);
                Assert.True(answer.Explored);
                return answer.Action;
            }).ToList();
            foreach (var option in options)
                Assert.InRange(picks.Count(p => p == option), 600 / options.Count - 70, 600 / options.Count + 70);
            Assert.Equal(picks.Count, picks.Count(options.Contains));
        }
    }

    [Fact]
    public void AtEpsilonOneAPromptsPicksAreDrawnEvenlyAndAtEpsilonZeroPointOneMostAnswersAreVs()
    {
        var picks = Enumerable.Range(0, 900).Select(seed => new MacroBrain(V, uniform: new Uniform(1, new Random(seed))).Choose(Prompt(0, "BAD", "WORSE", "A")).Picks).ToList();
        // No pick or one: half each; one: each card a third of that.
        Assert.InRange(picks.Count(p => p.Count == 0), 380, 520);
        foreach (int card in new[] { 0, 1, 2 }) Assert.InRange(picks.Count(p => p.SequenceEqual([card])), 110, 190);
        Assert.All(Enumerable.Range(0, 100), seed => Assert.Single(new MacroBrain(V, uniform: new Uniform(1, new Random(seed))).Choose(Prompt(1, "BAD", "WORSE")).Picks));

        var l0 = new MacroBrain(V, uniform: new Uniform(0.1, new Random(1)));
        var treasure = Enumerable.Range(0, 1000).Select(_ => l0.Choose(Treasure())).ToList();
        // V's best 90 % plus a third of the draws.
        Assert.InRange(treasure.Count(p => p.Action == MacroAction.Of(MacroAction.Relic, 1)), 900, 960);
        Assert.InRange(treasure.Count(p => p.Explored), 70, 130);
    }

    [Fact]
    public void TheSoftmaxDrawsByTheRatiosOfVsValuesAndMarksWhatItTookBelowTheBest()
    {
        var picks = Enumerable.Range(0, 1000).Select(seed =>
        {
            var l0 = new MacroBrain(V, softmax: new Softmax(0.05, new Random(seed)));
            var answer = l0.Choose(Treasure());
            return (Pick: answer.Action.ToString(), answer.Explored);
        }).ToList();
        // log 3 - log 2.9 = 0.034: C is drawn e^(-0.034 / 0.05) = 0.51 times as often as B; A, a third of B, never.
        Assert.InRange(picks.Count(p => p.Pick == "relic:1"), 610, 715);
        Assert.InRange(picks.Count(p => p.Pick == "relic:2"), 285, 390);
        Assert.DoesNotContain(picks, p => p.Pick == "relic:0");
        Assert.All(picks, p => Assert.Equal(p.Pick != "relic:1", p.Explored));
        // A prompt's step is drawn the same way: B or C, never A.
        var prompt = Enumerable.Range(0, 300).Select(seed => new MacroBrain(V, softmax: new Softmax(0.05, new Random(seed))).Choose(Prompt(1, "A", "B", "C")).Picks).ToList();
        Assert.Contains(prompt, p => p.SequenceEqual([1]));
        Assert.Contains(prompt, p => p.SequenceEqual([2]));
        Assert.DoesNotContain(prompt, p => p.SequenceEqual([0]));
    }
}
