using System.Text;
using Sts2Ai.Brain;
using Sts2Ai.Engine;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Engine;

/// <summary>The SL planner on the engine's worlds, here the stand-in's: its "LONG" combat is won by the strike from
/// round 3 on, and every action before costs 1 HP, so the best plan ends two turns and strikes.</summary>
[Collection(EngineTests.Name)]
public sealed class EnginePlannerTests(FakeEngine engine)
{
    private static readonly RunView Around = new()
    {
        Character = "SILENT", Ascension = 0, Act = 1, ActId = "HIVE", Floor = 3, Hp = 70, MaxHp = 70, Gold = 99,
        Deck = [], Relics = [], Potions = [null],
        Map = new MapView { Points = [], Visited = [], Boss = "QUEEN_BOSS" },
    };

    private static readonly CombatExport Live = new(Encoding.UTF8.GetBytes("LONG"), Around);

    private static readonly Budget Small = new(NodesPerWorld: 40, Beam: 4);

    /// <summary>The planner with its turns searched in <paramref name="threads"/> threads.</summary>
    private static EnginePlanner Planner(int threads) => new(new ToyLeaf(), threads);

    private static string Text(SlPlan plan) => ProtocolJson.Serialize(plan);

    [Fact]
    public void ThePlanIsTheOneTheSamePlannerMakesOnOneWorld()
    {
        using var world = EngineWorld.Import(Live.Bytes, Live.Run);
        var alone = new SlPlanner(new ToyLeaf()).Plan(world, Small);
        Assert.Equal((CombatStatus.Won, CombatAction.EndTurn), (alone.End, alone.Result.First));
        Assert.Equal([CombatAction.EndTurn, CombatAction.Play(7)], alone.Result.Plan);
        Assert.Equal(Text(alone), Text(Planner(1).Plan(Live, Small, [])));
    }

    [Fact]
    public void ThePlanDoesNotDependOnHowManyThreadsSearch()
    {
        string one = Text(Planner(1).Plan(Live, Small, []));
        for (int again = 0; again < 5; again++) Assert.Equal(one, Text(Planner(8).Plan(Live, Small, [])));
    }

    [Fact]
    public void AFailureInAThreadIsThePlansFailure()
    {
        var refused = Assert.Throws<EngineException>(() => Planner(4).Plan(new CombatExport(Encoding.UTF8.GetBytes("refuse"), Around), Small, []));
        Assert.Contains("takes no combat called refuse", refused.Message);
    }

    [Fact]
    public void APlanFreesEveryWorldItMade()
    {
        long before = engine.Worlds();
        Planner(4).Plan(Live, Small, []);
        Assert.Equal(before, engine.Worlds());
    }

    private static readonly CardChoice Asked = new()
    {
        Options = [new CardView { Id = "A" }, new CardView { Id = "B" }],
        Min = 1,
        Max = 1,
        Context = new() { Purpose = "hand", Cancelable = false, InCombat = true },
    };

    /// <summary>
    /// A node's clone that stands at a card prompt, as a planner's crossing asks it: the first option leads to the
    /// stand-in's "LONG" combat; the second raises another prompt, whose two answers lead to "LONG" and to a combat the
    /// strike wins at once. <paramref name="asked"/> gets every path played.
    /// </summary>
    private static PromptRoot.Crossing Clone(List<string> asked) => new(paths => [.. paths.Select(path =>
    {
        string text = string.Join(' ', path);
        lock (asked) asked.Add(text);
        var prompt = new PromptStep(CombatStatus.InProgress, Fixtures.Slugs() with { Prompt = Asked }, Around, HiddenKey: (ulong)path.Count + 5);
        PromptStep Combat(string encounter) => new(CombatStatus.InProgress, Fixtures.Slugs(), Around, Export: Convert.ToBase64String(Encoding.UTF8.GetBytes(encounter)));
        return text switch
        {
            "" or "pick:1" => prompt,
            "pick:0" or "pick:1 pick:0" => Combat("LONG"),
            "pick:1 pick:1" => Combat("NIBBITS_WEAK"),
            _ => throw new InvalidOperationException($"the clone was asked for {text}"),
        };
    })]);

