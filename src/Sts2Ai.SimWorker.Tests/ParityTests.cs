using Sts2Ai.Protocol;
using Sts2Ai.SimWorker.Checks;

namespace Sts2Ai.SimWorker.Tests;

/// <summary>
/// The lockstep check's own logic, on a combat written down here and second worlds that go wrong where a test says:
/// the check must report each wrong place once, where it is, and nothing else.
/// </summary>
public class ParityTests
{
    private const int Decisions = 6;

    /// <summary>What the scripted game shows at its decision <paramref name="at"/>; the last is its end.</summary>
    private static Shown Real(int at, CombatStatus end = CombatStatus.Won) =>
        new($$"""{"round":{{at}},"hand":[{"id":"A","cost":1},{"id":"B"}]}""", at == Decisions ? [] : ["play:1", "end_turn"],
            at == Decisions ? end : CombatStatus.InProgress);

    /// <summary>A world of the scripted combat, made at a decision. It shows what the game showed, but for what
    /// <paramref name="wrong"/> gives for (the decision it was made at, the decision it stands at).</summary>
    private sealed class Scripted(int madeAt, Func<int, int, Shown?> wrong, CombatStatus end) : SecondWorld, ICombatWorld
    {
        private int _taken;

        private Shown Now => wrong(madeAt, madeAt + _taken) ?? Real(madeAt + _taken, end);

        public override ICombatWorld World => this;
        public override string Observation() => Now.Observation;
        public override void Dispose() { }

        public IReadOnlyList<CombatAction> Legal() => [.. Now.Legal.Select(CombatAction.Parse)];
        public CombatStatus Status => Now.Status;
        public void Execute(CombatAction action)
        {
            if (action != CombatAction.EndTurn) throw new InvalidOperationException($"the script has no {action}");
            _taken++;
        }

        public CombatObservation Observe() => throw new NotSupportedException();
        public RunView Run() => throw new NotSupportedException();
        public ulong HiddenKey() => throw new NotSupportedException();
        public WorldSnapshot Snapshot() => throw new NotSupportedException();
        public void Restore(WorldSnapshot snapshot) => throw new NotSupportedException();
    }

    /// <param name="wrong">Where the worlds go wrong (see <see cref="Scripted"/>).</param>
    /// <param name="unmade">A decision no world can be made at.</param>
    /// <param name="action">The action the game was given at each decision.</param>
    private static LockstepResult Check(Func<int, int, Shown?> wrong, int? continues = null, int? unmade = null, CombatStatus end = CombatStatus.Won,
        string action = "end_turn")
    {
        var present = GameIds.Kinds.ToDictionary(k => k, _ => new HashSet<string>());
        var steps = Enumerable.Range(0, Decisions + 1).Select(i => i == Decisions
            ? new RealStep(Real(i, end), i, null, null, null, present)
            : new RealStep(Real(i), i, CombatAction.Parse(action), action, i == unmade ? null : () => new Scripted(i, wrong, end), present)).ToList();
        return Lockstep.Compare(steps, continues, seed: 1);
    }

    private static string[] Found(LockstepResult result) => [.. result.Differences.Select(d => $"{d.Check}@{d.Decision}{(d.From is { } from ? $"<{from}" : "")} {d.Path}")];

    [Fact]
    public void AWorldThatAgreesEverywhereHasNoDifference()
    {
        var result = Check((_, _) => null);
        Assert.Empty(result.Differences);
        Assert.Equal((Decisions, Decisions, 5 + 4 + 3 + 2 + 1), (result.Imports, result.StepChecks, result.ContinueChecks));
        Assert.DoesNotContain(true, result.Differed);
    }

    [Fact]
    public void AWorldMadeWrongIsAnImportDifferenceAndIsNotPlayedOn()
    {
        var result = Check((made, at) => made == 2 && at == 2 ? Real(2) with { Observation = """{"round":2,"hand":[{"id":"A","cost":0},{"id":"B"}]}""" } : null);
        Assert.Equal(["import@2 hand[0].cost"], Found(result));
        Assert.Equal(("1", "0"), (result.Differences[0].Real, result.Differences[0].World));
        Assert.Equal([false, false, true, false, false, false], result.Differed);
        Assert.DoesNotContain(true, result.ActionDiffered);
        Assert.Equal(5 + 3 + 2 + 1, result.ContinueChecks);
    }

    [Fact]
    public void AnActionEveryWorldGetsWrongIsOneStepDifferenceAndOneForEachWorldThatReachesIt()
    {
        // After the action of decision 3, every world that took it shows one card less; a world made later is right.
        var result = Check((made, at) => made <= 3 && at >= 4 ? Real(at) with { Observation = $$"""{"round":{{at}},"hand":[{"id":"A","cost":1}]}""" } : null);
        Assert.Equal(["step@3 hand[1]", "continue@3<1 hand[1]", "continue@3<2 hand[1]", "continue@3<3 hand[1]"], Found(result));
        Assert.Equal(("""{"id":"B"}""", null), (result.Differences[0].Real, result.Differences[0].World));
        Assert.Equal([false, false, false, true, false, false], result.ActionDiffered);
        // The carried world is made again at decision 4 and goes on: every action is still checked once.
        Assert.Equal(Decisions, result.StepChecks);
    }

