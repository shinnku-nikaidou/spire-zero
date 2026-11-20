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
    private static CardLock Lock(double by) => new(new HashSet<string> { "K", "J" }, by, new HashSet<string> { "B" });

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
        var toy = new Toy(path => new Afterstate(Fixtures.Floor1() with
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
}
