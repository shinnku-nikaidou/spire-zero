using System.Diagnostics;
using System.Text.Json;
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
/// real combat stays live here. Both give the same moves. Card prompts in the real combat are answered at random for
/// every player, as in bench.
/// </summary>
internal static class Panel
{
    private const int MaxRounds = 50;
    private const int MaxActions = 1000;

    public static int Run(Pump pump, Dictionary<string, string> opts)
    {
        int n = Cli.Int(opts, "n", 40);
        int start = Cli.Int(opts, "start", 0);
        int seed = Cli.Int(opts, "seed", 1);
        var player = opts.GetValueOrDefault("player", "pimc");
        var env = Enum.Parse<PlayEnvironment>(opts.GetValueOrDefault("env", "NoSL"), ignoreCase: true);
        var budget = new Budget(Cli.Int(opts, "worlds", 8), Cli.Int(opts, "nodes", 300));
        int workers = Cli.Int(opts, "workers", 0);
        if (player != "pimc" && !Policies.CombatNames.Contains(player)) return Cli.Fail($"unknown player {player} (pimc, {string.Join(", ", Policies.CombatNames)})");
        if (workers > 0 && (player != "pimc" || env != PlayEnvironment.NoSL)) return Cli.Fail("--workers is for --player pimc --env NoSL");
        var (act, rooms, pool) = Cli.Pool(opts);
        if (pool.Count == 0) return Cli.Fail("empty encounter pool");

        using var output = opts.TryGetValue("out", out var outPath) ? new StreamWriter(outPath) : null;
        using var workerPool = workers > 0 ? new WorkerPool(Cli.SelfCommand(), workers) : null;
        var pooled = workerPool == null ? null : new PooledPimc(workerPool);
        var outcomes = new Dictionary<string, int>();
        int failed = 0, gameErrors = 0;
        long decisions = 0, hpLost = 0;
        Console.WriteLine($"panel: {n} combats from #{start}, act {act} ({rooms}: {pool.Count} encounters), player {player}" +
                          (player == "pimc" ? $" {env} worlds {budget.Worlds} nodes {budget.NodesPerWorld}" : "") +
                          (workerPool != null ? $" on {workerPool.Count} workers" : "") +
                          $", game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var encounter = pool[i % pool.Count];
            var setup = Cli.Setup(opts, encounter, act, Cli.MakeSeed(seed, i));
            var started = clock.Elapsed;
            string outcome;
            string? failure = null;
            int hpStart = 0, hp = 0, rounds = 0, steps = 0;
            try
            {
                var rng = new Random(seed * 1_000_003 + i);
                var result = player == "pimc"
                    ? PlayWithBrain(pump, setup, new RandomChoices(rng), pooled, env, budget, opts.ContainsKey("trace"))
                    : PlayWithPolicy(pump, setup, new RandomChoices(rng), Policies.Combat(player, rng));
                (outcome, hpStart, hp, rounds, steps) = result;
            }
            catch (Exception e)
            {
                outcome = "Failed";
                failure = $"{e.GetType().Name}: {Cli.FirstLine(e.Message)}";
                failed++;
                if (HeadlessGame.Verbose) Console.Error.WriteLine(e);
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
        while (!session.IsOver && session.State.RoundNumber <= MaxRounds && session.ActionsExecuted < MaxActions)
            session.Execute(policy.Choose(Cli.Decision(session)));
        return (Outcome(session), hpStart, session.Player.Creature.CurrentHp, session.State.RoundNumber, session.ActionsExecuted);
    }

    /// <param name="pooled">Searches in workers; null searches here.</param>
    private static (string Outcome, int HpStart, int Hp, int Rounds, int Actions) PlayWithBrain(Pump pump, CombatSetup setup, IChoicePolicy choices,
        PooledPimc? pooled, PlayEnvironment env, Budget budget, bool trace)
    {
        var brain = new PimcBrain();
        CombatSession? session = CombatSession.Start(pump, setup, choices);
        try
        {
            int hpStart = session.Player.Creature.CurrentHp;
            while (!session.IsOver && session.State.RoundNumber <= MaxRounds && session.ActionsExecuted < MaxActions)
            {
                var obs = session.Observation();
                var legal = session.LegalActions();
                ulong decisionSeed = CombatSession.StableHash($"{setup.Seed}:{session.ActionsExecuted}");
                string buildSeed = Cli.MakeSeed((int)decisionSeed, 0);
                CombatDecisionResult result;
                if (legal.Count == 1) result = PimcBrain.Only(legal[0]);
                else if (pooled != null) result = pooled.Decide(obs, legal, buildSeed, budget, decisionSeed);
                else
                {
                    var real = session.Snapshot();
                    session.Dispose();
                    session = null;
                    using (var worlds = env == PlayEnvironment.SL ? SimWorlds.SL(pump, real) : SimWorlds.NoSL(pump, obs, buildSeed))
                        result = brain.Decide(obs, legal, worlds, budget, decisionSeed);
                    session = CombatSession.Restore(pump, real, choices);
                }
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
