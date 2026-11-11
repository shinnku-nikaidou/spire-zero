using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class CloneSearchTests
{
    /// <summary>A V that is the sum of what each card and relic is worth.</summary>
    private sealed class Worth(Dictionary<string, double> worth) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => run.Deck.Sum(c => worth.GetValueOrDefault(c.Id)) + run.Relics.Sum(r => worth.GetValueOrDefault(r.Id));
    }

    private static readonly Worth V = new(new() { ["X"] = 1, ["Y"] = 1, ["Z"] = 1.5, ["A"] = 1, ["B"] = 3, ["C"] = 2.9, ["BAD"] = -2 });

    /// <summary>A node's clone that plays each path in a toy game, and counts the probes.</summary>
    private sealed class Toy(Func<IReadOnlyList<string>, Afterstate> play) : IMacroClone
    {
        public int Probes { get; private set; }

        public IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<MacroAction>> paths)
        {
            Probes += paths.Count;
            return [.. paths.Select(p => play([.. p.Select(a => a.ToString())]))];
        }
    }

    private static readonly RunView Start = Fixtures.Floor1() with { Gold = 10, Deck = [], Relics = [] };

    private static Afterstate Exit(RunView run) => new(run, Surfaces.Map, null, []);

    private static IReadOnlyList<MacroAction> Acts(params string[] actions) => Fixtures.Acts(actions);

    /// <summary>Root options, each a path of one action.</summary>
    private static IReadOnlyList<IReadOnlyList<MacroAction>> Paths(params string[] actions) => [.. actions.Select(a => Acts(a))];

    private static string Chosen(MacroDecision decision) => new MacroBrain(V).Choose(decision).Action.ToString();

    private static MacroDecision Root(Toy toy) => Root(toy, toy.After([[]])[0]);

    private static MacroDecision Root(IMacroClone clone, Afterstate root) =>
        new(root.Surface!, root.Legal, () => new Observation { Run = root.Run, Screen = root.Screen! }, clone);

    /// <summary>A shop of three relics for 10 gold: X and Y cost 5, Z costs 10.</summary>
    private static Afterstate Shop(IReadOnlyList<string> path)
    {
        (string Id, int Cost)[] stock = [("X", 5), ("Y", 5), ("Z", 10)];
        var run = Start;
        foreach (var action in path)
        {
            if (action == "leave") return Exit(run);
            var (id, cost) = stock[int.Parse(action["buy:".Length..])];
            run = run with { Gold = run.Gold - cost, Relics = [.. run.Relics, new RelicView { Id = id }] };
        }
        var legal = Enumerable.Range(0, stock.Length).Where(i => stock[i].Cost <= run.Gold && run.Relics.All(r => r.Id != stock[i].Id))
            .Select(i => MacroAction.Of(MacroAction.Buy, i)).Append(MacroAction.Leave).ToList();
        return new(run, Surfaces.Shop, new ShopScreen { Entries = [] }, legal);
    }

    [Fact]
    public void AShopIsWorthItsBestBasketNotItsBestBuy()
    {
        // Z alone is the best buy, but X then Y is worth more, and only searching on finds it.
        var toy = new Toy(Shop);
        var l0 = new MacroBrain(V);
        var options = l0.Options(Root(toy));
        Assert.Equal("buy:0", Chosen(Root(toy)));
        Assert.Equal(2, options.Single(o => o.Action.ToString() == "buy:0").Value);
        Assert.Equal(1.5, options.Single(o => o.Action.ToString() == "buy:2").Value);
        Assert.Equal(0, options.Single(o => o.Action.ToString() == "leave").Value);
    }

    /// <summary>An event whose first page shows nothing: entering leads to a page that gives relic B; leaving ends it.</summary>
    private static Afterstate Event(IReadOnlyList<string> path)
    {
        static EventScreen Page(string page) => new() { Id = "TOY", Page = page, Options = [], Finished = false };
        return path switch
        {
            [] => new(Start, Surfaces.Event, Page("first"), Acts("event:0", "event:1")),
            ["event:0", ..] => Exit(Start),
            ["event:1"] => new(Start, Surfaces.Event, Page("second"), Acts("event:0")),
            ["event:1", "event:0", ..] => Exit(Start with { Relics = [new RelicView { Id = "B" }] }),
            _ => throw new InvalidOperationException(string.Join(" ", path)),
        };
    }

    [Fact]
    public void AnEventOptionIsWorthWhatItsNextPageGives()
    {
        // Leaving is listed first: from the first page alone the two would tie and leaving would win.
        Assert.Equal("event:1", Chosen(Root(new Toy(Event))));
    }

    /// <summary>A combat leaf that values every combat at the same <paramref name="worth"/>.</summary>
    private sealed class FlatLeaf(double worth) : ICombatLeaf
    {
        public LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history) => new(worth, 0, 0);
    }

    [Fact]
    public void AFightAndTheRunsEndAreNotValuedByV()
    {
        // A fight with relic B in hand (V alone would count it as won and B kept), a death and a won run, both at HP 0 as
        // the game leaves a run it ends: the fight is worth what the combat leaf says, the death 0, the win 1.
        var toy = new Toy(path => path switch
        {
            [] => new(Start, Surfaces.Event, new EventScreen { Id = "TOY", Options = [], Finished = false }, Acts("event:0", "event:1", "event:2")),
            ["event:0"] => new(Start with { Relics = [new RelicView { Id = "B" }] }, Surfaces.Combat, new CombatScreen { Combat = Fixtures.Slugs() }, []),
            ["event:1"] => new(Start with { Hp = 0 }, null, null, [], Won: false),
            ["event:2"] => new(Start with { Hp = 0 }, null, null, [], Won: true),
            _ => throw new InvalidOperationException(string.Join(" ", path)),
        });
        var found = new CloneSearch(V, toy, fights: new FlatLeaf(0.25)).Search(toy.After([[]])[0], Paths("event:0", "event:1", "event:2"));
        Assert.Equal([0.25, 0, 1], found.Select(f => f.Value));
        Assert.Throws<InvalidOperationException>(() => new CloneSearch(V, toy).Search(toy.After([[]])[0], Paths("event:0")));
    }

    [Fact]
    public void AMoveOnTheMapIsNotPlayed()
    {
        var run = Fixtures.Floor1();
        var next = run.Map.Points.First(p => p.Row == 1);
        var map = new Afterstate(run, Surfaces.Map, new MapScreen { Travelable = [new(next.Col, next.Row)] }, [MacroAction.Of(MacroAction.Map, next.Col, next.Row)]);
        var toy = new Toy(path => throw new InvalidOperationException($"played {string.Join(" ", path)}"));
        Assert.Equal($"map:{next.Col},{next.Row}", Chosen(Root(toy, map)));
        Assert.Equal(0, toy.Probes);
    }

    [Fact]
    public void APromptOfTwoPicksFindsTheBestPair()
    {
        var choice = new CardChoice
        {
            Options = [.. CardIds.Select(id => new CardView { Id = id })],
            Min = 2,
            Max = 2,
            Context = new ChoiceContext { Purpose = "choose_a_card_screen", Cancelable = false, InCombat = false },
        };
        var toy = new Toy(path => path switch
        {
            [var pick] => Exit(Start with { Deck = [.. pick["pick:".Length..].Split(',').Select(i => choice.Options[int.Parse(i)])] }),
            _ => throw new InvalidOperationException(string.Join(" ", path)),
        });
        var prompt = new ChoiceDecision(choice, () => new Observation { Run = Start, Screen = new CardChoiceScreen { Choice = choice } }, toy);
        Assert.Equal([1, 3], new MacroBrain(V).Choose(prompt).Picks);
    }

    private static readonly CardView[] Offered = [new() { Id = "A" }, new() { Id = "BAD" }];
    private static readonly string[] CardIds = ["A", "B", "BAD", "C"];

    /// <summary>
    /// A rewards screen: a card reward (reward:0, A or BAD) and relic Y (reward:1). Skipping the card reward returns to
    /// the screen with the card reward still on it, as in the game. Leaving goes on to a state V likes less (relic BAD).
    /// </summary>
    private static Afterstate Rewards(List<string> path)
    {
        var run = Start;
        bool card = true, relic = true;
        for (int i = 0; i < path.Count; i++)
        {
            switch (path[i])
            {
                case "rewards_done":
                    return Exit(run with { Relics = [.. run.Relics, new RelicView { Id = "BAD" }] });
                case "reward:1":
                    relic = false;
                    run = run with { Relics = [.. run.Relics, new RelicView { Id = "Y" }] };
                    break;
                case "reward:0" when ++i == path.Count:
                    return new(run, Surfaces.CardReward, new CardRewardScreen { Cards = Offered, Alternatives = ["Skip"] }, Acts("card:0", "card:1", "card_alt:Skip"));
                case "reward:0" when path[i] != "card_alt:Skip":
                    card = false;
                    run = run with { Deck = [.. run.Deck, Offered[int.Parse(path[i]["card:".Length..])]] };
                    break;
            }
        }
        var rewards = new List<RewardView>();
        if (card) rewards.Add(new() { Index = 0, Kind = "card", Cards = Offered });
        if (relic) rewards.Add(new() { Index = 1, Kind = "relic", Id = "Y" });
        return new(run, Surfaces.Rewards, new RewardsScreen { Rewards = rewards }, [.. rewards.Select(r => MacroAction.Of(MacroAction.Reward, r.Index)), MacroAction.RewardsDone]);
    }

    [Fact]
    public void ASkippedCardRewardIsNotOpenedAgain()
    {
        // From the card reward, skipping searches on through the rewards screen: relic Y, then the card reward again,
        // whose picks lead to states seen a level earlier, so it is worth V's value of it (1, before leaving's BAD)
        // and beats taking A (0 in the end). Opening it again from the rewards screen, which knows the true end, wins
        // again. Each decision on the node's clone is played, as a run would, until the node is left.
        var l0 = new MacroBrain(V);
        var path = new List<string>();
        while (Rewards(path).Surface != Surfaces.Map)
        {
            Assert.True(path.Count < 8, $"no way out: {string.Join(" ", path)}");
            var at = path.ToList();
            path.Add(l0.Choose(Root(new Toy(p => Rewards([.. at, .. p])))).Action.ToString());
        }
        Assert.Equal(["reward:0", "card:0", "reward:1", "rewards_done"], path);
    }

    [Fact]
    public void AStateReachedTwiceIsSearchedOnce()
    {
        // Two ways to the same page: its one answer is probed once.
        var toy = new Toy(path => path switch
        {
            [] => new(Start, Surfaces.Event, new EventScreen { Id = "TOY", Options = [], Finished = false }, Acts("event:0", "event:1")),
            [_] => new(Start, Surfaces.Event, new EventScreen { Id = "TOY", Page = "same", Options = [], Finished = false }, Acts("proceed")),
            [_, "proceed"] => Exit(Start),
            _ => throw new InvalidOperationException(string.Join(" ", path)),
        });
        var search = new CloneSearch(V, toy);
        search.Search(toy.After([[]])[0], Paths("event:0", "event:1"));
        Assert.Equal(3, search.Probes);
    }

    /// <summary>A V that is the sum of what each potion held is worth.</summary>
    private sealed class Belt(Dictionary<string, double> worth) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => run.Potions.Sum(p => p == null ? 0 : worth.GetValueOrDefault(p));
    }

    /// <summary>
    /// An event with a belt full of Q: its first option gives potion P on a rewards screen, which takes it only into a free
    /// slot, and a potion can be discarded there to make one; its second leaves.
    /// </summary>
    private static Afterstate Courier(IReadOnlyList<string> path)
    {
        var belt = new string?[] { "Q", "Q", "Q" };
        RunView Run() => Start with { Potions = [.. belt] };
        if (path.Count == 0) return new(Run(), Surfaces.Event, new EventScreen { Id = "TOY", Options = [], Finished = false }, Acts("event:0", "event:1"));
        if (path[0] == "event:1") return Exit(Run());
        foreach (var action in path.Skip(1))
        {
            if (action == "rewards_done") return Exit(Run());
            if (action == "reward:0")
            {
                belt[Array.IndexOf(belt, null)] = "P";
                return Exit(Run());
            }
            belt[int.Parse(action["discard_potion:".Length..])] = null;
        }
        var legal = new List<string>();
        if (belt.Contains(null)) legal.Add("reward:0");
        legal.Add("rewards_done");
        legal.AddRange(Enumerable.Range(0, belt.Length).Where(i => belt[i] != null).Select(i => $"discard_potion:{i}"));
        return new(Run(), Surfaces.Rewards, new RewardsScreen { Rewards = [new RewardView { Index = 0, Kind = "potion", Id = "P" }] }, Acts([.. legal]));
    }

    [Fact]
    public void AnEventsPotionIsWorthTheRoomMadeForItWhenTheBeltIsFull()
    {
        // The rewards screen can take P only after a discard: below the root the search makes the room, as the decision
        // there would, so the option is worth Q + Q + P, not the belt it started with.
        var toy = new Toy(Courier);
        var l0 = new MacroBrain(new Belt(new() { ["Q"] = 1, ["P"] = 2 }));
        var options = l0.Options(Root(toy));
        Assert.Equal(4, options.Single(o => o.Action.ToString() == "event:0").Value);
        Assert.Equal(3, options.Single(o => o.Action.ToString() == "event:1").Value);
    }
}
