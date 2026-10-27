using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// Command line:
///   list                                   characters, acts and encounters known to this game build
///   combat --encounter ID [--seed N] [--policy random|play] [--obs] [--verbose]
///                                          one combat, printing every step (--obs: the observation JSON too)
///   combat --from OBS.json [--seed N] ...  the same, starting from a combat built to show that observation
///   bench [--n 200] [--start 0] [--seed 1] [--act 0] [--rooms weak,regular] [--policy play] [--out results.jsonl]
///                                          many combats over an encounter pool; reports throughput and failures
///   selftest [--n 20] [--seed 1] [--act 0] [--rooms weak,regular] [--workers 0]
///                                          checks determinism, restore and determinize over an encounter pool
///   forkbench [--n 30] [--seed 1] [--act 0] [--rooms weak,regular] [--branches 20]
///                                          cost of restoring a combat and of one search branch
///   run [--n 1] [--start 0] [--seed 1 | --seeds FILE] [--asc 0] [--policy play|random|pimc] [--macro random|l0]
///       [--worlds 8] [--nodes 300] [--workers W] [--max-decisions 20000] [--check] [--trace] [--obs]
///       [--log decisions.jsonl] [--records runs.jsonl]
///                                          whole runs through Harness. Combat prompts are random; --macro picks the
///                                          brain of every decision outside combat (l0: the macro brain with V v0); --check plays
///                                          each seed twice and compares, observations and decision logs included (each
///                                          observation must survive a JSON round trip); --obs prints them; --log writes
///                                          the decision log; reports results, hashes and surface coverage.
///                                          pimc searches each combat decision (NoSL) in W worker processes (default:
///                                          one per world). --seeds plays the seeds of a seed file, from --start, all
///                                          of them unless --n; --records writes one run record per line (docs: evaluation §6)
///   report --seeds FILE --records A.jsonl[,B.jsonl] --out PATH [--previous OLD.json]
///                                          the evaluation report over those records: PATH.json and PATH.md
///   seeds --base N --n 300                 a seed file's seeds: MakeSeed(N, 0..n-1), one per line
///   panel [--n 40] [--start 0] [--seed 1] [--act 0] [--rooms weak,regular] [--player pimc|play|random] [--env NoSL|SL]
///         [--worlds 8] [--nodes 300] [--workers 0] [--trace] [--out results.jsonl]
///                                          the fixed combat panel: the same combats for every player; win rate, HP lost.
///                                          --workers N searches the NoSL worlds in N worker processes
///   serve                                  a worker: JSON lines on stdin and stdout (docs: simulator §8)
/// Loadout options for every command that runs combats: [--character SILENT] [--deck A*5,B+,...]
/// [--relics A,B] [--potions A,B] [--hp N] [--max-hp N]. Omitted parts keep the character's starting values.
/// </summary>
internal static class Cli
{
    private const int MaxRounds = 50;
    private const int MaxActions = 1000;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: list | combat --encounter ID | bench | selftest | forkbench | run | panel | serve | report | seeds"); return 2; }
        // These two need no game.
        if (args[0] == "report") return Report(ParseOptions(args.Skip(1).ToArray()));
        if (args[0] == "seeds") return Seeds(ParseOptions(args.Skip(1).ToArray()));
        // A worker's stdout is the wire: keep it, and send everything else that is printed to stderr from the start.
        TextWriter? wire = null;
        if (args[0] == "serve")
        {
            wire = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(Console.Error);
        }
        var opts = ParseOptions(args.Skip(1).ToArray());
        HeadlessGame.Boot(opts.ContainsKey("verbose"));
        var pump = Pump.InstallOnCurrentThread();
        return args[0] switch
        {
            "list" => List(),
            "combat" => OneCombat(pump, opts),
            "bench" => Bench(pump, opts),
            "selftest" => SelfTest.Run(pump, opts),
            "forkbench" => ForkBench.Run(pump, opts),
            "run" => Runs(pump, opts),
            "panel" => Panel.Run(pump, opts),
            "serve" => Serve.Run(pump, wire!),
            _ => Fail($"unknown command {args[0]}"),
        };
    }

    private static int List()
    {
        Console.WriteLine($"game {HeadlessGame.GameVersion}");
        Console.WriteLine("characters: " + string.Join(" ", ModelDb.AllCharacters.Select(c => c.Id.Entry)));
        for (int i = 0; i < ModelDb.ActsByIndex.Count; i++)
        {
            foreach (var act in ModelDb.ActsByIndex[i])
            {
                Console.WriteLine($"act {i}: {act.Id.Entry}{(act.IsDefault ? " (default)" : "")}");
                Print("weak", act.AllWeakEncounters);
                Print("regular", act.AllRegularEncounters);
                Print("elite", act.AllEliteEncounters);
                Print("boss", act.AllBossEncounters);
            }
        }
        return 0;

        static void Print(string label, IEnumerable<EncounterModel> encounters) =>
            Console.WriteLine($"  {label,-8} " + string.Join(" ", encounters.Select(e => e.Id.Entry)));
    }

    private static int OneCombat(Pump pump, Dictionary<string, string> opts)
    {
        int seed = Int(opts, "seed", 1);
        CombatSetup setup;
        if (opts.TryGetValue("from", out var path))
        {
            setup = CombatBuilder.Setup(ProtocolJson.Deserialize<CombatObservation>(File.ReadAllText(path)), MakeSeed(seed, 0));
        }
        else
        {
            if (!opts.TryGetValue("encounter", out var id)) return Fail("combat needs --encounter ID (see `list`) or --from OBS.json");
            var encounter = ModelDb.AllEncounters.FirstOrDefault(e => e.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (encounter == null) return Fail($"no encounter {id}");
            int act = Enumerable.Range(0, ModelDb.ActsByIndex.Count).FirstOrDefault(i => ModelDb.ActsByIndex[i].Any(a => a.AllEncounters.Contains(encounter)));
            setup = Setup(opts, encounter, act, MakeSeed(seed, 0));
        }
        var rng = new Random(seed);
        var policy = Policies.Combat(opts.GetValueOrDefault("policy", "play"), rng);

        Console.WriteLine($"{setup.Encounter.Id.Entry} act {setup.ActIndex} seed {setup.Seed}{(setup.From != null ? " (built from observation)" : "")}");
        using var session = CombatSession.Start(pump, setup, new RandomChoices(rng));
        foreach (var difference in session.BuildDifferences) Console.WriteLine("  not reproduced: " + difference);
        while (!session.IsOver && session.State.RoundNumber <= MaxRounds && session.ActionsExecuted < MaxActions)
        {
            Console.WriteLine("  " + session.Summary());
            if (opts.ContainsKey("obs")) Console.WriteLine("  " + ProtocolJson.Serialize(session.Observation()));
            var action = policy.Choose(Decision(session));
            Console.WriteLine($"    -> {action}");
            session.Execute(action);
        }
        Console.WriteLine($"result {session.Outcome} after {session.ActionsExecuted} actions, round {session.State.RoundNumber}, hp {session.Player.Creature.CurrentHp}");
        Console.WriteLine("trace: " + string.Join(" ", session.Trace));
        foreach (var e in HeadlessGame.TakeErrors()) Console.WriteLine("  game error: " + e);
        return 0;
    }

    private static int Bench(Pump pump, Dictionary<string, string> opts)
    {
        int n = Int(opts, "n", 200);
        int start = Int(opts, "start", 0);
        int seed = Int(opts, "seed", 1);
        var policy = opts.GetValueOrDefault("policy", "play");
        if (!Policies.CombatNames.Contains(policy)) return Fail($"unknown policy {policy} (one of {string.Join(", ", Policies.CombatNames)})");
        var (act, rooms, pool) = Pool(opts);
        if (pool.Count == 0) return Fail("empty encounter pool");

        using var output = opts.TryGetValue("out", out var outPath) ? new StreamWriter(outPath) : null;
        var outcomes = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        var failedEncounters = new Dictionary<string, int>();
        var errorTexts = new Dictionary<string, int>();
        int gameErrors = 0, combatsWithErrors = 0, firstWithErrors = -1;
        long actions = 0;

        Console.WriteLine($"bench: {n} combats, act {act} ({rooms}: {pool.Count} encounters), policy {policy}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var encounter = pool[i % pool.Count];
            var setup = Setup(opts, encounter, act, MakeSeed(seed, i));
            var rng = new Random(seed * 1_000_003 + i);
            var started = clock.Elapsed;
            string outcome;
            string? failure = null;
            int steps = 0, rounds = 0, hp = 0;
            double startMs = 0;
            try
            {
                using var session = CombatSession.Start(pump, setup, new RandomChoices(rng));
                startMs = (clock.Elapsed - started).TotalMilliseconds;
                PlayOut(session, Policies.Combat(policy, rng));
                outcome = session.IsOver ? session.Outcome.ToString() : CombatOutcome.Truncated.ToString();
                steps = session.ActionsExecuted;
                rounds = session.State.RoundNumber;
                hp = session.Player.Creature.CurrentHp;
            }
            catch (Exception e)
            {
                outcome = "Failed";
                failure = $"{e.GetType().Name}: {FirstLine(e.Message)}";
                failures[failure] = failures.GetValueOrDefault(failure) + 1;
                failedEncounters[encounter.Id.Entry] = failedEncounters.GetValueOrDefault(encounter.Id.Entry) + 1;
                if (HeadlessGame.Verbose) Console.Error.WriteLine(e);
            }
            var errors = HeadlessGame.TakeErrors();
            gameErrors += errors.Count;
            if (errors.Count > 0 && combatsWithErrors++ == 0) firstWithErrors = i;
            foreach (var e in errors) errorTexts[FirstLine(e)] = errorTexts.GetValueOrDefault(FirstLine(e)) + 1;
            actions += steps;
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            output?.WriteLine(JsonSerializer.Serialize(new
            {
                i, encounter = encounter.Id.Entry, seed = setup.Seed, outcome, steps, rounds, hp,
                ms = (clock.Elapsed - started).TotalMilliseconds, start_ms = startMs, failure, game_errors = errors.Take(3),
            }));
        }
        clock.Stop();

        double secs = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"done in {secs:F2}s: {n / secs:F1} combats/s, {actions / secs:F0} actions/s, {1000 * secs / n:F1} ms/combat");
        Console.WriteLine("outcomes: " + string.Join(" ", outcomes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"pump idle waits: {pump.IdleWaits} ({pump.IdleWaitTicks} ms)");
        Console.WriteLine($"game Log.Error lines: {gameErrors}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20)} MB");
        if (combatsWithErrors > 0)
            Console.WriteLine($"  {combatsWithErrors} combats logged errors, first #{firstWithErrors}");
        foreach (var (msg, count) in errorTexts.OrderByDescending(kv => kv.Value).Take(5))
            Console.WriteLine($"  game error x{count}: {(msg.Length > 300 ? msg[..300] + "..." : msg)}");
        foreach (var (msg, count) in failures.OrderByDescending(kv => kv.Value).Take(10))
            Console.WriteLine($"  failure x{count}: {msg}");
        if (failedEncounters.Count > 0)
            Console.WriteLine("  failing encounters: " + string.Join(" ", failedEncounters.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}")));
        return failures.Count == 0 ? 0 : 1;
    }

    private static int Runs(Pump pump, Dictionary<string, string> opts)
    {
        var seeds = opts.TryGetValue("seeds", out var seedFile) ? EvalReport.ReadSeeds(seedFile) : null;
        int start = Int(opts, "start", 0);
        int n = Int(opts, "n", seeds == null ? 1 : seeds.Count - start);
        if (seeds != null && start + n > seeds.Count) return Fail($"{seedFile} has {seeds.Count} seeds, not {start + n}");
        int seed = Int(opts, "seed", 1);
        int maxDecisions = Int(opts, "max-decisions", 20_000);
        bool check = opts.ContainsKey("check");
        bool observe = check || opts.ContainsKey("obs");
        using var logFile = opts.TryGetValue("log", out var logPath) ? new StreamWriter(logPath) : null;
        using var records = opts.TryGetValue("records", out var recordsPath) ? new StreamWriter(recordsPath) { AutoFlush = true } : null;
        bool log = check || logFile != null;
        var policy = opts.GetValueOrDefault("policy", "play");
        if (policy != "pimc" && !Policies.CombatNames.Contains(policy)) return Fail($"unknown policy {policy} (pimc, {string.Join(", ", Policies.CombatNames)})");
        string macroBrain = opts.GetValueOrDefault("macro", "random");
        if (macroBrain is not ("random" or "l0")) return Fail($"unknown macro brain {macroBrain} (random, l0)");
        var budget = new Budget(Int(opts, "worlds", 8), Int(opts, "nodes", 300));
        // The run holds this process's one live game, so the brain can only search in workers.
        using var workerPool = policy == "pimc" ? new WorkerPool(SelfCommand(), Int(opts, "workers", budget.Worlds)) : null;
        var pooled = workerPool == null ? null : new PooledPimc(workerPool);
        var loadout = Loadout.Parse(opts.GetValueOrDefault("deck"), opts.GetValueOrDefault("relics"), opts.GetValueOrDefault("potions"),
            opts.GetValueOrDefault("hp"), opts.GetValueOrDefault("max-hp"));

        var outcomes = new Dictionary<string, int>();
        var surfaces = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        int mismatches = 0, gameErrors = 0;
        Console.WriteLine($"run: {n} runs, policy {policy}" +
                          (pooled != null ? $" worlds {budget.Worlds} nodes {budget.NodesPerWorld} on {workerPool!.Count} workers" : "") + $", macro {macroBrain}" +
                          $", game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = new RunSetup(opts.GetValueOrDefault("character", "SILENT"), seeds?[i] ?? MakeSeed(seed, i), Int(opts, "asc", 0), loadout);
            var started = clock.Elapsed;
            // Placeholders draw from the run's generator, so a seed plays the same run whatever is logged.
            Dispatcher Harness(Random rng)
            {
                var combat = pooled?.Route(budget) ?? CombatRoute.Of(policy, Policies.Combat(policy, rng));
                var random = new RandomChoices(rng);
                if (macroBrain == "l0")
                {
                    var macro = new MacroBrain(RunValue.Initial);
                    return new Dispatcher(setup.Seed, PlayEnvironment.NoSL, combat, new("random", random), new("l0", macro), new("l0", macro), log);
                }
                return new Dispatcher(setup.Seed, PlayEnvironment.NoSL, combat, new("random", random), new("random", new RandomMacro(rng)), new("random", random), log);
            }
            var first = PlayRun(pump, setup, Harness, maxDecisions, opts.ContainsKey("trace"), observe, opts.ContainsKey("obs"));
            if (pooled != null) first.Errors.AddRange(pooled.TakeGameErrors());
            foreach (var entry in first.Log) logFile?.WriteLine(ProtocolJson.Serialize(entry));
            var ms = (clock.Elapsed - started).TotalMilliseconds;
            records?.WriteLine(ProtocolJson.Serialize(first.Record with { GameErrors = first.Errors.Count, ElapsedMs = ms }));
            outcomes[first.Outcome] = outcomes.GetValueOrDefault(first.Outcome) + 1;
            foreach (var (surface, count) in first.Record.Surfaces) surfaces[surface] = surfaces.GetValueOrDefault(surface) + count;
            if (first.Failure != null) failures[first.Failure] = failures.GetValueOrDefault(first.Failure) + 1;
            gameErrors += first.Errors.Count;
            Console.WriteLine($"#{i} {setup.Seed} {first.Outcome} {first.Summary} decisions {first.Trace.Count} {ms:F0} ms " +
                              $"maps {string.Join(",", first.MapHashes.Select(h => h.ToString("x16")))}" +
                              (observe ? $" obs {Digest(first.Observations):x16}" : ""));
            if (first.Failure != null) Console.WriteLine($"  failure: {first.Failure}");
            foreach (var e in first.Errors.Take(3)) Console.WriteLine($"  game error: {FirstLine(e)}");
            if (!check) continue;
            var second = PlayRun(pump, setup, Harness, maxDecisions, trace: false, observe, printObservations: false);
            if (pooled != null) pooled.TakeGameErrors();
            // Each decision's observation is shown before its action, so they pair up by index.
            int diverged = Enumerable.Range(0, Math.Min(first.Trace.Count, second.Trace.Count)).FirstOrDefault(k =>
                first.Trace[k] != second.Trace[k] || first.Observations.ElementAtOrDefault(k) != second.Observations.ElementAtOrDefault(k), -1);
            bool same = first.Trace.Count == second.Trace.Count && first.Summary == second.Summary &&
                first.MapHashes.SequenceEqual(second.MapHashes) && first.Observations.SequenceEqual(second.Observations);
            static string Timeless(DecisionEntry e) => ProtocolJson.Serialize(e with { ElapsedMs = 0 });
            bool sameLog = first.Log.Select(Timeless).SequenceEqual(second.Log.Select(Timeless));
            if (diverged < 0 && same && sameLog) continue;
            mismatches++;
            Console.WriteLine(diverged >= 0
                ? first.Trace[diverged] != second.Trace[diverged] ? $"  NOT DETERMINISTIC: decision {diverged}: {first.Trace[diverged]} vs {second.Trace[diverged]}"
                : $"  NOT DETERMINISTIC: observation {diverged} ({first.Trace[diverged]}) differs"
                : !same ? $"  NOT DETERMINISTIC: {first.Trace.Count} vs {second.Trace.Count} decisions; {second.Summary}"
                : "  NOT DETERMINISTIC: the decision logs differ");
        }
        clock.Stop();

        int decisions = surfaces.Values.Sum();
        Console.WriteLine($"done in {clock.Elapsed.TotalSeconds:F1}s: {decisions / clock.Elapsed.TotalSeconds:F0} decisions/s");
        Console.WriteLine($"pump idle waits: {pump.IdleWaits} ({pump.IdleWaitTicks} ms)");
        Console.WriteLine("outcomes: " + string.Join(" ", outcomes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine("surfaces: " + string.Join(" ", surfaces.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        var missing = typeof(Surfaces).GetFields().Select(f => (string)f.GetValue(null)!).Where(s => !surfaces.ContainsKey(s)).ToList();
        if (missing.Count > 0) Console.WriteLine("surfaces never reached: " + string.Join(" ", missing));
        foreach (var (msg, count) in failures.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  failure x{count}: {msg}");
        if (check) Console.WriteLine($"determinism: {n - mismatches}/{n} runs identical on replay");
        Console.WriteLine($"game Log.Error lines: {gameErrors}" + (workerPool != null ? $"; worker restarts: {workerPool.Restarts}" : ""));
        return failures.Count == 0 && mismatches == 0 && gameErrors == 0 ? 0 : 1;
    }

    /// <param name="Observations">Per decision, its observation's hash (when observing).</param>
    /// <param name="Log">Harness's decision log, when logging.</param>
    /// <param name="Record">The run's record, its game errors and time still to fill in.</param>
    private sealed record RunResult(string Outcome, string Summary, IReadOnlyList<string> Trace, IReadOnlyList<ulong> MapHashes,
        IReadOnlyList<ulong> Observations, IReadOnlyList<DecisionEntry> Log, string? Failure, List<string> Errors, RunRecord Record);

    /// <summary>
    /// One run to the end (or the decision cap) through the Harness that <paramref name="harness"/> builds on one
    /// generator, seeded by the run's seed, so the seed alone gives the same run.
    /// Observing checks that each observation survives a JSON round trip, and keeps its hash.
    /// </summary>
    private static RunResult PlayRun(Pump pump, RunSetup setup, Func<Random, Dispatcher> harness, int maxDecisions, bool trace, bool observe, bool printObservations)
    {
        var rng = new Random(unchecked((int)CombatSession.StableHash(setup.Seed)));
        var dispatcher = harness(rng);
        var observations = new List<ulong>();
        RunSession? session = null;
        try
        {
            Action<Observation>? observed = !observe ? null : obs =>
            {
                var json = ProtocolJson.Serialize(obs);
                if (ProtocolJson.Serialize(ProtocolJson.Deserialize<Observation>(json)) != json) throw new InvalidOperationException($"observation does not round-trip: {json}");
                if (printObservations) Console.WriteLine($"  obs {observations.Count}: {json}");
                observations.Add(ProtocolJson.Hash(obs));
            };
            session = RunSession.Start(pump, setup, dispatcher, dispatcher, dispatcher, observed);
            int printed = 0;
            while (!session.IsOver && session.Trace.Count < maxDecisions)
            {
                session.Step();
                if (!trace) continue;
                for (; printed < session.Trace.Count; printed++) Console.WriteLine($"  {printed}: {session.Trace[printed]}");
            }
            return Result(session.Won ? "won" : session.IsOver ? "lost" : "truncated", null);
        }
        catch (Exception e)
        {
            if (HeadlessGame.Verbose) Console.Error.WriteLine(e);
            return Result("failed", $"{e.GetType().Name}: {FirstLine(e.Message)}");
        }
        finally
        {
            session?.Dispose();
        }

        RunResult Result(string outcome, string? failure)
        {
            var steps = session?.Trace.ToList() ?? [];
            var surfaces = steps.GroupBy(s => s[..s.IndexOf(' ')]).ToDictionary(g => g.Key, g => g.Count());
            var config = new RunConfig(HeadlessGame.GameVersion, Commit, dispatcher.Champion, dispatcher.Env.ToString(), "R1");
            var player = session?.Player;
            var record = new RunRecord(config, setup.Seed, setup.Character, setup.Ascension, outcome,
                session?.Run.CurrentActIndex ?? 0, session?.Run.TotalFloor ?? 0, player?.Creature.CurrentHp ?? 0, player?.Creature.MaxHp ?? 0,
                player?.Gold ?? 0, session?.Combats.ToList() ?? [], surfaces, new Dictionary<string, int>(dispatcher.Brains),
                session?.CardRewards ?? 0, session?.CardRewardsSkipped ?? 0, 0, failure, 0);
            return new(outcome, session?.Summary() ?? "", steps, session?.MapHashes.ToList() ?? [], observations, dispatcher.Log.ToList(), failure,
                HeadlessGame.TakeErrors(), record);
        }
    }

    /// <summary>The commit this program was built from, as the build stamped it.</summary>
    private static readonly string Commit =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').ElementAtOrDefault(1) ?? "unknown";

    private static int Report(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("seeds", out var seedFile) || !opts.TryGetValue("records", out var recordFiles) || !opts.TryGetValue("out", out var outPath))
            return Fail("usage: report --seeds FILE --records A.jsonl[,B.jsonl] --out PATH [--previous OLD.json]");
        var runs = recordFiles.Split(',').SelectMany(File.ReadAllLines).Where(l => l.Length > 0).Select(ProtocolJson.Deserialize<RunRecord>).ToList();
        var previous = opts.TryGetValue("previous", out var previousPath) ? ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(previousPath)) : null;
        EvalReport report;
        try
        {
            report = EvalReport.Of(runs, Path.GetFileNameWithoutExtension(seedFile), EvalReport.ReadSeeds(seedFile), DateTime.Now.ToString("yyyy-MM-dd"));
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath + ".json", report.Json());
        var markdown = report.Markdown(previous);
        File.WriteAllText(outPath + ".md", markdown);
        Console.Write(markdown);
        return 0;
    }

    private static int Seeds(Dictionary<string, string> opts)
    {
        if (!opts.ContainsKey("base")) return Fail("usage: seeds --base N --n 300");
        int seedBase = Int(opts, "base", 0);
        foreach (int i in Enumerable.Range(0, Int(opts, "n", 300))) Console.WriteLine(MakeSeed(seedBase, i));
        return 0;
    }

    /// <summary>One hash for a run's observation hashes, to compare runs across processes.</summary>
    private static ulong Digest(IReadOnlyList<ulong> hashes)
    {
        ulong digest = 14695981039346656037;
        foreach (var h in hashes) digest = (digest ^ h) * 1099511628211;
        return digest;
    }

    /// <summary>Plays until the combat ends or hits the round/action cap. Calls <paramref name="beforeAction"/> before each action.</summary>
    public static void PlayOut(CombatSession session, ICombatPolicy policy, Action<CombatSession>? beforeAction = null)
    {
        while (!session.IsOver && session.State.RoundNumber <= MaxRounds && session.ActionsExecuted < MaxActions)
        {
            beforeAction?.Invoke(session);
            session.Execute(policy.Choose(Decision(session)));
        }
    }

    public static CombatDecision Decision(CombatSession session) => new(session.LegalActions(), session.Observation);

    public static CombatSetup Setup(Dictionary<string, string> opts, EncounterModel encounter, int act, string seed)
    {
        var loadout = Loadout.Parse(opts.GetValueOrDefault("deck"), opts.GetValueOrDefault("relics"), opts.GetValueOrDefault("potions"),
            opts.GetValueOrDefault("hp"), opts.GetValueOrDefault("max-hp"));
        return new CombatSetup(opts.GetValueOrDefault("character", "SILENT"), encounter, act, seed, Loadout: loadout);
    }

    /// <summary>The encounters of one act's room kinds (--act, --rooms), or just --encounter if given.</summary>
    public static (int Act, string Rooms, List<EncounterModel> Pool) Pool(Dictionary<string, string> opts)
    {
        if (opts.TryGetValue("encounter", out var id))
        {
            var encounter = ModelDb.AllEncounters.First(e => e.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase));
            int encounterAct = Enumerable.Range(0, ModelDb.ActsByIndex.Count).FirstOrDefault(i => ModelDb.ActsByIndex[i].Any(a => a.AllEncounters.Contains(encounter)));
            return (encounterAct, encounter.Id.Entry, [encounter]);
        }
        int act = Int(opts, "act", 0);
        var rooms = opts.GetValueOrDefault("rooms", "weak,regular");
        var pool = ModelDb.ActsByIndex[act]
            .SelectMany(a => rooms.Split(',').SelectMany(r => r switch
            {
                "weak" => a.AllWeakEncounters,
                "regular" => a.AllRegularEncounters,
                "elite" => a.AllEliteEncounters,
                "boss" => a.AllBossEncounters,
                _ => throw new ArgumentException("unknown room kind " + r),
            }))
            .Distinct().ToList();
        return (act, rooms, pool);
    }

    /// <summary>A game-style seed string (the game's alphabet, 12 chars), derived deterministically from (seed, index).</summary>
    public static string MakeSeed(int seed, int index)
    {
        const string alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var rng = new Random(unchecked(seed * 1_000_003 + index * 7_919) ^ 0x5eed);
        return string.Concat(Enumerable.Range(0, 12).Select(_ => alphabet[rng.Next(alphabet.Length)]));
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var opts = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException("unexpected argument " + args[i]);
            var key = args[i][2..];
            opts[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        }
        return opts;
    }

    /// <summary>How to start this program again: its apphost, or dotnet with its dll if that is how it was started.</summary>
    public static IReadOnlyList<string> SelfCommand()
    {
        var exe = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(exe) == "dotnet" ? [exe, typeof(Cli).Assembly.Location] : [exe];
    }

    public static int Int(Dictionary<string, string> opts, string key, int fallback) =>
        opts.TryGetValue(key, out var v) ? int.Parse(v) : fallback;

    public static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
