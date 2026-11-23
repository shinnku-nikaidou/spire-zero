using Sts2Ai.Brain;
using Sts2Ai.Brain.Macro;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

/// <summary>A run tilted toward card families (<see cref="CardLock"/>; plans/2026-11-20-1231-silent-shiv-poison.md, 2.2).</summary>
public class CardLockTests
{
    /// <summary>A V of 10 plus what each card is worth, and for K: worth 0.5 once, 3 less for each copy past it.</summary>
    private sealed class Worth(Dictionary<string, double> worth) : IRunValue
    {
        public string Name => "test";

        public double Value(RunView run)
        {
            int k = run.Deck.Count(c => c.Id == "K");
            return 10 + run.Deck.Sum(c => worth.GetValueOrDefault(c.Id)) + (k > 0 ? 0.5 - 3 * (k - 1) : 0);
        }
    }

    private static readonly Worth V = new(new() { ["A"] = 1, ["B"] = 3, ["C"] = 2.9, ["BAD"] = -1, ["J"] = -1.2 });

    /// <summary>K and J favored, B refused.</summary>
    private static CardLock Lock(double by) => new(new HashSet<string> { "K", "J" }, by, new HashSet<string> { "B" }, new HashSet<string>(), new HashSet<string>());

    /// <summary>The Regent's Tyranny runs as a lock (plans/2026-11-23-1545-sl-regent-g0.md): T favored by 10, kept and upgraded first.</summary>
    private static CardLock Tyranny() => new(new HashSet<string> { "T" }, 10, new HashSet<string>(), new HashSet<string> { "T" }, new HashSet<string> { "T" });

    private static List<CardView> Cards(params string[] ids) => [.. ids.Select(id => new CardView { Id = id })];

    private static MacroDecision CardReward(IReadOnlyList<CardView> deck, params string[] cards) => new(Surfaces.CardReward,
        [.. cards.Select((_, i) => MacroAction.Of(MacroAction.Card, i)), new MacroAction(MacroAction.CardAlt, "Skip")],
        () => new Observation { Run = Fixtures.Floor1() with { Deck = deck }, Screen = new CardRewardScreen { Cards = Cards(cards), Alternatives = ["Skip"] } });

    private static ChoiceDecision Removal(IReadOnlyList<CardView> deck, params string[] offered) => new(new CardChoice
    {
        Options = Cards(offered),
        Min = 1,
        Max = 1,
        Context = new ChoiceContext { Purpose = "deck_for_removal", Cancelable = false, InCombat = false },
    }, () => new Observation { Run = Fixtures.Floor1() with { Deck = deck }, Screen = new TreasureScreen { Relics = [] } });

    [Fact]
    public void AnAfterstateIsWeighedWithTheBonusForTheFavoredCardsItAddsAndRefusedCardsBreakIt()
    {
        var lockBy = Lock(0.2);
        Assert.Equal(5, lockBy.Worth(Cards("A"), Cards("A", "C"), 5));
        Assert.Equal(5 * Math.Exp(0.4), lockBy.Worth(Cards("A"), Cards("A", "K", "J"), 5), 12);
        Assert.Equal(5 * Math.Exp(-0.2), lockBy.Worth(Cards("K", "A"), Cards("A"), 5), 12);
        Assert.Equal(double.NegativeInfinity, lockBy.Worth(Cards("A"), Cards("A", "B"), 5));
        Assert.False(lockBy.Breaks(Cards("B", "A"), Cards("A")));
    }

