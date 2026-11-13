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

    /// <summary>The planner with its turns searched in <paramref name="threads"/> threads. No plan here is rooted at a
    /// card prompt, so it has no planner on the node's clone to hand one to.</summary>
    private static EnginePlanner Planner(int threads) => new(new ToyLeaf(), threads, null!);

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
        var refused = Assert.Throws<EngineException>(() => Planner(4).Plan(new(Encoding.UTF8.GetBytes("refuse"), Around), Small, []));
        Assert.Contains("takes no combat called refuse", refused.Message);
    }

    [Fact]
    public void APlanFreesEveryWorldItMade()
    {
        long before = engine.Worlds();
        Planner(4).Plan(Live, Small, []);
        Assert.Equal(before, engine.Worlds());
    }
}
