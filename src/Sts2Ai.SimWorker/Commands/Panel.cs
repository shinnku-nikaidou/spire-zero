using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// The fixed combat panel (docs: combat-brain §5, test 2): the same combats, from the same seeds, for every player
/// being compared, so differences come from the player and not from luck (common random numbers). A player is a
/// placeholder policy, the PIMC brain in NoSL or SL, or the SL planner, which plans the whole combat once and follows
/// its plan (docs: sl-planner §4).
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
    private static readonly Option<string> Player = Opt.Of("--player", "pimc", "who plays: pimc, plan (SL only), or a placeholder policy")
        .AcceptOnlyFromAmong(["pimc", "plan", .. Policies.CombatNames]);
    private static readonly Option<PlayEnvironment> Env = Opt.Of("--env", PlayEnvironment.NoSL, "pimc's environment; plan's is SL");
    private static readonly BudgetArgs Budget = new();
    private static readonly Option<int> Workers = Opt.Of("--workers", 0, "searches the NoSL worlds in this many worker processes");
    private static readonly LeafArgs Leaf = new();
    private static readonly Option<bool> Trace = Opt.Of("--trace", false, "prints each of pimc's decisions");
    private static readonly Option<string?> Out = Opt.Maybe<string?>("--out", "one JSON line per combat");
    private static readonly Option<string?> Prefer = Opt.Maybe<string?>("--prompt-prefer",
        "an experiment: a card prompt that offers some of these card ids takes the first listed of them instead of p0's pick, in the real combat and the planner's worlds");
    private static readonly PoolArgs Pool = new();

    public static Command Command()
    {
        var command = Opt.Command("panel", "the fixed combat panel: the same combats for every player; win rate, HP lost",
            [N, Start, Seed, Player, Env], Budget.Options, [Workers], Leaf.Options, [Trace, Out, Prefer], Pool.Options);
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
        var budget = Budget.Read(r, player);
        bool searches = player is "pimc" or "plan";
        if (player == "plan" && env != PlayEnvironment.SL) throw new CliException("plan plays in SL only (--env SL)");
        int workers = r.GetValue(Workers);
        if (workers > 0 && (player != "pimc" || env != PlayEnvironment.NoSL)) throw new CliException("--workers is for --player pimc --env NoSL");
        var leaf = Leaf.Read(r);
        if (!leaf.IsDefault && !searches) throw new CliException("--combat-net is for --player pimc or plan");
        var pool = Pool.Read(r);
        var preferred = r.GetValue(Prefer) is { } ids ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        if (preferred != null && workers > 0) throw new CliException("--prompt-prefer does not reach the workers");
        IChoicePolicy prompts = preferred == null ? PromptBrain.Instance : new Preferred(preferred);

        using var output = r.GetValue(Out) is { } outPath ? new StreamWriter(outPath) : null;
        using var workerPool = workers > 0 ? new WorkerPool(Program.SelfCommand(), workers, leaf.WorkerOptions()) : null;
        var pooled = workerPool == null ? null : new PooledPimc(workerPool);
        var outcomes = new Dictionary<string, int>();
        int failed = 0, gameErrors = 0, replans = 0;
        long decisions = 0, hpLost = 0;
        Console.WriteLine($"panel: {n} combats from #{start}, act {pool.Act} ({pool.Rooms}: {pool.Encounters.Count} encounters), player {player}" +
                          leaf.Suffix +
                          (searches ? $" {env} budget {budget}" : "") +
                          (preferred != null ? $", prompts prefer {string.Join(",", preferred)}" : "") +
                          (workerPool != null ? $" on {workerPool.Count} workers" : "") +
                          $", game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = pool.Setup(i, GameSeeds.MakeSeed(seed, i));
            var encounter = setup.Encounter;
            var started = clock.Elapsed;
            string outcome;
            string? failure = null;
            int hpStart = 0, hp = 0, rounds = 0, steps = 0, enemyHp = 0, enemyMaxHp = 0;
            var searched = new Searched();
            var powers = new Dictionary<string, int>();
            try
            {
                var rng = new Random(seed * 1_000_003 + i);
                var result = searches
                    ? PlayWithBrain(pump, setup, prompts, pooled, player == "plan" ? null : new PimcBrain(leaf.Leaf),
                        new SlPlanner(leaf.Leaf), env, budget, searched, powers, r.GetValue(Trace))
                    : PlayWithPolicy(pump, setup, prompts, Policies.Combat(player, rng), powers);
                (outcome, hpStart, hp, rounds, steps, enemyHp, enemyMaxHp) = result;
            }
            catch (Exception e)
            {
                outcome = "Failed";
                failure = $"{e.GetType().Name}: {Logs.FirstLine(e.Message)}";
                failed++;
                Logs.Sim.LogError(e, "combat #{Index} failed", i);
            }
            var errors = HeadlessGame.TakeErrors();
            if (workerPool != null) errors.AddRange(workerPool.TakeGameErrors());
            gameErrors += errors.Count;
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            decisions += steps;
            replans += searched.Replans;
            if (outcome == nameof(CombatOutcome.Win)) hpLost += hpStart - hp;
            double ms = (clock.Elapsed - started).TotalMilliseconds;
            Console.WriteLine($"#{i} {encounter.Id.Entry} {setup.Seed} {outcome} hp {hp}/{hpStart} rounds {rounds} actions {steps} {ms:F0} ms" +
                              (player == "plan" ? $" plans {searched.Plans} ({searched.Proved} proved, {searched.Nodes} nodes, {searched.Replans} replans)" : "") +
                              (failure != null ? $" failure: {failure}" : ""));
            output?.WriteLine(JsonSerializer.Serialize(new
            {
                i, encounter = encounter.Id.Entry, seed = setup.Seed, player, env = searches ? env.ToString() : null, prefer = preferred,
                outcome, hp_start = hpStart, hp, rounds, actions = steps, enemy_hp = enemyHp, enemy_max_hp = enemyMaxHp, player_powers = powers, ms, failure,
                game_errors = errors.Take(3),
                searches = searched.Plans, proved = searched.Proved, nodes = searched.Nodes, replans = searched.Replans,
                search_ms = searched.Ms,
            }));
        }
        clock.Stop();

        int wins = outcomes.GetValueOrDefault(nameof(CombatOutcome.Win));
        Console.WriteLine("outcomes: " + string.Join(" ", outcomes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"win rate {wins / (double)n:P1}, mean HP lost per win {(wins == 0 ? 0 : hpLost / (double)wins):F2}, " +
                          $"{clock.Elapsed.TotalSeconds:F1}s, {1000 * clock.Elapsed.TotalSeconds / Math.Max(1, decisions):F1} ms per action");
        Console.WriteLine($"game Log.Error lines: {gameErrors}" + (workerPool != null ? $"; worker restarts: {workerPool.Restarts}" : "") +
                          (player == "plan" ? $"; replans: {replans}" : ""));
        return failed == 0 ? 0 : 1;
    }

    private static (string Outcome, int HpStart, int Hp, int Rounds, int Actions, int EnemyHp, int EnemyMaxHp) PlayWithPolicy(Pump pump, CombatSetup setup,
        IChoicePolicy choices, ICombatPolicy policy, Dictionary<string, int> powers)
    {
        using var session = CombatSession.Start(pump, setup, choices);
        int hpStart = session.Player.Creature.CurrentHp;
        while (!session.IsCapped)
        {
            var decision = session.Decision();
            Peak(powers, decision.Observation);
            session.Execute(policy.Choose(decision));
        }
        return Ended(session, hpStart);
    }

    /// <summary>How a combat ended: its outcome, the player's HP at the start and the end, its rounds and actions, and the
    /// HP the enemies still had, against their max HP (how far a lost fight got).</summary>
    private static (string Outcome, int HpStart, int Hp, int Rounds, int Actions, int EnemyHp, int EnemyMaxHp) Ended(CombatSession session, int hpStart) =>
        (Outcome(session), hpStart, session.Player.Creature.CurrentHp, session.State.RoundNumber, session.ActionsExecuted,
            session.State.Enemies.Where(e => e.IsAlive).Sum(e => e.CurrentHp), session.State.Enemies.Sum(e => e.MaxHp));

    /// <summary>Keeps each of the player's powers at its highest amount seen at the player's decisions; a lost combat
    /// ends with none.</summary>
    private static void Peak(Dictionary<string, int> powers, CombatObservation obs)
    {
        foreach (var power in obs.Player.Powers)
            powers[power.Id] = Math.Max(powers.GetValueOrDefault(power.Id, int.MinValue), power.Amount);
    }

    /// <summary>What a searching player's searches of one combat did: how many there were, how many of the planner's
    /// were proved the best, their nodes, the replans among them, and their time.</summary>
    private sealed class Searched
    {
        public int Plans, Proved, Replans;
        public long Nodes;
        public double Ms;
    }

    /// <param name="pooled">Searches in workers; null searches here.</param>
    /// <param name="pimc">The PIMC brain that plays, with its leaf; null for the planner.</param>
    /// <param name="planner">The SL planner, with the same leaf, when <paramref name="pimc"/> is null. It plans once and
    /// then follows its plan (<see cref="SlRoute"/>).</param>
    private static (string Outcome, int HpStart, int Hp, int Rounds, int Actions, int EnemyHp, int EnemyMaxHp) PlayWithBrain(Pump pump, CombatSetup setup, IChoicePolicy choices,
        PooledPimc? pooled, PimcBrain? pimc, SlPlanner planner, PlayEnvironment env, Budget budget, Searched searched, Dictionary<string, int> powers,
        bool trace)
    {
        var moves = new MoveRecord();
        var plan = budget.Follow && pimc != null ? new TurnPlan() : null;
        var route = pimc == null ? new SlRoute(budget) : null;
        CombatSession? session = CombatSession.Start(pump, setup, choices);
        try
        {
            int hpStart = session.Player.Creature.CurrentHp;
            while (!session.IsCapped)
            {
                var obs = session.Observation();
                var legal = session.LegalActions();
                Peak(powers, obs);
                moves.Witness(obs);
                var history = moves.History;
                ulong decisionSeed = CombatSession.StableHash($"{setup.Seed}:{session.ActionsExecuted}");
                string buildSeed = GameSeeds.MakeSeed((int)decisionSeed, 0);
                // Searches the decision's worlds here: the real combat is kept as a snapshot meanwhile and put back after.
                T Searching<T>(Func<IWorldSource, Stopwatch, T> search)
                {
                    var real = session!.Snapshot();
                    session.Dispose();
                    session = null;
                    var clock = Stopwatch.StartNew();
                    T result;
                    using (var worlds = env == PlayEnvironment.SL ? SimWorlds.SL(pump, real, choices) : SimWorlds.NoSL(pump, obs, buildSeed, history))
                        result = search(worlds, clock);
                    searched.Plans++;
                    searched.Ms += clock.Elapsed.TotalMilliseconds;
                    session = CombatSession.Restore(pump, real, choices);
                    return result;
                }
                CombatDecisionResult Search()
                {
                    if (legal.Count == 1) return PimcBrain.Only(legal[0]);
                    if (pooled != null) return pooled.Decide(obs, legal, buildSeed, budget, decisionSeed, history);
                    return Searching((worlds, _) => pimc!.Decide(obs, legal, worlds, budget, decisionSeed, history));
                }
                SlPlan Plan(Budget b) => Searching((worlds, clock) =>
                {
                    var made = planner.Plan(obs, worlds.Worlds(1, 0)[0], b, history);
                    searched.Proved += made.Proved ? 1 : 0;
                    searched.Nodes += made.Nodes;
                    if (trace)
                        Console.WriteLine($"  plan: {made.End} value {made.Value:F3}, {made.Result.Plan.Count + 1} actions, {made.Turns} turns, {made.Nodes} nodes" +
                                          $"{(made.Proved ? ", proved" : "")}, {clock.ElapsedMilliseconds} ms");
                    return made;
                });
                var result = route != null ? route.Decide(obs, legal, Plan) : plan == null ? Search() : plan.Decide(obs, legal, Search);
                if (result.Replanned) searched.Replans++;
                if (trace)
                    Console.WriteLine($"  {session.Summary()}\n    -> {result.First} then [{string.Join(" ", result.Plan)}]; " +
                                      string.Join(" ", result.Candidates.Take(4).Select(c => $"{c.Action}={c.Value:F3}±{c.Ci:F3}")));
                session.Execute(result.First);
            }
            return Ended(session, hpStart);
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>p0, except that a prompt offering some of the preferred card ids takes the first listed of them.</summary>
    private sealed class Preferred(IReadOnlyList<string> ids) : IChoicePolicy
    {
        public IReadOnlyList<int> Choose(ChoiceDecision decision)
        {
            var options = decision.Choice.Options;
            if (decision.Choice.Max == 1)
                foreach (var id in ids)
                    for (int i = 0; i < options.Count; i++)
                        if (options[i].Id == id)
                            return [i];
            return PromptBrain.Instance.Choose(decision);
        }
    }

    private static string Outcome(CombatSession session) => (session.IsOver ? session.Outcome : CombatOutcome.Truncated).ToString();
}
