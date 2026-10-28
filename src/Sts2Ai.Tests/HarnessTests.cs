using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

public class EvalReportTests
{
    private static readonly string[] Seeds = ["A", "B", "C"];

    [Fact]
    public void SortsTheRunsInTheSetsOrder()
    {
        var report = EvalReport.Of([Fixtures.Run("C", 10), Fixtures.Run("A", 20)], "set", Seeds, "2026-10-27");
        Assert.Equal(["A", "C"], report.Runs.Select(r => r.Seed));
        Assert.Equal(15, report.Metrics.FloorMean);
        Assert.False(report.Metrics.Void);
    }

    [Fact]
    public void RejectsRunsThatBreakTheSet()
    {
        Assert.Throws<ArgumentException>(() => EvalReport.Of([], "set", Seeds, "d"));
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 1), Fixtures.Run("A", 2)], "set", Seeds, "d"));
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("Z", 1)], "set", Seeds, "d"));
        var other = Fixtures.Config with { Commit = "def5678" };
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 1), Fixtures.Run("B", 1, config: other)], "set", Seeds, "d"));
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 1), Fixtures.Run("B", 1) with { Character = "IRONCLAD" }], "set", Seeds, "d"));
        Assert.Throws<ArgumentException>(() => EvalReport.Of([Fixtures.Run("A", 1), Fixtures.Run("B", 1) with { Ascension = 1 }], "set", Seeds, "d"));
    }

    [Fact]
    public void ARandomDecisionOrAFailureVoidsIt()
    {
        Assert.True(EvalReport.Of([Fixtures.Run("A", 5, randomDecisions: 1)], "set", Seeds, "d").Metrics.Void);
        Assert.True(EvalReport.Of([Fixtures.Run("A", 5, outcome: "failed")], "set", Seeds, "d").Metrics.Void);
    }
}

public class GateTests
{
    private static readonly RunConfig Challenger = Fixtures.Config with { Champion = "combat pimc c2 8x300, combat prompts p0, macro l0, macro prompts l0" };

    /// <summary>Floors that differ by <paramref name="gain"/> on average, by gain ± 2 in turn so the variance is not 0.</summary>
    private static List<(RunRecord, RunRecord)> Pairs(int n, double gain, string? challengerOutcome = null) =>
        Enumerable.Range(0, n).Select(i => (
            Fixtures.Run($"S{i}", 20),
            Fixtures.Run($"S{i}", 20 + (int)gain + (i % 2 == 0 ? 2 : -2), challengerOutcome ?? "lost", Challenger))).ToList();

    [Fact]
    public void AClearGainPasses()
    {
        var result = Gate.Sprt(Pairs(40, 5), "floor", 1.5);
        Assert.Equal("pass", result.Verdict);
        Assert.Equal(Gate.MinPairs, result.Decided);
        Assert.Equal(5, result.FloorDiffMean);
    }

    [Fact]
    public void PairsOnlyRunsOfTheChampionsCharacterAndAscension()
    {
        var champion = EvalReport.Of([Fixtures.Run("A", 10), Fixtures.Run("B", 10)], "set", ["A", "B"], "d");
        Assert.Equal(2, Gate.Pairs(champion, [Fixtures.Run("A", 12, config: Challenger), Fixtures.Run("B", 12, config: Challenger)]).Count);
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 12, config: Challenger) with { Character = "IRONCLAD" }]));
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 12, config: Challenger) with { Ascension = 5 }]));
    }

    [Fact]
    public void NoGainFails() => Assert.Equal("fail", Gate.Sprt(Pairs(200, 0), "floor", 1.5).Verdict);

    [Fact]
    public void ANotWorseGatePassesNoLossAndFailsALoss()
    {
        Assert.Equal("pass", Gate.Sprt(Pairs(200, 0), "floor", 1.5, notWorse: true).Verdict);
        Assert.Equal("fail", Gate.Sprt(Pairs(200, -5), "floor", 1.5, notWorse: true).Verdict);
        Assert.Contains("floor, not worse, delta 1.5", Gate.Sprt(Pairs(200, 0), "floor", 1.5, notWorse: true).Summary());
    }

    [Fact]
    public void AFloorGateDecidesNothingBeforeItsMinimumPairs() =>
        Assert.Equal("open", Gate.Sprt(Pairs(Gate.MinPairs - 1, 10), "floor", 1.5).Verdict);

    [Fact]
    public void TiedPairsDoNotCountTowardsTheMinimum()
    {
        // One run in ten gains 15 floors, none of the first ten: a gain of 1.5 that ties at first.
        var pairs = Enumerable.Range(0, 200).Select(i => (
            Fixtures.Run($"S{i}", 20),
            Fixtures.Run($"S{i}", i >= 10 && i % 10 == 9 ? 35 : 20, config: Challenger))).ToList();
        var result = Gate.Sprt(pairs, "floor", 1.5);
        Assert.Equal("pass", result.Verdict);
        Assert.True(result.Decided > 10 * Gate.MinPairs);
    }

    [Fact]
    public void AChallengerThatFailsARunIsVoid()
    {
        var result = Gate.Sprt(Pairs(40, 5, "failed"), "floor", 1.5);
        Assert.Equal("void", result.Verdict);
        Assert.Contains("failed 40", result.VoidReason);
    }

    [Fact]
    public void AWinGateCountsTheWinsOnlyOneSideHas()
    {
        var result = Gate.Sprt(Pairs(30, 0, "won"), "win", 0.2);
        Assert.Equal("pass", result.Verdict);
        Assert.Equal(0, result.ChampionOnlyWins);
        Assert.Throws<ArgumentException>(() => Gate.Sprt(Pairs(30, 0), "hp", 0.2));
    }

    [Fact]
    public void PairsTheChampionsSeedsTheChallengerPlayed()
    {
        var champion = EvalReport.Of([Fixtures.Run("A", 10), Fixtures.Run("B", 11), Fixtures.Run("C", 12)], "set", ["A", "B", "C"], "d");
        var pairs = Gate.Pairs(champion, [Fixtures.Run("C", 15, config: Challenger), Fixtures.Run("A", 9, config: Challenger)]);
        Assert.Equal([("A", 10, 9), ("C", 12, 15)], pairs.Select(p => (p.Champion.Seed, p.Champion.Floor, p.Challenger.Floor)));
    }

    [Fact]
    public void RefusesChallengersPlayedElsewhereOrTwice()
    {
        var champion = EvalReport.Of([Fixtures.Run("A", 10), Fixtures.Run("B", 11)], "set", ["A", "B"], "d");
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 1, config: Challenger with { Env = "SL" })]));
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 1, config: Challenger), Fixtures.Run("A", 2, config: Challenger)]));
        Assert.Throws<ArgumentException>(() => Gate.Pairs(champion, [Fixtures.Run("A", 1, config: Challenger), Fixtures.Run("B", 2)]));
    }
}

