using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// The fixed combat panel (docs: combat-brain §7, test 2): the same combats, from the same seeds, for every player
/// being compared, so differences come from the player and not from luck (common random numbers). A player is a
/// placeholder policy, or the PIMC brain in NoSL or SL.
///
/// The brain searches in this process, which can hold only one live combat, so before each of its decisions the real
/// combat is kept as a snapshot and put back by replay afterwards; or, with --workers, in worker processes, while the
/// real combat stays live here. Both give the same moves. Card prompts in the real combat are answered by p0
/// (<see cref="PromptBrain"/>) for every player, as in a run.
/// </summary>
internal static class Panel
{
    private static readonly Option<int> N = Opt.Of("--n", 40, "combats");
    private static readonly Option<int> Start = Opt.Of("--start", 0, "the first combat's index");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base");
    private static readonly Option<string> Player = Opt.Of("--player", "pimc", "who plays: pimc, or a placeholder policy")
        .AcceptOnlyFromAmong(["pimc", .. Policies.CombatNames]);
    private static readonly Option<PlayEnvironment> Env = Opt.Of("--env", PlayEnvironment.NoSL, "pimc's environment");
    private static readonly BudgetArgs Budget = new();
    private static readonly Option<int> Workers = Opt.Of("--workers", 0, "searches the NoSL worlds in this many worker processes");
    private static readonly LeafArgs Leaf = new();
    private static readonly Option<bool> Trace = Opt.Of("--trace", false, "prints each of pimc's decisions");
    private static readonly Option<string?> Out = Opt.Maybe<string?>("--out", "one JSON line per combat");
    private static readonly PoolArgs Pool = new();

