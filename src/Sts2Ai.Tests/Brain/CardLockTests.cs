using Sts2Ai.Brain;
using Sts2Ai.Brain.Macro;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

/// <summary>A run locked to card families (<see cref="CardLock"/>; plans/2026-11-20-1231-silent-shiv-poison.md, 2.2).</summary>
public class CardLockTests
{
    /// <summary>A V that is the sum of what each card is worth.</summary>
    private sealed class Worth(Dictionary<string, double> worth) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => run.Deck.Sum(c => worth.GetValueOrDefault(c.Id));
    }

    private static readonly Worth V = new(new() { ["A"] = 1, ["B"] = 3, ["C"] = 2.9, ["BAD"] = -2, ["WORSE"] = -3 });

    /// <summary>WORSE taken, B refused.</summary>
    private static readonly CardLock Lock = new(new HashSet<string> { "WORSE" }, new HashSet<string> { "B" });

    private static List<CardView> Cards(params string[] ids) => [.. ids.Select(id => new CardView { Id = id })];

    private static MacroDecision Rewards(params string[] cards) => new(Surfaces.Rewards, Fixtures.Acts("reward:0", "rewards_done"), () => new Observation
    {
        Run = Fixtures.Floor1(),
        Screen = new RewardsScreen { Rewards = [new RewardView { Index = 0, Kind = "card", Cards = Cards(cards) }] },
    });

    private static MacroDecision CardReward(params string[] cards) => new(Surfaces.CardReward,
        [.. cards.Select((_, i) => MacroAction.Of(MacroAction.Card, i)), new MacroAction(MacroAction.CardAlt, "Skip")],
        () => new Observation { Run = Fixtures.Floor1(), Screen = new CardRewardScreen { Cards = Cards(cards), Alternatives = ["Skip"] } });

    private static ChoiceDecision Removal(int min, IReadOnlyList<CardView> deck, params string[] offered) => new(new CardChoice
    {
        Options = Cards(offered),
        Min = min,
        Max = 1,
        Context = new ChoiceContext { Purpose = "deck_for_removal", Cancelable = min == 0, InCombat = false },
    }, () => new Observation { Run = Fixtures.Floor1() with { Deck = deck }, Screen = new TreasureScreen { Relics = [] } });

    [Fact]
    public void ADeckBreaksItWithFewerCardsToTakeOrMoreToRefuse()
    {
        Assert.False(Lock.Breaks(Cards("WORSE", "A"), Cards("WORSE", "A", "WORSE", "C")));
        Assert.True(Lock.Breaks(Cards("WORSE", "A"), Cards("A")));
        Assert.True(Lock.Breaks(Cards("A"), Cards("A", "B")));
        Assert.False(Lock.Breaks(Cards("B", "A"), Cards("A")));
    }

    [Fact]
    public void ACardRewardOfferingACardToTakeIsOpenedAndTheCardTakenWhateverVOrTheDrawsSay()
    {
        // V would leave the reward (WORSE and BAD are worth less than nothing) and, opened, take A.
        Assert.Equal(MacroAction.RewardsDone, new MacroBrain(V).Choose(Rewards("WORSE", "BAD")).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Card, 2), new MacroBrain(V).Choose(CardReward("WORSE", "BAD", "A")).Action);
        for (int seed = 0; seed < 200; seed++)
        {
            var l0 = new MacroBrain(V, uniform: new Uniform(1, new Random(seed)), cards: Lock);
            var opened = l0.Choose(Rewards("WORSE", "BAD"));
            Assert.Equal(MacroAction.Of(MacroAction.Reward, 0), opened.Action);
            Assert.True(opened.Explored);
            Assert.Equal(MacroAction.Of(MacroAction.Card, 0), l0.Choose(CardReward("WORSE", "BAD", "A")).Action);
        }
        // With no card to take offered, V and the draws as ever.
        Assert.Equal(MacroAction.Of(MacroAction.Card, 1), new MacroBrain(V, cards: Lock).Choose(CardReward("A", "C")).Action);
    }

    [Fact]
    public void ACardToRefuseIsNeitherTakenNorBought()
    {
        // V would take B; refused, it takes C, the next best, also when a draw picks B.
        Assert.Equal(MacroAction.Of(MacroAction.Card, 0), new MacroBrain(V).Choose(CardReward("B", "C")).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Card, 1), new MacroBrain(V, cards: Lock).Choose(CardReward("B", "C")).Action);
        Assert.All(Enumerable.Range(0, 200), seed => Assert.NotEqual(MacroAction.Of(MacroAction.Card, 0),
            new MacroBrain(V, uniform: new Uniform(1, new Random(seed)), cards: Lock).Choose(CardReward("B", "C")).Action));

        var shop = new MacroDecision(Surfaces.Shop, Fixtures.Acts("buy:0", "buy:1", "leave"), () => new Observation
        {
            Run = Fixtures.Floor1() with { Gold = 500 },
            Screen = new ShopScreen
            {
                Entries = [new() { Slot = 0, Kind = "card", Card = new() { Id = "B" }, Cost = 1 }, new() { Slot = 1, Kind = "card", Card = new() { Id = "C" }, Cost = 1 }],
            },
        });
        Assert.Equal(MacroAction.Of(MacroAction.Buy, 0), new MacroBrain(V).Choose(shop).Action);
        Assert.Equal(MacroAction.Of(MacroAction.Buy, 1), new MacroBrain(V, cards: Lock).Choose(shop).Action);
    }

    /// <summary>A node's clone that plays each path in a toy game.</summary>
    private sealed class Toy(Func<IReadOnlyList<string>, Afterstate> play) : IMacroClone
    {
        public IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<MacroAction>> paths) =>
            [.. paths.Select(p => play([.. p.Select(a => a.ToString())]))];
    }

    /// <summary>A shop of two cards for 10 gold, 5 each: B and C.</summary>
    private static Afterstate CardShop(IReadOnlyList<string> path)
    {
        string[] stock = ["B", "C"];
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
    public void OnTheClonesSearchAStateThatBreaksItIsWorthNothingAndNotSearchedOn()
    {
        var toy = new Toy(CardShop);
        var root = toy.After([[]])[0];
        var shop = new MacroDecision(root.Surface!, root.Legal, () => new Observation { Run = root.Run, Screen = root.Screen! }, toy);
        // V's basket is both cards (B first); locked, C alone, and buying B is worth −∞ however it goes on.
        Assert.Equal(5.9, new MacroBrain(V).Options(shop).Single(o => o.Action.ToString() == "buy:0").Value, 9);
        var options = new MacroBrain(V, cards: Lock).Options(shop);
        Assert.Equal(double.NegativeInfinity, options.Single(o => o.Action.ToString() == "buy:0").Value);
        Assert.Equal(2.9, options.Single(o => o.Action.ToString() == "buy:1").Value, 9);
        Assert.Equal(MacroAction.Of(MacroAction.Buy, 1), new MacroBrain(V, cards: Lock).Choose(shop).Action);
    }

    [Fact]
    public void ARemovalLeavesTheCardsToTakeUnlessNothingElseCanGo()
    {
        var deck = Cards("WORSE", "BAD", "A");
        // V would remove WORSE, the worst; locked, BAD, also when the draws pick.
        Assert.Equal([0], new MacroBrain(V).Choose(Removal(1, deck, "WORSE", "BAD")).Picks);
        Assert.Equal([1], new MacroBrain(V, cards: Lock).Choose(Removal(1, deck, "WORSE", "BAD")).Picks);
        Assert.All(Enumerable.Range(0, 200), seed => Assert.Equal([1],
            new MacroBrain(V, uniform: new Uniform(1, new Random(seed)), cards: Lock).Choose(Removal(1, deck, "WORSE", "BAD")).Picks));
        // A removal the game asks for with only WORSE to pick removes it.
        Assert.Equal([0], new MacroBrain(V, cards: Lock).Choose(Removal(1, deck, "WORSE")).Picks);
    }
}