    [Fact]
    public void WhatAMadeWorldMissedShowsOnlyInItsContinuation()
    {
        // A world made at decision 2 lacks something hidden that the last action needs; the world carried from the start has it.
        var result = Check((made, at) => made == 2 && at == Decisions ? Real(at) with { Status = CombatStatus.Lost } : null);
        Assert.Equal(["continue@5<2 status"], Found(result));
    }

    [Fact]
    public void ALostCombatIsComparedByItsStatusAlone()
    {
        Assert.Empty(Check((_, at) => at == Decisions ? Real(at, CombatStatus.Lost) with { Observation = "{}" } : null, end: CombatStatus.Lost).Differences);
        Assert.Equal("step@5 round", Found(Check((_, at) => at == Decisions ? Real(at) with { Observation = "{}" } : null))[0]);
    }

    [Fact]
    public void OtherPlaysAreADifferenceOnceTheObservationAgrees()
    {
        var result = Check((made, at) => made == 0 && at == 1 ? Real(1) with { Legal = ["play:1", "potion:0", "end_turn"] } : null);
        Assert.Equal(["step@0 legal[1]"], Found(result));
        Assert.Equal(("end_turn", "potion:0"), (result.Differences[0].Real, result.Differences[0].World));
    }

    [Fact]
    public void AWorldThatFailsIsADifference()
    {
        var result = Check((_, _) => null, action: "play:1");
        Assert.Equal(Decisions, result.Differences.Count(d => d.Check == "step" && d.Path == "error" && d.World!.Contains("the script has no play:1")));
        Assert.All(result.ActionDiffered, Assert.True);
    }

    [Fact]
    public void WhereNoWorldCanBeMadeTheCarriedWorldStillPassesThrough()
    {
        var result = Check((_, _) => null, unmade: 2);
        Assert.Empty(result.Differences);
        Assert.Equal((Decisions - 1, Decisions), (result.Imports, result.StepChecks));
    }

    [Fact]
    public void ContinuingFromAFewDecisionsChecksThoseOnly()
    {
        var result = Check((_, _) => null, continues: 2);
        Assert.Equal(Decisions, result.Imports);
        Assert.InRange(result.ContinueChecks, 1 + 2, 5 + 4);
    }

    [Theory]
    [InlineData("""{"a":1,"b":[1,2]}""", """{"a":1,"b":[1,2]}""", null, null, null)]
    [InlineData("""{"a":1,"b":[1,2]}""", """{"a":1,"b":[1,3]}""", "b[1]", "2", "3")]
    [InlineData("""{"a":1,"b":[1,2]}""", """{"a":1,"b":[1,2,3]}""", "b[2]", null, "3")]
    [InlineData("""{"a":{"x":null},"b":2}""", """{"b":2}""", "a", """{"x":null}""", null)]
    [InlineData("""{"a":1}""", """{"a":1,"c":{"d":2}}""", "c", null, """{"d":2}""")]
    [InlineData("""{"a":1.0}""", """{"a":1}""", "a", "1.0", "1")]
    [InlineData("""{"a":"1"}""", """{"a":1}""", "a", "\"1\"", "1")]
    public void TheFirstJsonDifferenceIsItsPathAndBothValues(string real, string world, string? path, string? was, string? now)
    {
        (string, string?, string?)? expected = path == null ? null : (path, was, now);
        Assert.Equal(expected, JsonDiff.First(real, world));
    }

    [Fact]
    public void TheSameMembersInAnotherOrderDiffer() => Assert.Equal("", JsonDiff.First("""{"a":1,"b":2}""", """{"b":2,"a":1}""")!.Value.Path);

    [Fact]
    public void ASweepsLinesAddUpToItsSummary()
    {
        var result = Check((made, at) => made <= 3 && at >= 4 ? Real(at) with { Observation = $$"""{"round":{{at}},"hand":[{"id":"A","cost":1}]}""" } : null);
        var paths = result.Differences.GroupBy(ParityLine.PathKey).ToDictionary(g => g.Key, g => new[] { 0, g.Count(d => d.Check == "step"), g.Count(d => d.Check == "continue") });
        var line = new ParityLine
        {
            I = 7, Character = "SILENT", Ascension = 0, Encounter = "E", Act = 0, Seed = "S", Deck = "starting", Outcome = "Win", Decisions = Decisions,
            Differed = 1, DifferenceCount = result.Differences.Count, Differences = result.Differences, Paths = paths,
            Met = new() { ["cards"] = new() { ["A"] = [6, 1, 2, 1] } },
        };
        var summary = new ParitySummary();
        // A line survives its wire form: a sweep's parts send theirs to the whole.
        summary.Add(ProtocolJson.Deserialize<ParityLine>(ProtocolJson.Serialize(line)));
        summary.Add(line with { I = 8, Differed = 0, DifferenceCount = 0, Differences = [], Paths = [], Met = [] });
        Assert.False(summary.Clean);
        var text = new StringWriter();
        summary.Print(text, GameIds.Kinds.ToDictionary(k => k, k => k == "cards" ? new List<string> { "A", "Z" } : []));
        Assert.Contains("differed   1 decisions in 1 combats; differences: 0 import, 1 step, 3 continue", text.ToString());
        Assert.Contains("hand[]  (0 / 1 / 3)  #7 decision 3, end_turn: real {\"id\":\"B\"}, world nothing", text.ToString());
        Assert.Contains("never met, cards (1): Z", text.ToString());
    }
}