    public static Command Command()
    {
        var command = Opt.Command("panel", "the fixed combat panel: the same combats for every player; win rate, HP lost",
            [N, Start, Seed, Player, Env], Budget.Options, [Workers], Leaf.Options, [Trace, Out], Pool.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        int n = r.GetValue(N);
        int start = r.GetValue(Start);
        int seed = r.GetValue(Seed);
        var player = r.GetValue(Player)!;
        var env = r.GetValue(Env);
        var budget = Budget.Read(r);
        int workers = r.GetValue(Workers);
        if (workers > 0 && (player != "pimc" || env != PlayEnvironment.NoSL)) throw new CliException("--workers is for --player pimc --env NoSL");
        var leaf = Leaf.Read(r);
        if (!leaf.IsDefault && player != "pimc") throw new CliException("--leaf and --combat-net are for --player pimc");
        var pool = Pool.Read(r);

        using var output = r.GetValue(Out) is { } outPath ? new StreamWriter(outPath) : null;
        using var workerPool = workers > 0 ? new WorkerPool(Cli.SelfCommand(), workers, leaf.WorkerOptions()) : null;
        var pooled = workerPool == null ? null : new PooledPimc(workerPool);
        var outcomes = new Dictionary<string, int>();
        int failed = 0, gameErrors = 0;
        long decisions = 0, hpLost = 0;
        Console.WriteLine($"panel: {n} combats from #{start}, act {pool.Act} ({pool.Rooms}: {pool.Encounters.Count} encounters), player {player}" +
                          leaf.Suffix +
                          (player == "pimc" ? $" {env} budget {budget}" : "") +
                          (workerPool != null ? $" on {workerPool.Count} workers" : "") +
                          $", game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = pool.Setup(i, Cli.MakeSeed(seed, i));
            var encounter = setup.Encounter;
            var started = clock.Elapsed;
            string outcome;
            string? failure = null;
            int hpStart = 0, hp = 0, rounds = 0, steps = 0;
            try
            {
                var rng = new Random(seed * 1_000_003 + i);
                var result = player == "pimc"
                    ? PlayWithBrain(pump, setup, PromptBrain.Instance, pooled, leaf.Leaf, env, budget, r.GetValue(Trace))
                    : PlayWithPolicy(pump, setup, PromptBrain.Instance, Policies.Combat(player, rng));
                (outcome, hpStart, hp, rounds, steps) = result;
            }
            catch (Exception e)
            {
                outcome = "Failed";
                failure = $"{e.GetType().Name}: {Cli.FirstLine(e.Message)}";
                failed++;
                Logs.Sim.LogDebug(e, "combat #{Index} failed", i);
            }
            var errors = HeadlessGame.TakeErrors();
            if (pooled != null) errors.AddRange(pooled.TakeGameErrors());
            gameErrors += errors.Count;
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            decisions += steps;
            if (outcome == nameof(CombatOutcome.Win)) hpLost += hpStart - hp;
            double ms = (clock.Elapsed - started).TotalMilliseconds;
            Console.WriteLine($"#{i} {encounter.Id.Entry} {setup.Seed} {outcome} hp {hp}/{hpStart} rounds {rounds} actions {steps} {ms:F0} ms" +
                              (failure != null ? $" failure: {failure}" : ""));
            output?.WriteLine(JsonSerializer.Serialize(new
            {
                i, encounter = encounter.Id.Entry, seed = setup.Seed, player, env = player == "pimc" ? env.ToString() : null,
                outcome, hp_start = hpStart, hp, rounds, actions = steps, ms, failure, game_errors = errors.Take(3),
            }));
        }
        clock.Stop();

        int wins = outcomes.GetValueOrDefault(nameof(CombatOutcome.Win));
        Console.WriteLine("outcomes: " + string.Join(" ", outcomes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"win rate {wins / (double)n:P1}, mean HP lost per win {(wins == 0 ? 0 : hpLost / (double)wins):F2}, " +
                          $"{clock.Elapsed.TotalSeconds:F1}s, {1000 * clock.Elapsed.TotalSeconds / Math.Max(1, decisions):F1} ms per action");
        Console.WriteLine($"game Log.Error lines: {gameErrors}" + (workerPool != null ? $"; worker restarts: {workerPool.Restarts}" : ""));
        return failed == 0 ? 0 : 1;
    }

    private static (string Outcome, int HpStart, int Hp, int Rounds, int Actions) PlayWithPolicy(Pump pump, CombatSetup setup, IChoicePolicy choices, ICombatPolicy policy)
    {
        using var session = CombatSession.Start(pump, setup, choices);
        int hpStart = session.Player.Creature.CurrentHp;
        while (!Cli.IsCapped(session))
            session.Execute(policy.Choose(Cli.Decision(session)));
        return (Outcome(session), hpStart, session.Player.Creature.CurrentHp, session.State.RoundNumber, session.ActionsExecuted);
    }

    /// <param name="pooled">Searches in workers; null searches here.</param>
    /// <param name="leaf">The leaf of the search here; the workers have theirs from their start.</param>
    private static (string Outcome, int HpStart, int Hp, int Rounds, int Actions) PlayWithBrain(Pump pump, CombatSetup setup, IChoicePolicy choices,
        PooledPimc? pooled, ICombatLeaf leaf, PlayEnvironment env, Budget budget, bool trace)
    {
        var brain = new PimcBrain(leaf);
        var moves = new MoveRecord();
        var plan = budget.Follow ? new TurnPlan() : null;
        CombatSession? session = CombatSession.Start(pump, setup, choices);
        try
        {
            int hpStart = session.Player.Creature.CurrentHp;
            while (!Cli.IsCapped(session))
            {
                var obs = session.Observation();
                var legal = session.LegalActions();
                moves.Witness(obs);
                var history = moves.History;
                ulong decisionSeed = CombatSession.StableHash($"{setup.Seed}:{session.ActionsExecuted}");
                string buildSeed = Cli.MakeSeed((int)decisionSeed, 0);
                CombatDecisionResult Search()
                {
                    if (legal.Count == 1) return PimcBrain.Only(legal[0]);
                    if (pooled != null) return pooled.Decide(obs, legal, buildSeed, budget, decisionSeed, history);
                    var real = session!.Snapshot();
                    session.Dispose();
                    session = null;
                    CombatDecisionResult searched;
                    using (var worlds = env == PlayEnvironment.SL ? SimWorlds.SL(pump, real) : SimWorlds.NoSL(pump, obs, buildSeed))
                        searched = brain.Decide(obs, legal, worlds, budget, decisionSeed, history);
                    session = CombatSession.Restore(pump, real, choices);
                    return searched;
                }
                var result = plan == null ? Search() : plan.Decide(obs, legal, Search);
                if (trace)
                    Console.WriteLine($"  {session.Summary()}\n    -> {result.First} then [{string.Join(" ", result.Plan)}]; " +
                                      string.Join(" ", result.Candidates.Take(4).Select(c => $"{c.Action}={c.Value:F3}±{c.Ci:F3}")));
                session.Execute(result.First);
            }
            return (Outcome(session), hpStart, session.Player.Creature.CurrentHp, session.State.RoundNumber, session.ActionsExecuted);
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static string Outcome(CombatSession session) => (session.IsOver ? session.Outcome : CombatOutcome.Truncated).ToString();
}
