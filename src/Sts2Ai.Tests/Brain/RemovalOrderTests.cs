using Sts2Ai.Brain;
using Sts2Ai.Brain.Macro;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

/// <summary>A removal takes the basic Strikes, Defends and curses first (<see cref="RemovalOrder"/>, the user's rule, 2026-11-22).</summary>
public class RemovalOrderTests
{
    /// <summary>A V of 10 plus what each card is worth: it would rather lose Neutralize, then the upgraded Defend and Strike.</summary>
    private sealed class Worth : IRunValue
    {
        public string Name => "test";

        public double Value(RunView run) => 10 + run.Deck.Sum(c => (c.Id, c.Up) switch
        {
            ("NEUTRALIZE", _) => -3,
            ("DEFEND_SILENT", 1) => -2.5,
            ("STRIKE_SILENT", 1) => -2,
            ("STRIKE_SILENT", null) => -1,
            ("REGRET", _) => -0.5,
            _ => 0,
        });
    }

    private static readonly Worth V = new();

    private static CardView Card(string id, int? up = null, params string[] keywords) =>
        new() { Id = id, Up = up, Keywords = keywords.Length > 0 ? keywords : null };

    private static readonly CardView Strike = Card("STRIKE_SILENT"), StrikeUp = Card("STRIKE_SILENT", 1), DefendUp = Card("DEFEND_SILENT", 1),
        Neutralize = Card("NEUTRALIZE"), Regret = Card("REGRET");

    private static CardChoice Prompt(int min, int max, IReadOnlyList<CardView> options, string purpose = "deck_for_removal", bool inCombat = false) => new()
    {
        Options = options,
        Min = min,
        Max = max,
        Context = new ChoiceContext { Purpose = purpose, Cancelable = min == 0, InCombat = inCombat },
    };

    private static ChoiceDecision Removal(CardChoice choice, IMacroClone? clone = null) =>
        new(choice, () => new Observation { Run = Fixtures.Floor1() with { Deck = choice.Options }, Screen = new TreasureScreen { Relics = [] } }, clone);

    [Fact]
    public void ARemovalTakesAPlainStrikeOrACurseFirstWhateverVSays()
    {
        // V would remove Neutralize (+3); of the cards that go first, the plain Strike (+1) beats Regret (+0.5).
        Assert.Equal([1], new MacroBrain(V).Choose(Removal(Prompt(1, 1, [Neutralize, Strike, StrikeUp, Regret]))).Picks);
        // An upgraded Strike or Defend is any other card: with no card that goes first, V chooses freely.
        Assert.Equal([0], new MacroBrain(V).Choose(Removal(Prompt(1, 1, [Neutralize, StrikeUp, DefendUp]))).Picks);
        Assert.Equal([1], new MacroBrain(V).Choose(Removal(Prompt(1, 1, [StrikeUp, Card("DOUBT")]))).Picks);
    }

    [Fact]
    public void APromptOfMorePicksThanCardsThatGoFirstTakesThemAllAndTheRestFreely()
    {
        // The greedy answer lists its picks in the order it took them.
        Assert.Equal([1, 0], new MacroBrain(V).Choose(Removal(Prompt(2, 2, [Neutralize, Strike, StrikeUp, DefendUp]))).Picks);
        Assert.Equal([1, 3], new MacroBrain(V).Choose(Removal(Prompt(2, 2, [Neutralize, Strike, StrikeUp, Regret]))).Picks);
        // Picking none, where the prompt allows it, stays allowed: removing the plain Defend changes nothing V reads.
        Assert.Empty(new MacroBrain(V).Choose(Removal(Prompt(0, 1, [Neutralize, Card("DEFEND_SILENT")]))).Picks);
    }

    [Fact]
    public void ATransformAndACombatsPromptAreNotRemovals()
    {
        Assert.Equal([1, 3], RemovalOrder.Firsts(Prompt(1, 1, [Neutralize, Strike, StrikeUp, Regret])));
        Assert.Empty(RemovalOrder.Firsts(Prompt(1, 1, [Neutralize, Strike], "deck_for_transformation")));
        Assert.Empty(RemovalOrder.Firsts(Prompt(1, 1, [Neutralize, Strike], inCombat: true)));
        Assert.Equal([0], new MacroBrain(V).Choose(Removal(Prompt(1, 1, [Neutralize, Strike], "deck_for_transformation"))).Picks);
    }

