using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class CombatExplorationTests
{
    private static readonly CombatAction Play = CombatAction.Play(0, 1);

    /// <summary>A search that always prefers playing the card a little, and plans to end the turn after it.</summary>
    private static readonly CombatRoute Searched = new("test", (_, _, _, _) => new(Play, [CombatAction.EndTurn],
        [new(Play.ToString(), 0.5, 0, 8), new(CombatAction.EndTurn.ToString(), 0.49, 0, 8)]));

    private sealed class Flat : IRunValue
    {
        public string Name => "flat";

        public double Value(RunView run) => 0;
    }

    private static CombatDecision Decision() => new([Play, CombatAction.EndTurn], Fixtures.Slugs);

    [Fact]
    public void AnExploringRouteDrawsByTheSearchsValuesAndTheLogMarksIt()
    {
        var macro = new MacroBrain(new Flat());
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
