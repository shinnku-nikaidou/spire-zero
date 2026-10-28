using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class SoftmaxTests
{
    [Fact]
    public void BestIsTheFirstBest() => Assert.Equal(1, Softmax.Best([0.1, 0.5, 0.5, -1]));

    [Fact]
    public void DrawsInProportionToTheExponentOfTheValue()
    {
        // At τ 2, weights 1 : 1/2 : e^-20.
        var softmax = new Softmax(2, new Random(1));
        var counts = new int[3];
        for (int i = 0; i < 30_000; i++) counts[softmax.Draw([3, 3 - 2 * Math.Log(2), -37])]++;
        Assert.InRange(counts[0] / 30_000.0, 0.65, 0.68);
        Assert.InRange(counts[1] / 30_000.0, 0.32, 0.35);
        Assert.Equal(0, counts[2]);
    }

    [Fact]
    public void AColdOneTakesTheBest()
    {
        var softmax = new Softmax(1e-6, new Random(1));
        Assert.All(Enumerable.Range(0, 1000), _ => Assert.Equal(2, softmax.Draw([0, 0.5, 0.501])));
    }

    [Fact]
    public void ADrawWithinAHundredthOfTheTemperatureIsATie()
    {
        var softmax = new Softmax(0.002, new Random(1));
        Assert.False(softmax.Explored(0.79009, 0.7901));
        Assert.True(softmax.Explored(0.7898, 0.7901));
    }

    [Fact]
    public void RefusesATemperatureOfZero() => Assert.Throws<ArgumentOutOfRangeException>(() => new Softmax(0, new Random(1)));
}

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

public class CombatExplorationTests
{
    private static readonly CombatAction Play = CombatAction.Play(0, 1);

    /// <summary>A search that always prefers playing the card a little, and plans to end the turn after it.</summary>
    private static readonly CombatRoute Searched = new("test", (_, _, _, _) => new(Play, [CombatAction.EndTurn],
        [new(Play.ToString(), 0.5, 0, 8), new(CombatAction.EndTurn.ToString(), 0.49, 0, 8)]));

    private static CombatDecision Decision() => new([Play, CombatAction.EndTurn], Fixtures.Slugs);

    [Fact]
    public void AnExploringRouteDrawsByTheSearchsValuesAndTheLogMarksIt()
    {
        var macro = new MacroBrain(RunValue.Initial);
        var dispatcher = new Dispatcher("run-1", PlayEnvironment.NoSL, Searched.Exploring(new Softmax(0.01, new Random(1))),
            new("p0", PromptBrain.Instance), new("l0", macro), new("l0", macro), log: true);
        var picks = Enumerable.Range(0, 400).Select(_ => dispatcher.Choose(Decision())).ToList();

        // Weights 1 : e^-1, so about 27% end the turn.
        Assert.InRange(picks.Count(p => p == CombatAction.EndTurn), 80, 140);
        Assert.All(dispatcher.Log, e =>
        {
            Assert.Equal(e.Chosen == CombatAction.EndTurn.ToString() ? true : null, e.Explored);
            Assert.Equal(e.Explored == true ? null : ["end_turn"], e.Plan);
            Assert.Equal(2, e.Candidates!.Count);
        });
    }

    [Fact]
    public void ATieIsDrawnButNotMarked()
    {
        var tied = new CombatRoute("test", (_, _, _, _) => new(Play, [CombatAction.EndTurn],
            [new(Play.ToString(), 0.5, 0, 8), new(CombatAction.EndTurn.ToString(), 0.5, 0, 8)])).Exploring(new Softmax(0.001, new Random(1)));
        var results = Enumerable.Range(0, 100).Select(_ => tied.Decide(Decision(), 0, null, [])).ToList();
        Assert.InRange(results.Count(r => r.First == CombatAction.EndTurn), 30, 70);
        Assert.All(results, r => Assert.False(r.Explored));
    }