    [Fact]
    public void AnEvenDrawTakesTheCardsThatGoFirstFirst()
    {
        var choice = Prompt(1, 2, [Neutralize, Strike, StrikeUp, Regret]);
        var drawn = Enumerable.Range(0, 400).Select(seed => new MacroBrain(V, uniform: new Uniform(1, new Random(seed))).Choose(Removal(choice))).ToList();
        Assert.All(drawn, a => Assert.True(a.Explored && RemovalOrder.Keeps(choice, a.Picks), string.Join(",", a.Picks)));
        // One pick is either card that goes first; two are both.
        Assert.Equal(["1", "1,3", "3"], drawn.Select(a => string.Join(",", a.Picks)).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>A node's clone that plays each path in a toy game.</summary>
    private sealed class Toy(Func<IReadOnlyList<MacroAction>, Afterstate> play) : IMacroClone
    {
        public IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<MacroAction>> paths) => [.. paths.Select(play)];
    }

    private static Afterstate Exit(IReadOnlyList<CardView> deck) => new(Fixtures.Floor1() with { Deck = deck }, Surfaces.Map, null, []);

    private static IReadOnlyList<CardView> Without(IReadOnlyList<CardView> deck, IReadOnlyList<int> picks) => [.. deck.Where((_, i) => !picks.Contains(i))];

    [Fact]
    public void OnTheClonesLocalSearchEveryAnswerKeepsTheRule()
    {
        var choice = Prompt(2, 2, [Neutralize, Strike, Strike, StrikeUp, DefendUp]);
        var toy = new Toy(path => path is [var pick] ? Exit(Without(choice.Options, pick.Picks)) : throw new InvalidOperationException("one pick a path"));
        Assert.Equal([1, 2], new MacroBrain(V).Choose(Removal(choice, toy)).Picks);
        var one = Prompt(2, 2, [Neutralize, Strike, StrikeUp, DefendUp]);
        var other = new Toy(path => Exit(Without(one.Options, path[0].Picks)));
        Assert.Equal([0, 1], new MacroBrain(V).Choose(Removal(one, other)).Picks);
        Assert.All(Enumerable.Range(0, 100), seed => Assert.True(RemovalOrder.Keeps(choice,
            new MacroBrain(V, uniform: new Uniform(1, new Random(seed))).Choose(Removal(choice, toy)).Picks)));
    }

    /// <summary>A merchant whose one service is a removal, in SL: the removal opens its prompt on the clone.</summary>
    private static Afterstate Merchant(IReadOnlyList<CardView> deck, IReadOnlyList<MacroAction> path) => path switch
    {
        [] => new(Fixtures.Floor1() with { Deck = deck }, Surfaces.Shop, new ShopScreen { Entries = [], RemovalCost = 75 }, [MacroAction.Remove, MacroAction.Leave]),
        [var leave] when leave == MacroAction.Leave => Exit(deck),
        [_] => new(Fixtures.Floor1() with { Deck = deck }, Surfaces.CardChoice, new CardChoiceScreen { Choice = Prompt(1, 1, deck) }, []),
        [_, var pick] => Exit(Without(deck, pick.Picks)),
        _ => throw new InvalidOperationException("no more answers"),
    };

    [Fact]
    public void AMerchantsRemovalIsValuedByARemovalTheRuleAllowsInSlAndInNoSl()
    {
        IReadOnlyList<CardView> deck = [Neutralize, Strike, DefendUp];
        double strikeGone = V.Value(Fixtures.Floor1() with { Deck = [Neutralize, DefendUp] });
        // SL: the clone's search answers the prompt inside the node with the plain Strike alone.
        var toy = new Toy(path => Merchant(deck, path));
        var root = toy.After([[]])[0];
        var sl = new MacroDecision(root.Surface!, root.Legal, () => new Observation { Run = root.Run, Screen = root.Screen! }, toy);
        Assert.Equal(strikeGone, new MacroBrain(V).Options(sl).Single(o => o.Action == MacroAction.Remove).Value, 12);
        // NoSL: the removal's predicted afterstate is the Strike gone; with no card that goes first, Neutralize.
        var noSl = new MacroDecision(root.Surface!, root.Legal, () => new Observation { Run = root.Run, Screen = root.Screen! });
        var after = new MacroBrain(V).Options(noSl).Single(o => o.Action == MacroAction.Remove).After;
        Assert.Equal(["NEUTRALIZE", "DEFEND_SILENT"], after.Deck.Select(c => c.Id));
        // An Eternal curse cannot be removed, so it does not hold the others back.
        var eternal = new MacroDecision(Surfaces.Shop, [MacroAction.Remove, MacroAction.Leave], () => new Observation
        {
            Run = Fixtures.Floor1() with { Deck = [Neutralize, StrikeUp, Card("ASCENDERS_BANE", null, "Eternal")] },
            Screen = new ShopScreen { Entries = [], RemovalCost = 75 },
        });
        Assert.Equal(["STRIKE_SILENT", "ASCENDERS_BANE"], new MacroBrain(V).Options(eternal).Single(o => o.Action == MacroAction.Remove).After.Deck.Select(c => c.Id));
    }
}