public class DispatcherTests
{
    private sealed class Fixed(string pick) : IMacroPolicy, IChoicePolicy
    {
        public string Choose(MacroDecision decision) => pick;
        public IReadOnlyList<int> Choose(ChoiceDecision decision) => [];
    }

    private static Dispatcher Make(CombatRoute combat, string macroPick = "map:0,1")
    {
        var macro = new Fixed(macroPick);
        return new Dispatcher("run-1", PlayEnvironment.NoSL, combat, new("p0", macro), new("l0", macro), new("l0", macro));
    }

    private static CombatDecision Decision(CombatObservation obs) => new([CombatAction.Play(0, 1), CombatAction.EndTurn], () => obs);

    private static MacroDecision Map(string surface = Surfaces.Map) =>
        new(surface, ["map:0,1"], () => throw new InvalidOperationException("the observation is not needed without a log"));

    [Fact]
    public void GivesTheCombatBrainTheMovesSoFar()
    {
        var seen = new List<IReadOnlyList<EnemyTurns>>();
        var dispatcher = Make(new("test", (_, _, clone, history) =>
        {
            Assert.Null(clone);
            seen.Add(history);
            return new(CombatAction.EndTurn, [], []);
        }));
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        dispatcher.Choose(Decision(Fixtures.Slugs() with { Round = 2 }));

        Assert.Equal([1, 2], seen.Select(h => h[0].Turns.Count));
        Assert.Equal(2, dispatcher.Brains["test"]);
    }

    [Fact]
    public void AMacroDecisionEndsTheCombatsRecord()
    {
        var dispatcher = Make(CombatRoute.Of("test", new EndTurns()));
        dispatcher.Choose(Decision(Fixtures.Slugs()));
        Assert.NotEmpty(dispatcher.History);

        Assert.Equal("map:0,1", dispatcher.Choose(Map()));
        Assert.Empty(dispatcher.History);
    }

    [Fact]
    public void RefusesAnIllegalPick()
    {
        var combat = Make(new("test", (_, _, _, _) => new(CombatAction.Play(9), [], [])));
        Assert.Throws<InvalidOperationException>(() => combat.Choose(Decision(Fixtures.Slugs())));
        Assert.Throws<InvalidOperationException>(() => Make(CombatRoute.Of("test", new EndTurns()), "map:9,9").Choose(Map()));
    }

    [Fact]
    public void HasNoFallbackForAnUncoveredSurface() =>
        Assert.Throws<InvalidOperationException>(() => Make(CombatRoute.Of("test", new EndTurns())).Choose(Map("crystal_ball")));

    [Fact]
    public void RefusesNodeCheckpointsInNoSL() =>
        Assert.Throws<InvalidOperationException>(() => Make(CombatRoute.Of("test", new EndTurns())).Checkpoint(new(0, "{}")));

    private sealed class EndTurns : ICombatPolicy
    {
        public CombatAction Choose(CombatDecision decision) => CombatAction.EndTurn;
    }
}

public class WorkerPoolTests
{
    private static readonly WorkerRequest Request = new SlWorldRequest(0, new NodeClone("", []), [], 1);

    /// <summary>A worker as a shell script: it says hello, then runs <paramref name="script"/> on its first request.</summary>
    private static WorkerPool Pool(string script) =>
        new(["/bin/sh", "-c", $"echo '{{\"game\":\"test\"}}'; read request; {script}"], 1, timeout: TimeSpan.FromSeconds(1));

    [Fact]
    public void AWorkerStuckTwiceFailsTheCall()
    {
        using var pool = Pool("sleep 30");
        var e = Assert.ThrowsAny<Exception>(() => pool.Map([Request]));
        Assert.Contains("did not answer within 1 s", e.InnerException?.Message ?? e.Message);
        Assert.Equal(1, pool.Restarts);
    }

    [Fact]
    public void AWorkerStuckOnceIsRestartedAndAnswers()
    {
        string marker = Path.Combine(Path.GetTempPath(), $"stuck-{Guid.NewGuid():N}");
        try
        {
            // The first process leaves the marker and hangs; the one started in its place answers.
            using var pool = Pool($"if [ -e {marker} ]; then echo '{{\"id\":0,\"lines\":[],\"game_errors\":[]}}'; read rest; else touch {marker}; sleep 30; fi");
            var reply = Assert.Single(pool.Map([Request]));
            Assert.Equal(0, reply.Id);
            Assert.Equal(1, pool.Restarts);
        }
        finally
        {
            File.Delete(marker);
        }
    }
}
