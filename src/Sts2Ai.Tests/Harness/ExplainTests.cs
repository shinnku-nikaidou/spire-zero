using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Harness;

/// <summary>The options a macro answer carries to a host that shows them (docs: harness §5): V's value of each, and on the map each one's route.</summary>
public class ExplainTests
{
    /// <summary>A V that is the sum of what each relic is worth, less a little per map point visited, so routes differ.</summary>
    private sealed class Worth(Dictionary<string, double> worth) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => run.Relics.Sum(r => worth.GetValueOrDefault(r.Id)) - 0.001 * run.Map.Visited.Sum(c => c.Col);
    }

    private static readonly Worth V = new(new() { ["A"] = 0.1, ["B"] = 0.3, ["C"] = 0.2 });

    private sealed class EndTurns : ICombatPolicy
    {
        public CombatAction Choose(CombatDecision decision) => CombatAction.EndTurn;
    }

    private static Dispatcher Make(bool explain)
    {
        var l0 = new MacroBrain(V);
        return new Dispatcher("run-1", PlayEnvironment.NoSL, CombatRoute.Of("test", new EndTurns()), new("p0", l0), new("l0", l0), new("l0", l0),
            explain: explain);
    }

    [Fact]
    public void AMacroAnswerCarriesEachOptionsValueTheChosenFirst()
    {
        var treasure = new MacroDecision(Surfaces.Treasure, Fixtures.Acts("relic:0", "relic:1", "relic:2"),
            () => new Observation { Run = Fixtures.Floor1(), Screen = new TreasureScreen { Relics = ["A", "B", "C"] } });
        var (answer, note) = Make(explain: false).Decide(treasure);

        Assert.Equal("relic:1", answer.Action.ToString());
        var candidates = note.Candidates!;
        Assert.Equal(["relic:1", "relic:2", "relic:0"], candidates.Select(c => c.Action));
        double start = V.Value(Fixtures.Floor1());
        Assert.Equal([start + 0.3, start + 0.2, start + 0.1], candidates.Select(c => c.Value), new Close());
        Assert.All(candidates, c => Assert.Null(c.Plan));
    }

    [Fact]
    public void WithExplainEachMapOptionCarriesItsRouteToTheBoss()
    {
        var run = Fixtures.Floor1();
        var at = run.Map.Points.First(p => p.Col == run.Map.Visited[^1].Col && p.Row == run.Map.Visited[^1].Row);
        var legal = at.Next.Select(c => MacroAction.Of(MacroAction.Map, c.Col, c.Row)).ToList();
        var map = new MacroDecision(Surfaces.Map, legal, () => new Observation { Run = run, Screen = new MapScreen { Travelable = at.Next } });

        Assert.All(Make(explain: false).Decide(map).Note.Candidates!, c => Assert.Null(c.Plan));

        var (chosen, note) = Make(explain: true).Decide(map);
        var candidates = note.Candidates!;
        Assert.Equal(legal.Select(a => a.ToString()).Order(), candidates.Select(c => c.Action).Order());
        Assert.Equal(chosen.Action.ToString(), candidates[0].Action);
        // The chosen option's route is the answer's plan; every route goes one row at a time from its option to the last row.
        Assert.Equal(note.Plan, candidates[0].Plan);
        int last = run.Map.Points.Where(p => p.Next.Count > 0).Max(p => p.Row);
        foreach (var c in candidates)
        {
            var rows = c.Plan!.Prepend(c.Action).Select(a => int.Parse(a.Split(',')[1])).ToList();
            Assert.Equal(Enumerable.Range(rows[0], rows.Count), rows);
            Assert.True(rows[^1] >= last);
        }
    }

    private sealed class Close : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-9;
        public int GetHashCode(double value) => 0;
    }
}
