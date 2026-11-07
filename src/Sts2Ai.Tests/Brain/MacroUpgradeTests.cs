using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

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