    [Fact]
    public void APlanRootedAtACardPromptCrossesItOnTheCloneAndGoesOnInTheEngine()
    {
        var asked = new List<string>();
        var crossing = Clone(asked);
        var plan = Planner(4).Plan(crossing, Small, []);
        // The second option and then the second again reach the combat that is won at once.
        Assert.Equal((CombatStatus.Won, CombatAction.Pick([1])), (plan.End, plan.Result.First));
        Assert.Equal([CombatAction.Pick([1]), CombatAction.Play(7)], plan.Result.Plan);
        // Each path was played on the clone once, however many states and threads came to it.
        Assert.Equal(["", "pick:0", "pick:1", "pick:1 pick:0", "pick:1 pick:1"], asked.Order(StringComparer.Ordinal));
        Assert.Equal(5, crossing.Crossed);

        // The same plan as the planner makes alone on one such world, and on one thread.
        var again = new List<string>();
        var second = Clone(again);
        using var world = new PromptRoot(second);
        Assert.Equal(Text(new SlPlanner(new ToyLeaf()).Plan(world, Small)), Text(plan));
        var third = Clone([]);
        Assert.Equal(Text(plan), Text(Planner(1).Plan(third, Small, [])));
    }

    [Fact]
    public void AnswersCrossedAheadAreNotAskedAgainAndEveryEngineWorldIsFreed()
    {
        long before = engine.Worlds();
        var asked = new List<string>();
        var crossing = Clone(asked);
        crossing.Ahead([[], [CombatAction.Pick([0])], [CombatAction.Pick([1])]]);
        Assert.Equal(3, asked.Count);
        // No world of the engine's is made until a search stands at an answer.
        Assert.Equal(before, engine.Worlds());
        Planner(4).Plan(crossing, Small, []);
        Assert.Equal(5, asked.Count);
        using (var world = new PromptRoot(crossing))
        {
            Assert.Throws<InvalidOperationException>(() => world.Execute(CombatAction.EndTurn));
            Assert.Throws<InvalidOperationException>(() => world.Execute(CombatAction.Pick([0, 1])));
        }
        Assert.Equal(before, engine.Worlds());
    }

    [Fact]
    public void AnAnswersEngineWorldIsFreedOnceTheSearchHasLeftIt()
    {
        long before = engine.Worlds();
        var many = Asked with { Options = [.. Enumerable.Range(0, 12).Select(i => new CardView { Id = $"C{i}" })] };
        var crossing = new PromptRoot.Crossing(paths => [.. paths.Select(path => path.Count == 0
            ? new PromptStep(CombatStatus.InProgress, Fixtures.Slugs() with { Prompt = many }, Around, HiddenKey: 5)
            : new PromptStep(CombatStatus.InProgress, Fixtures.Slugs(), Around, Export: Convert.ToBase64String(Encoding.UTF8.GetBytes("LONG"))))]);
        using var world = new PromptRoot(crossing);
        var found = new SlPlanner(new ToyLeaf()).SearchTurn(world, [], 200, 4);
        Assert.Equal(13, crossing.Crossed);
        Assert.NotEmpty(found.Ends);
        // Every answer was searched, and only the world where the search stopped is left.
        Assert.Equal(before + 1, engine.Worlds());
    }

    [Fact]
    public void APromptsAnswersAreCrossedAFewAtATime()
    {
        var batches = new List<int>();
        var crossing = new PromptRoot.Crossing(paths =>
        {
            batches.Add(paths.Count);
            return [.. paths.Select(_ => new PromptStep(CombatStatus.Lost, Fixtures.Slugs(), Around))];
        });
        var answers = Enumerable.Range(0, 2 * PromptRoot.Crossing.Batch + 5).Select(i => (IReadOnlyList<CombatAction>)[CombatAction.Pick([i])]).ToList();
        crossing.Ahead(answers);
        crossing.Ahead(answers);
        Assert.Equal([PromptRoot.Crossing.Batch, PromptRoot.Crossing.Batch, 5], batches);
        Assert.Equal(answers.Count, crossing.Crossed);
    }
}
