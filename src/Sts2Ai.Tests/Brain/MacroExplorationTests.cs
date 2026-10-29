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

        // Weights e^-2 : 1 : e^-0.1 for A, B, C.
        Assert.InRange(picks.Count(p => p == "relic:0"), 40, 100);
        Assert.InRange(picks.Count(p => p == "relic:1"), 470, 560);
        Assert.All(dispatcher.Log, e => Assert.Equal(e.Chosen != "relic:1" ? true : null, e.Explored));
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
    public void ExploringCardRewardsItDrawsSomeOutcomesEvenlyWhateverVSaysAndTheCardScreenKeepsTheDraw()
    {
        var picks = new List<(string Pick, bool Explored)>();
        for (int seed = 0; seed < 4000; seed++)
        {
            var l0 = new MacroBrain(V, cards: new CardDraw(0.5, new Random(seed)));
            if (l0.Choose(Rewards("WORSE", "BAD", "A")) == "rewards_done")
                picks.Add(("skip", l0.Explored));
            else
                picks.Add((l0.Choose(CardReward("WORSE", "BAD", "A")), l0.Explored));
        }
        // Undrawn, V opens the reward and takes A (worth 1, skipping 0); drawn (half the time), each outcome has 1/4.
        Assert.InRange(picks.Count(p => p.Pick == "card:2"), 2350, 2650);
        foreach (var other in new[] { "card:0", "card:1", "skip" })
            Assert.InRange(picks.Count(p => p.Pick == other), 400, 600);
        Assert.All(picks, p => Assert.Equal(p.Pick != "card:2", p.Explored));
        Assert.DoesNotContain(picks, p => p.Pick == "card_alt:Skip");
    }

    [Fact]
    public void ExploringAPromptItMayStopOrPickButNotBelowTheMinimum()
    {
        var l0 = new MacroBrain(V, new Softmax(1, new Random(1)));
        var skippable = Enumerable.Range(0, 1000).Select(_ => (l0.Choose(Prompt(0, "BAD", "WORSE")), l0.Explored)).ToList();
        // Weights 1 : e^-2 : e^-3 for stopping, BAD, WORSE.
        Assert.InRange(skippable.Count(p => p.Item1.Count == 1), 100, 220);
        Assert.All(skippable, p => Assert.Equal(p.Item1.Count == 1, p.Explored));
        Assert.All(Enumerable.Range(0, 100), _ => Assert.Single(l0.Choose(Prompt(1, "BAD", "WORSE"))));
    }
}