    [Fact]
    public void ARouteWithoutCandidatesIsLeftAlone()
    {
        var route = CombatRoute.Of("test", new PlayPolicy(new Random(1))).Exploring(new Softmax(100, new Random(1)));
        Assert.All(Enumerable.Range(0, 50), _ => Assert.Equal(Play, route.Decide(Decision(), 0, null, []).First));
    }
}

public class MacroUpgradeTests
{
    /// <summary>A V that is the HP plus the deck's total damage, and a little for each upgraded card.</summary>
    private sealed class Damage : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) =>
            run.Hp + run.Deck.Sum(c => (double)(c.Vars?.GetValueOrDefault("Damage") ?? 0)) + 0.1 * run.Deck.Count(c => c.Up > 0);
    }

    private static CardView Card(string id, int damage, int upgraded) => new()
    {
        Id = id, Cost = 1, Vars = new Dictionary<string, decimal> { ["Damage"] = damage },
        Upgraded = new CardView { Id = id, Up = 1, Cost = 1, Vars = new Dictionary<string, decimal> { ["Damage"] = upgraded } },
    };

    private static readonly CardView Small = Card("AAA", 6, 7), Big = Card("ZZZ", 6, 12);

    private static MacroDecision Rest(int heal, params CardView[] deck) => new(Surfaces.Rest, ["rest:HEAL", "rest:SMITH"],
        () => new Observation
        {
            Run = Fixtures.Floor1() with { Hp = 40, Deck = deck },
            Screen = new RestScreen { Options = [new("HEAL", true, heal), new("SMITH", true)] },
        });

    [Fact]
    public void SmithingIsWorthItsBestUpgradeWhateverTheDecksOrder()
    {
        var l0 = new MacroBrain(new Damage());
        // The best upgrade adds 6 damage: smithing beats a heal of 5, whichever card comes first; the other upgrade
        // adds only 1, and alone it loses to the heal.
        Assert.Equal("rest:SMITH", l0.Choose(Rest(5, Small, Big)));
        Assert.Equal("rest:SMITH", l0.Choose(Rest(5, Big, Small)));
        Assert.Equal("rest:HEAL", l0.Choose(Rest(5, Small, Small)));
    }

    /// <summary>A V that likes the run as it is best: each HP and each upgraded card costs.</summary>
    private sealed class Unchanged : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => -run.Hp - 2 * run.Deck.Count(c => c.Up > 0);
    }

    private static ChoiceDecision UpgradePrompt() => new(new CardChoice
    {
        Options = [Small, Big],
        Min = 0,
        Max = 1,
        Context = new ChoiceContext { Purpose = "deck_for_upgrade", Cancelable = true, InCombat = false },
    }, () => new Observation { Run = Fixtures.Floor1() with { Hp = 40, Deck = [Small, Big] }, Screen = new TreasureScreen { Relics = [] } });

    [Fact]
    public void ThePromptSmithingOpensTakesTheUpgradeItWasValuedBy()
    {
        // An upgrade beats the heal, and keeping the run as it is beats both; but the rest site must be used, and
        // stopping the prompt would only go back to it.
        var l0 = new MacroBrain(new Unchanged());
        Assert.Equal("rest:SMITH", l0.Choose(Rest(5, Small, Big)));
        Assert.Single(l0.Choose(UpgradePrompt()));
        // A prompt that no answer of L0's opened may still be stopped.
        Assert.Empty(l0.Choose(UpgradePrompt()));
    }

    [Fact]
    public void TheUpgradePromptTakesTheCardWhosePreviewIsBest()
    {
        var l0 = new MacroBrain(new Damage());
        var prompt = new ChoiceDecision(new CardChoice
        {
            Options = [Small, Big],
            Min = 1,
            Max = 1,
            Context = new ChoiceContext { Purpose = "deck_for_upgrade", Cancelable = false, InCombat = false },
        }, () => new Observation { Run = Fixtures.Floor1() with { Deck = [Small, Big] }, Screen = new TreasureScreen { Relics = [] } });
        Assert.Equal([1], l0.Choose(prompt));
    }
}
