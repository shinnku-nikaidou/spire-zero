using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

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

        public IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<string>> paths)
        {
            Probes += paths.Count;
            return paths.Select(play).ToList();
        }
    }

    private static readonly RunView Start = Fixtures.Floor1() with { Gold = 10, Deck = [], Relics = [] };

    private static Afterstate Exit(RunView run) => new(run, Surfaces.Map, null, []);

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
            .Select(i => $"buy:{i}").Append("leave").ToList();
        return new(run, Surfaces.Shop, new ShopScreen { Entries = [] }, legal);
    }

    [Fact]
    public void AShopIsWorthItsBestBasketNotItsBestBuy()
    {
        // Z alone is the best buy, but X then Y is worth more, and only searching on finds it.
        var toy = new Toy(Shop);
        var l0 = new MacroBrain(V);
        var options = l0.Options(Root(toy));
        Assert.Equal("buy:0", l0.Choose(Root(toy)));
        Assert.Equal(2, options.Single(o => o.Action == "buy:0").Value);
        Assert.Equal(1.5, options.Single(o => o.Action == "buy:2").Value);
        Assert.Equal(0, options.Single(o => o.Action == "leave").Value);
    }

    /// <summary>An event whose first page shows nothing: entering leads to a page that gives relic B; leaving ends it.</summary>
    private static Afterstate Event(IReadOnlyList<string> path)
    {
        EventScreen Page(string page) => new() { Id = "TOY", Page = page, Options = [], Finished = false };
        return path switch
        {
            [] => new(Start, Surfaces.Event, Page("first"), ["event:0", "event:1"]),
            ["event:0", ..] => Exit(Start),
            ["event:1"] => new(Start, Surfaces.Event, Page("second"), ["event:0"]),
            ["event:1", "event:0", ..] => Exit(Start with { Relics = [new RelicView { Id = "B" }] }),
            _ => throw new InvalidOperationException(string.Join(" ", path)),
        };
    }

    [Fact]
    public void AnEventOptionIsWorthWhatItsNextPageGives()
    {
        // Leaving is listed first: from the first page alone the two would tie and leaving would win.
        Assert.Equal("event:1", new MacroBrain(V).Choose(Root(new Toy(Event))));
    }

    [Fact]
    public void AMoveOnTheMapIsNotPlayed()
    {
        var run = Fixtures.Floor1();
        var next = run.Map.Points.First(p => p.Row == 1);
        var map = new Afterstate(run, Surfaces.Map, new MapScreen { Travelable = [new(next.Col, next.Row)] }, [$"map:{next.Col},{next.Row}"]);
        var toy = new Toy(path => throw new InvalidOperationException($"played {string.Join(" ", path)}"));
        Assert.Equal($"map:{next.Col},{next.Row}", new MacroBrain(V).Choose(Root(toy, map)));
        Assert.Equal(0, toy.Probes);
    }

    [Fact]
    public void APromptOfTwoPicksFindsTheBestPair()
    {
        var choice = new CardChoice
        {
            Options = new[] { "A", "B", "BAD", "C" }.Select(id => new CardView { Id = id }).ToList(),
            Min = 2,
            Max = 2,
            Context = new ChoiceContext { Purpose = "choose_a_card_screen", Cancelable = false, InCombat = false },
        };
        var toy = new Toy(path => path switch
        {
            [var pick] => Exit(Start with { Deck = pick["pick:".Length..].Split(',').Select(i => choice.Options[int.Parse(i)]).ToList() }),
            _ => throw new InvalidOperationException(string.Join(" ", path)),
        });
        var prompt = new ChoiceDecision(choice, () => new Observation { Run = Start, Screen = new CardChoiceScreen { Choice = choice } }, toy);
        Assert.Equal([1, 3], new MacroBrain(V).Choose(prompt));
    }

    [Fact]
    public void AStateReachedTwiceIsSearchedOnce()
    {
        // Two ways to the same page: its one answer is probed once.
        var toy = new Toy(path => path switch
        {
            [] => new(Start, Surfaces.Event, new EventScreen { Id = "TOY", Options = [], Finished = false }, ["event:0", "event:1"]),
            [_] => new(Start, Surfaces.Event, new EventScreen { Id = "TOY", Page = "same", Options = [], Finished = false }, ["proceed"]),
            [_, "proceed"] => Exit(Start),
            _ => throw new InvalidOperationException(string.Join(" ", path)),
        });
        var search = new CloneSearch(V, toy);
        search.Search(toy.After([[]])[0], [["event:0"], ["event:1"]]);
        Assert.Equal(3, search.Probes);
    }
}