    [Fact]
    public void AFavoredCardIsTakenWhereItsBonusCoversVsGapAndOnlyAsLongAsVWantsTheCopy()
    {
        // V would take A (10 + 1) over K (10 + 0.5); 0.2 in log V makes K worth 12.8, 0.02 only 10.7.
        Assert.Equal(MacroAction.Of(MacroAction.Card, 1), new MacroBrain(V).Choose(CardReward([], "K", "A")).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Card, 0), new MacroBrain(V, cards: Lock(0.2)).Choose(CardReward([], "K", "A")).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Card, 1), new MacroBrain(V, cards: Lock(0.02)).Choose(CardReward([], "K", "A")).Action);
        // A second K is worth 3 less (7.5 e^0.2 = 9.2 against 10.5): its bonus does not make up for it, so the reward is skipped.
        Assert.Equal(new MacroAction(MacroAction.CardAlt, "Skip"), new MacroBrain(V, cards: Lock(0.2)).Choose(CardReward(Cards("K"), "K")).Action);
    }

    [Fact]
    public void ACardToRefuseIsNeitherTakenDrawnNorBought()
    {
        // V would take B; refused, it takes C, the next best, also when a draw picks B.
        Assert.Equal(MacroAction.Of(MacroAction.Card, 0), new MacroBrain(V).Choose(CardReward([], "B", "C")).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Card, 1), new MacroBrain(V, cards: Lock(0.2)).Choose(CardReward([], "B", "C")).Action);
        Assert.All(Enumerable.Range(0, 200), seed => Assert.NotEqual(MacroAction.Of(MacroAction.Card, 0),
            new MacroBrain(V, uniform: new Uniform(1, new Random(seed)), cards: Lock(0.2)).Choose(CardReward([], "B", "C")).Action));

        var shop = new MacroDecision(Surfaces.Shop, Fixtures.Acts("buy:0", "buy:1", "leave"), () => new Observation
        {
            Run = Fixtures.Floor1() with { Gold = 500, Deck = [] },
            Screen = new ShopScreen
            {
                Entries = [new() { Slot = 0, Kind = "card", Card = new() { Id = "B" }, Cost = 1 }, new() { Slot = 1, Kind = "card", Card = new() { Id = "C" }, Cost = 1 }],
            },
        });
        Assert.Equal(MacroAction.Of(MacroAction.Buy, 0), new MacroBrain(V).Choose(shop).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Buy, 1), new MacroBrain(V, cards: Lock(0.2)).Choose(shop).Action);
    }

    [Fact]
    public void ARemovalWeighsAFavoredCardWithItsBonus()
    {
        // V would remove J (10 - 1 left) over BAD (10 - 1.2 left); with 0.2 for J, removing it leaves 9 e^-0.2 = 7.4.
        var deck = Cards("J", "BAD");
        Assert.Equal([0], new MacroBrain(V).Choose(Removal(deck, "J", "BAD")).Picks);
        Assert.Equal([1], new MacroBrain(V, cards: Lock(0.2)).Choose(Removal(deck, "J", "BAD")).Picks);
    }

    /// <summary>A node's clone that plays each path in a toy game.</summary>
    private sealed class Toy(Func<IReadOnlyList<string>, Afterstate> play) : IMacroClone
    {
        public IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<MacroAction>> paths) =>
            [.. paths.Select(p => play([.. p.Select(a => a.ToString())]))];
    }

    /// <summary>A shop of three cards for 10 gold, 5 each: B, C and J.</summary>
    private static Afterstate CardShop(IReadOnlyList<string> path)
    {
        string[] stock = ["B", "C", "J"];
        var run = Fixtures.Floor1() with { Gold = 10, Deck = [] };
        foreach (var action in path)
        {
            if (action == "leave") return new(run, Surfaces.Map, null, []);
            run = run with { Gold = run.Gold - 5, Deck = [.. run.Deck, new CardView { Id = stock[int.Parse(action["buy:".Length..])] }] };
        }
        var legal = Enumerable.Range(0, stock.Length).Where(i => run.Gold >= 5 && run.Deck.All(c => c.Id != stock[i]))
            .Select(i => MacroAction.Of(MacroAction.Buy, i)).Append(MacroAction.Leave).ToList();
        return new(run, Surfaces.Shop, new ShopScreen { Entries = [] }, legal);
    }

    [Fact]
    public void APromptsDrawnAnswerIsCheckedWhereTheClonePlaysIt()
    {
        // A transformation: the screen predicts the card gone, the game turns A into B (refused) and BAD into C.
        var deck = Cards("A", "BAD");
        var toy = new Toy(path => path is [] ? Asked(deck) : new Afterstate(Fixtures.Floor1() with
        {
            Deck = path switch { ["pick:0"] => Cards("B", "BAD"), ["pick:1"] => Cards("A", "C"), _ => deck },
        }, Surfaces.Map, null, []));
        var prompt = new ChoiceDecision(new CardChoice
        {
            Options = Cards("A", "BAD"),
            Min = 1,
            Max = 1,
            Context = new ChoiceContext { Purpose = "deck_for_transformation", Cancelable = false, InCombat = false },
        }, () => new Observation { Run = Fixtures.Floor1() with { Deck = deck }, Screen = new TreasureScreen { Relics = [] } }, toy);
        Assert.All(Enumerable.Range(0, 100), seed => Assert.Equal([1],
            new MacroBrain(V, uniform: new Uniform(1, new Random(seed)), cards: Lock(0.2)).Choose(prompt).Picks));
    }

    [Fact]
    public void OnTheClonesSearchEachStateIsWeighedByTheLockAndOneThatBreaksItIsNotSearchedOn()
    {
        var toy = new Toy(CardShop);
        var root = toy.After([[]])[0];
        var shop = new MacroDecision(root.Surface!, root.Legal, () => new Observation { Run = root.Run, Screen = root.Screen! }, toy);
        // V's basket is B and C (15.9); locked, B is worth −∞ however it goes on, and C's best basket is C alone (12.9),
        // J's bonus not making up for its -1.2 (C and J: 11.7 e^0.2 = 14.3 > 12.9, so it does).
        Assert.Equal(15.9, new MacroBrain(V).Options(shop).Single(o => o.Action.ToString() == "buy:0").Value, 9);
        var options = new MacroBrain(V, cards: Lock(0.2)).Options(shop);
        Assert.Equal(double.NegativeInfinity, options.Single(o => o.Action.ToString() == "buy:0").Value);
        Assert.Equal(11.7 * Math.Exp(0.2), options.Single(o => o.Action.ToString() == "buy:1").Value, 9);
        Assert.Equal(MacroAction.Of(MacroAction.Buy, 1), new MacroBrain(V, cards: Lock(0.2)).Choose(shop).Action);
    }

    [Fact]
    public void AKeptCardNeverLeavesTheDeckByAnAnswer()
    {
        var keep = Tyranny();
        Assert.True(keep.Breaks(Cards("T", "A"), Cards("A")));
        Assert.True(keep.Breaks(Cards("T", "A"), Cards("C", "A")));
        Assert.False(keep.Breaks(Cards("T", "A"), [new CardView { Id = "T", Up = 1 }, new CardView { Id = "A" }]));
        Assert.False(keep.Breaks(Cards("T", "T"), Cards("T", "T", "A")));
        Assert.Equal(double.NegativeInfinity, keep.Worth(Cards("T", "BAD"), Cards("BAD"), 5));
        // V would remove T (worth -2) over BAD (-1); kept, the removal takes BAD, also when a draw picks T, and a prompt
        // that allows no pick takes none.
        var v = new Worth(new() { ["T"] = -2, ["BAD"] = -1 });
        var deck = Cards("T", "BAD");
        Assert.Equal([0], new MacroBrain(v).Choose(Removal(deck, "T", "BAD")).Picks);
        Assert.Equal([1], new MacroBrain(v, cards: keep).Choose(Removal(deck, "T", "BAD")).Picks);
        Assert.All(Enumerable.Range(0, 200), seed => Assert.Equal([1],
            new MacroBrain(v, uniform: new Uniform(1, new Random(seed)), cards: keep).Choose(Removal(deck, "T", "BAD")).Picks));
        var optional = new ChoiceDecision(new CardChoice
        {
            Options = Cards("T"),
            Min = 0,
            Max = 1,
            Context = new ChoiceContext { Purpose = "deck_for_removal", Cancelable = true, InCombat = false },
        }, () => new Observation { Run = Fixtures.Floor1() with { Deck = Cards("T") }, Screen = new TreasureScreen { Relics = [] } });
        Assert.Equal([0], new MacroBrain(v).Choose(optional).Picks);
        Assert.Empty(new MacroBrain(v, cards: keep).Choose(optional).Picks);
    }

    [Fact]
    public void AFavorOfTenTakesTheCardWhereverAnAnswerCan()
    {
        // V would take A (10 + 1) or even skip (10) over T (10 - 2): e^10 makes T worth some 176,000.
        var v = new Worth(new() { ["A"] = 1, ["T"] = -2 });
        Assert.Equal(MacroAction.Of(MacroAction.Card, 1), new MacroBrain(v).Choose(CardReward([], "T", "A")).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Card, 0), new MacroBrain(v, cards: Tyranny()).Choose(CardReward([], "T", "A")).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Card, 0), new MacroBrain(v, softmax: new Softmax(0.01, new Random(1)), cards: Tyranny())
            .Choose(CardReward([], "T", "A")).Action);
    }

    /// <summary>A card with an upgrade's preview.</summary>
    private static CardView Upgradable(string id) => new() { Id = id, Upgraded = new CardView { Id = id, Up = 1 } };

    /// <summary>A V of 10 plus 3 for an upgraded A, 1 for an upgraded T.</summary>
    private sealed class Upgrades : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => 10 + run.Deck.Sum(c => c.Up == null ? 0 : c.Id == "A" ? 3 : c.Id == "T" ? 1 : 0);
    }

    private static CardChoice Upgrade(int min, params CardView[] options) => new()
    {
        Options = options,
        Min = min,
        Max = 1,
        Context = new ChoiceContext { Purpose = "deck_for_upgrade", Cancelable = min == 0, InCombat = false },
    };

    private static ChoiceDecision Smith(CardChoice choice, IMacroClone? clone = null) =>
        new(choice, () => new Observation { Run = Fixtures.Floor1() with { Deck = choice.Options }, Screen = new TreasureScreen { Relics = [] } }, clone);

    /// <summary>A card prompt of these tests over <paramref name="deck"/>, as its clone shows it.</summary>
    private static Afterstate Asked(IReadOnlyList<CardView> deck) => new(Fixtures.Floor1() with { Deck = deck }, Surfaces.CardChoice, new TreasureScreen { Relics = [] }, []);

    [Fact]
    public void AnUpgradesPromptTakesACardToUpgradeFirstWhateverVSays()
    {
        var choice = Upgrade(1, Upgradable("A"), Upgradable("T"));
        Assert.Equal([0], new MacroBrain(new Upgrades()).Choose(Smith(choice)).Picks);
        Assert.Equal([1], new MacroBrain(new Upgrades(), cards: Tyranny()).Choose(Smith(choice)).Picks);
        Assert.Equal([1], CardLock.Firsts(Tyranny(), choice));
        // Only at an upgrade's prompt outside combat, and only a card not upgraded; elsewhere a removal's rule holds.
        Assert.Empty(CardLock.Firsts(Tyranny(), choice with { Context = choice.Context with { InCombat = true } }));
        Assert.Empty(CardLock.Firsts(Tyranny(), Upgrade(1, Upgradable("A"), new CardView { Id = "T", Up = 1 })));
        Assert.Empty(CardLock.Firsts(Lock(0.2), choice));
        // Even draws upgrade T, and a prompt that allows no pick may still take none.
        Assert.All(Enumerable.Range(0, 200), seed => Assert.Equal([1],
            new MacroBrain(new Upgrades(), uniform: new Uniform(1, new Random(seed)), cards: Tyranny()).Choose(Smith(choice)).Picks));
        var none = new Worth(new());
        Assert.Empty(new MacroBrain(none, cards: Tyranny()).Choose(Smith(Upgrade(0, Upgradable("A"), Upgradable("T")))).Picks);
    }

    [Fact]
    public void OnTheClonesSearchAnUpgradeIsValuedByTheCardToUpgradeFirst()
    {
        // A rest site whose smithing opens the prompt on the clone; the clone's search answers it with T alone.
        CardView[] deck = [Upgradable("A"), Upgradable("T")];
        var prompt = Upgrade(1, deck);
        Afterstate Upgraded(string pick) => new(Fixtures.Floor1() with
        {
            Deck = [.. deck.Select((c, i) => pick == $"pick:{i}" ? c.Upgraded! : c)],
        }, Surfaces.Map, null, []);
        var toy = new Toy(path => path switch
        {
            [] => new(Fixtures.Floor1() with { Deck = deck }, Surfaces.Rest, new RestScreen { Options = [new("SMITH", true)] }, Fixtures.Acts("rest:SMITH")),
            ["rest:SMITH"] => new(Fixtures.Floor1() with { Deck = deck }, Surfaces.CardChoice, new CardChoiceScreen { Choice = prompt }, []),
            ["rest:SMITH", var pick] => Upgraded(pick),
            _ => throw new InvalidOperationException("no more answers"),
        });
        var root = toy.After([[]])[0];
        var rest = new MacroDecision(root.Surface!, root.Legal, () => new Observation { Run = root.Run, Screen = root.Screen! }, toy);
        Assert.Equal(13, new MacroBrain(new Upgrades()).Options(rest).Single().Value, 9);
        Assert.Equal(11, new MacroBrain(new Upgrades(), cards: Tyranny()).Options(rest).Single().Value, 9);
        // The prompt itself answered on the clone: its local search weighs only answers that upgrade T.
        var smith = new Toy(path => path switch
        {
            [] => Asked(deck),
            [var pick] => Upgraded(pick),
            _ => throw new InvalidOperationException("one pick a path"),
        });
        Assert.Equal([0], new MacroBrain(new Upgrades()).Choose(Smith(prompt, smith)).Picks);
        Assert.Equal([1], new MacroBrain(new Upgrades(), cards: Tyranny()).Choose(Smith(prompt, smith)).Picks);
    }
}
