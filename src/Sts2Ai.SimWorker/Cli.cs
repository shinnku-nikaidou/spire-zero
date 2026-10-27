using System.Diagnostics;
using System.Globalization;
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
///   run [--n 1] [--start 0] [--seed 1 | --seeds FILE] [--asc 0] [--env NoSL|SL] [--policy play|random|pimc] [--macro random|l0]
///       [--value v.json] [--worlds 8] [--nodes 300] [--workers W] [--max-decisions 20000] [--check] [--trace] [--obs]
///       [--log decisions.jsonl] [--records runs.jsonl] [--brain SOCKET]
///                                          whole runs through Harness. Combat prompts go to p0; --macro picks the
///                                          brain of every decision outside combat (l0: the macro brain with V, whose
///                                          weights --value reads, the initial ones by default); --check plays
///                                          each seed again over the wire, to a brain in this process, and compares,
///                                          observations, records and decision logs included (each observation must
///                                          survive a JSON round trip), and replays each node from its checkpoint (the
///                                          SL clone, docs: simulator §7); --obs prints them; --log writes the decision
///                                          log; reports results, hashes and surface coverage.
///                                          pimc searches each combat decision in W worker processes (default: one
///                                          per world): NoSL worlds, or in SL the node's clone. --seeds plays the seeds of a seed file, from --start, all
///                                          of them unless --n; --records writes one run record per line (docs: evaluation §6).
///                                          --brain plays against the brain listening at SOCKET instead, which picks the
///                                          champion (--check then replays against it too)
///   brain [--socket ~/.local/state/sts2ai/brain.sock] [--policy ...] [--macro ...] [--value ...] [--worlds 8] [--nodes 300]
///         [--workers W] [--log decisions.jsonl]
///                                          the brain alone: serves the hosts that connect, one at a time (docs: protocol §2)
///   report --seeds FILE --records A.jsonl[,B.jsonl] --out PATH [--previous OLD.json]
///                                          the evaluation report over those records: PATH.json and PATH.md
///   leaderboard [--reports eval/reports] [--out eval/leaderboard.md]
///                                          the leaderboards over every report in the folder (docs: evaluation §2)
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
        if (args.Length == 0) { Console.Error.WriteLine("usage: list | combat --encounter ID | bench | selftest | forkbench | run | brain | panel | serve | report | gate | leaderboard | seeds"); return 2; }
        // These need no game.
        if (args[0] == "report") return Report(ParseOptions(args.Skip(1).ToArray()));
        if (args[0] == "leaderboard") return Leaderboards(ParseOptions(args.Skip(1).ToArray()));
        if (args[0] == "seeds") return Seeds(ParseOptions(args.Skip(1).ToArray()));
        if (args[0] == "gate") return GateCommand(ParseOptions(args.Skip(1).ToArray()));
        if (args[0] == "brain") return Brain(WithChampion(ParseOptions(args.Skip(1).ToArray())));
        // A worker's stdout is the wire: keep it, and send everything else that is printed to stderr from the start.
        TextWriter? wire = null;
        if (args[0] == "serve")
        {
            wire = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(Console.Error);
        }
        var opts = WithChampion(ParseOptions(args.Skip(1).ToArray()));
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
        if (!Enum.TryParse<PlayEnvironment>(opts.GetValueOrDefault("env", "NoSL"), ignoreCase: true, out var env))
            return Fail($"unknown environment {opts["env"]} (NoSL, SL)");
        bool check = opts.ContainsKey("check");
        bool observe = check || opts.ContainsKey("obs");
        using var logFile = opts.TryGetValue("log", out var logPath) ? new StreamWriter(logPath) : null;
        using var records = opts.TryGetValue("records", out var recordsPath) ? new StreamWriter(recordsPath) { AutoFlush = true } : null;
        bool log = check || logFile != null;
        var brainPath = opts.GetValueOrDefault("brain");
        if (brainPath != null && (opts.Keys.Intersect(["policy", "macro", "worlds", "nodes", "workers", "log"]).FirstOrDefault() is { } brainOption))
            return Fail($"--{brainOption} belongs to the brain at {brainPath}, not to this host");
        ChampionOptions? champion = null;
        try
        {
            if (brainPath == null) champion = ChampionOptions.Parse(opts);
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
        using var workerPool = champion?.Pool();
        var pooled = workerPool == null ? null : new PooledPimc(workerPool);
        var harness = champion?.Harness(pooled, log);
        using var remote = brainPath == null ? null : new BrainConnection(brainPath);
        // --check plays each seed again over the wire: to the same brain if it is remote, else to a brain server in
        // this process with the same champion, whose decision log is compared too.
        Dispatcher? served = null;
        using var checkWire = check && remote == null ? BrainConnection.InProcess(new BrainServer(hello => served = harness!(hello))) : null;
        Func<Hello, IRunBrain> firstBrain = remote != null ? hello => WireBrain.Start(remote.Wire, hello) : hello => new LocalBrain(harness!(hello));
        Func<Hello, IRunBrain> secondBrain = hello => WireBrain.Start((remote ?? checkWire)!.Wire, hello);
        var loadout = Loadout.Parse(opts.GetValueOrDefault("deck"), opts.GetValueOrDefault("relics"), opts.GetValueOrDefault("potions"),
            opts.GetValueOrDefault("hp"), opts.GetValueOrDefault("max-hp"));

        var outcomes = new Dictionary<string, int>();
        var surfaces = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        int mismatches = 0, gameErrors = 0, nodes = 0, nodesExact = 0, nodesRelicState = 0;
        var cloneMs = new List<double>();
        Console.WriteLine($"run: {n} runs in {env}, {(champion?.ToString() ?? $"brain at {brainPath}")}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = new RunSetup(opts.GetValueOrDefault("character", "SILENT"), seeds?[i] ?? MakeSeed(seed, i), Int(opts, "asc", 0), loadout);
            var started = clock.Elapsed;
            var first = PlayRun(pump, setup, env, firstBrain, maxDecisions, opts.ContainsKey("trace"), observe, opts.ContainsKey("obs"));
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
                              (observe ? $" obs {Digest(first.Observations.Select(o => o.Full)):x16}" : ""));
            if (first.Failure != null) Console.WriteLine($"  failure: {first.Failure}");
            foreach (var e in first.Errors.Take(3)) Console.WriteLine($"  game error: {FirstLine(e)}");
            if (!check) continue;
            var (clonesExact, clonesRelicState, cloneMismatch) = ClonesReplay(pump, first, cloneMs);
            nodes += first.Nodes.Count;
            nodesExact += clonesExact;
            nodesRelicState += clonesRelicState;
            if (cloneMismatch != null) Console.WriteLine($"  CLONE MISMATCH: {cloneMismatch}");
            var second = PlayRun(pump, setup, env, secondBrain, maxDecisions, trace: false, observe, printObservations: false);
            if (pooled != null) pooled.TakeGameErrors();
            var secondLog = served?.Log ?? second.Log;
            // Each decision's observation is shown before its action, so they pair up by index.
            int diverged = Enumerable.Range(0, Math.Min(first.Trace.Count, second.Trace.Count)).FirstOrDefault(k =>
                first.Trace[k] != second.Trace[k] || first.Observations.ElementAtOrDefault(k) != second.Observations.ElementAtOrDefault(k), -1);
            bool same = first.Trace.Count == second.Trace.Count && first.Summary == second.Summary &&
                first.MapHashes.SequenceEqual(second.MapHashes) && first.Observations.SequenceEqual(second.Observations) &&
                ProtocolJson.Serialize(first.Record) == ProtocolJson.Serialize(second.Record);
            static string Timeless(DecisionEntry e) => ProtocolJson.Serialize(e with { ElapsedMs = 0 });
            bool sameLog = first.Log.Select(Timeless).SequenceEqual(secondLog.Select(Timeless));
            if (diverged < 0 && same && sameLog) continue;
            mismatches++;
            Console.WriteLine(diverged >= 0
                ? first.Trace[diverged] != second.Trace[diverged] ? $"  NOT DETERMINISTIC: decision {diverged}: {first.Trace[diverged]} vs {second.Trace[diverged]}"
                : $"  NOT DETERMINISTIC: observation {diverged} ({first.Trace[diverged]}) differs"
                : !same ? $"  NOT DETERMINISTIC: {first.Trace.Count} vs {second.Trace.Count} decisions; {second.Summary}; {second.Failure}"
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
        if (check) Console.WriteLine($"determinism: {n - mismatches}/{n} runs identical on replay over the wire; " +
                                     $"{nodesExact}/{nodes} nodes replay exactly from their checkpoint" +
                                     (nodesRelicState > 0 ? $", {nodesRelicState} more but for relic state the save drops" : "") +
                                     (cloneMs.Count > 0 ? $" ({cloneMs.Average():F0} ms each, max {cloneMs.Max():F0})" : ""));
        Console.WriteLine($"game Log.Error lines: {gameErrors}" + (workerPool != null ? $"; worker restarts: {workerPool.Restarts}" : ""));
        return failures.Count == 0 && mismatches == 0 && nodesExact + nodesRelicState == nodes && gameErrors == 0 ? 0 : 1;
    }

    /// <summary>
    /// Loads each node of a played run from its checkpoint and replays the node's decisions (docs: simulator §7): the
    /// clone must show the run's observations one for one, and be at its horizon once it has travelled. Returns how
    /// many nodes did, how many did but for the state of relics (fields the save drops, docs: environments §6), and
    /// what went wrong in the first that did neither; each node's load and replay time goes to <paramref name="ms"/>.
    /// </summary>
    private static (int Exact, int RelicState, string? Mismatch) ClonesReplay(Pump pump, RunResult run, List<double> ms)
    {
        int exact = 0, relicState = 0;
        string? mismatch = null;
        for (int k = 0; k < run.Nodes.Count; k++)
        {
            var (save, start) = run.Nodes[k];
            int end = k + 1 < run.Nodes.Count ? run.Nodes[k + 1].Start : run.Trace.Count;
            var seen = new List<(ulong Full, ulong Bare)>();
            bool relicsDiffer = false;
            string? wrong;
            var clock = Stopwatch.StartNew();
            try
            {
                var clone = new NodeClone(save, run.Trace.Skip(start).Take(end - start).ToList());
                using var session = RunSession.Load(pump, clone, Unasked.Instance, Unasked.Instance, Unasked.Instance,
                    obs => seen.Add((ProtocolJson.Hash(obs), ProtocolJson.Hash(WithoutRelicState(obs)))));
                relicsDiffer = Enumerable.Range(0, end - start).Any(i => seen.ElementAtOrDefault(i).Full != run.Observations[start + i].Full);
                int differs = Enumerable.Range(0, end - start).FirstOrDefault(i => seen.ElementAtOrDefault(i).Bare != run.Observations[start + i].Bare, -1);
                bool last = k + 1 == run.Nodes.Count;
                wrong = differs >= 0 ? $"observation {start + differs} ({run.Trace[start + differs]}) differs"
                    : seen.Count != end - start ? $"{seen.Count} observations, not {end - start}"
                    : !last && !session.AtHorizon ? "not at its horizon after travelling"
                    : null;
                if (wrong == null && !last)
                {
                    try
                    {
                        session.Step();
                        wrong = "stepped past its horizon";
                    }
                    catch (InvalidOperationException) { }
                }
            }
            catch (Exception e)
            {
                wrong = $"{e.GetType().Name}: {FirstLine(e.Message)}";
            }
            ms.Add(clock.Elapsed.TotalMilliseconds);
            if (HeadlessGame.TakeErrors() is [var error, ..]) wrong ??= $"game error: {FirstLine(error)}";
            if (wrong != null) mismatch ??= $"node {k} (decisions {start}..{end - 1}): {wrong}";
            else if (relicsDiffer) relicState++;
            else exact++;
        }
        return (exact, relicState, mismatch);
    }

    /// <summary>The observation with each relic shown by its id alone, its counter and status left out.</summary>
    private static Observation WithoutRelicState(Observation obs)
    {
        static IReadOnlyList<RelicView> Ids(IReadOnlyList<RelicView> relics) => [.. relics.Select(r => new RelicView { Id = r.Id })];
        return obs with
        {
            Run = obs.Run with { Relics = Ids(obs.Run.Relics) },
            Screen = obs.Screen switch
            {
                CombatScreen screen => screen with { Combat = screen.Combat with { Relics = Ids(screen.Combat.Relics) } },
                CardChoiceScreen { Combat: { } combat } screen => screen with { Combat = combat with { Relics = Ids(combat.Relics) } },
                _ => obs.Screen,
            },
        };
    }

    /// <summary>Policies for a clone that replays everything: asking one means the recording ran out.</summary>
    private sealed class Unasked : ICombatPolicy, IChoicePolicy, IMacroPolicy
    {
        public static readonly Unasked Instance = new();

        public CombatAction Choose(CombatDecision decision) => throw Exhausted();

        IReadOnlyList<int> IChoicePolicy.Choose(ChoiceDecision decision) => throw Exhausted();

        public string Choose(MacroDecision decision) => throw Exhausted();

        private static InvalidOperationException Exhausted() => new("asked beyond the recording");
    }

    /// <summary>
    /// The brain alone (docs: protocol §2): serves the hosts that connect to the socket, one at a time, with the champion
    /// --policy and --macro name. Prints a line per run, with the game errors of its search workers.
    /// </summary>
    private static int Brain(Dictionary<string, string> opts)
    {
        var path = opts.GetValueOrDefault("socket") ?? BrainSocket.DefaultPath;
        ChampionOptions champion;
        try
        {
            champion = ChampionOptions.Parse(opts);
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
        using var workerPool = champion.Pool();
        var pooled = workerPool == null ? null : new PooledPimc(workerPool);
        using var logFile = opts.TryGetValue("log", out var logPath) ? new StreamWriter(logPath) { AutoFlush = true } : null;
        var server = new BrainServer(champion.Harness(pooled, logFile != null), (hello, harness, end) =>
        {
            foreach (var entry in harness.Log) logFile?.WriteLine(ProtocolJson.Serialize(entry));
            var errors = pooled?.TakeGameErrors() ?? [];
            Console.WriteLine($"{hello.Run.RunId} ({hello.Role}) {end.Reason} floor {end.Floor}, {harness.Brains.Values.Sum()} decisions" +
                              (errors.Count > 0 ? $", game Log.Error lines in workers: {errors.Count}" : ""));
            foreach (var e in errors.Take(3)) Console.WriteLine($"  game error: {FirstLine(e)}");
        });
        Console.WriteLine($"brain: {champion}, listening on {path}");
        server.Listen(path);
        return 0;
    }

    /// <summary>The champion that --policy and --macro name (docs: harness §4), pimc's budget, and l0's V (--value).</summary>
    /// <param name="ValueFile">Where <paramref name="Value"/> came from; null for the initial weights.</param>
    private sealed record ChampionOptions(string Policy, string Macro, Budget Budget, int Workers, RunValue Value, string? ValueFile)
    {
        public static ChampionOptions Parse(Dictionary<string, string> opts)
        {
            var policy = opts.GetValueOrDefault("policy", "play");
            if (policy != "pimc" && !Policies.CombatNames.Contains(policy))
                throw new ArgumentException($"unknown policy {policy} (pimc, {string.Join(", ", Policies.CombatNames)})");
            var macro = opts.GetValueOrDefault("macro", "random");
            if (macro is not ("random" or "l0")) throw new ArgumentException($"unknown macro brain {macro} (random, l0)");
            var budget = new Budget(Int(opts, "worlds", 8), Int(opts, "nodes", 300));
            var value = RunValue.Initial;
            var valueFile = opts.GetValueOrDefault("value");
            if (valueFile != null)
            {
                if (macro != "l0") throw new ArgumentException("--value weighs l0's V; it needs --macro l0");
                value = RunValue.From(ProtocolJson.Deserialize<ValueWeights>(File.ReadAllText(valueFile)));
            }
            return new(policy, macro, budget, Int(opts, "workers", budget.Worlds), value, valueFile);
        }

        /// <summary>pimc's search workers. A run holds its process's one live game, so search runs in other processes.</summary>
        public WorkerPool? Pool() => Policy == "pimc" ? new WorkerPool(SelfCommand(), Workers) : null;

        /// <summary>
        /// One Harness per run. Combat prompts go to p0 (<see cref="PromptBrain"/>), which search worlds ask too.
        /// Placeholders draw from one generator seeded by the run id, so a run plays the same whatever is logged, in
        /// this process or over the wire. In SL, pimc searches the node's clone; nothing else looks further than in NoSL.
        /// </summary>
        public Func<Hello, Dispatcher> Harness(PooledPimc? pooled, bool log) => hello =>
        {
            if (!Enum.TryParse<PlayEnvironment>(hello.Env, out var env)) throw new NotSupportedException($"no environment {hello.Env}");
            var rng = new Random(unchecked((int)ProtocolJson.Hash(hello.Run.RunId)));
            var combat = pooled?.Route(Budget) ?? CombatRoute.Of(Policy, Policies.Combat(Policy, rng));
            var prompts = new Route<IChoicePolicy>("p0", PromptBrain.Instance);
            Dispatcher dispatcher;
            if (Macro == "l0")
            {
                var l0 = new MacroBrain(Value);
                dispatcher = new Dispatcher(hello.Run.RunId, env, combat, prompts, new(L0, l0), new(L0, l0), log);
            }
            else dispatcher = new Dispatcher(hello.Run.RunId, env, combat, prompts, new("random", new RandomMacro(rng)), new("random", new RandomChoices(rng)), log);
            if (dispatcher.Champion != Name) throw new InvalidOperationException($"champion {dispatcher.Champion} goes by {Name} in champion files");
            return dispatcher;
        };

        /// <summary>l0 by its V's name, unless V has the initial weights.</summary>
        private string L0 => Value == RunValue.Initial ? "l0" : $"l0 {Value.Name}";

        /// <summary>The champion as Harness names it (<see cref="Dispatcher.Champion"/>), which runs check.</summary>
        public string Name
        {
            get
            {
                string macro = Macro == "l0" ? L0 : "random";
                return $"combat {Policy}{(Policy == "pimc" ? $" {Budget.Worlds}x{Budget.NodesPerWorld}" : "")}, combat prompts p0, macro {macro}, macro prompts {macro}";
            }
        }

        /// <summary>The options that play this champion, for a champion file.</summary>
        public Dictionary<string, string> Options(PlayEnvironment env)
        {
            var options = new Dictionary<string, string> { ["env"] = env.ToString(), ["policy"] = Policy, ["macro"] = Macro };
            if (Policy == "pimc") { options["worlds"] = $"{Budget.Worlds}"; options["nodes"] = $"{Budget.NodesPerWorld}"; }
            if (ValueFile != null) options["value"] = ValueFile;
            return options;
        }

        public override string ToString() =>
            $"policy {Policy}{(Policy == "pimc" ? $" worlds {Budget.Worlds} nodes {Budget.NodesPerWorld} on {Workers} workers" : "")}, macro {(Macro == "l0" ? L0 : Macro)}";
    }

    /// <param name="Observations">Per decision, its observation's hash, and the hash without relic state (when observing).</param>
    /// <param name="Log">Harness's decision log, when logging in this process.</param>
    /// <param name="Record">The run's record, its game errors and time still to fill in.</param>
    /// <param name="Nodes">Each node's checkpoint and where its decisions begin in the trace (when observing).</param>
    private sealed record RunResult(string Outcome, string Summary, IReadOnlyList<string> Trace, IReadOnlyList<ulong> MapHashes,
        IReadOnlyList<(ulong Full, ulong Bare)> Observations, IReadOnlyList<DecisionEntry> Log, string? Failure, List<string> Errors, RunRecord Record,
        IReadOnlyList<(string Save, int Start)> Nodes);

    /// <summary>
    /// One run to the end (or the decision cap), against the brain that <paramref name="brains"/> gives for the run's
    /// hello: its id is a hash of the seed, so the seed alone gives the same run.
    /// Observing checks that each observation survives a JSON round trip, and keeps its hash.
    /// </summary>
    private static RunResult PlayRun(Pump pump, RunSetup setup, PlayEnvironment env, Func<Hello, IRunBrain> brains, int maxDecisions, bool trace,
        bool observe, bool printObservations)
    {
        var hello = new Hello(HeadlessGame.GameVersion, env.ToString(), "worker",
            new RunHeader(RunHeader.IdOf(setup.Seed), setup.Character, setup.Ascension));
        IRunBrain? brain = null;
        var observations = new List<(ulong Full, ulong Bare)>();
        var nodes = new List<(string Save, int Start)>();
        RunSession? session = null;
        RunResult result;
        try
        {
            brain = brains(hello);
            var export = env == PlayEnvironment.SL ? new NodeExport(brain) : null;
            Action<Observation>? observed = !observe ? null : obs =>
            {
                var json = ProtocolJson.Serialize(obs);
                if (ProtocolJson.Serialize(ProtocolJson.Deserialize<Observation>(json)) != json) throw new InvalidOperationException($"observation does not round-trip: {json}");
                if (printObservations) Console.WriteLine($"  obs {observations.Count}: {json}");
                observations.Add((ProtocolJson.Hash(obs), ProtocolJson.Hash(WithoutRelicState(obs))));
            };
            IRunBrain player = export ?? brain;
            session = RunSession.Start(pump, setup, player, player, player, observed);
            if (export != null) export.Session = session;
            int printed = 0;
            while (!session.IsOver && session.Trace.Count < maxDecisions)
            {
                if (observe && (nodes.Count == 0 || nodes[^1].Start != session.NodeStart)) nodes.Add((session.Clone().Save, session.NodeStart));
                session.Step();
                if (!trace) continue;
                for (; printed < session.Trace.Count; printed++) Console.WriteLine($"  {printed}: {session.Trace[printed]}");
            }
            result = Result(session.Won ? "won" : session.IsOver ? "lost" : "truncated", null);
        }
        catch (Exception e)
        {
            if (HeadlessGame.Verbose) Console.Error.WriteLine(e);
            result = Result("failed", $"{e.GetType().Name}: {FirstLine(e.Message)}");
        }
        finally
        {
            session?.Dispose();
        }
        brain?.End(new RunEnd(result.Outcome == "won", result.Record.Floor, result.Outcome), result.Failure);
        return result;

        RunResult Result(string outcome, string? failure)
        {
            var steps = session?.Trace.ToList() ?? [];
            var surfaces = steps.GroupBy(s => s[..s.IndexOf(' ')]).ToDictionary(g => g.Key, g => g.Count());
            var config = new RunConfig(HeadlessGame.GameVersion, Commit, brain?.Champion ?? "none", hello.Env, "R1");
            var player = session?.Player;
            var record = new RunRecord(config, setup.Seed, setup.Character, setup.Ascension, outcome,
                session?.Run.CurrentActIndex ?? 0, session?.Run.TotalFloor ?? 0, player?.Creature.CurrentHp ?? 0, player?.Creature.MaxHp ?? 0,
                player?.Gold ?? 0, session?.Combats.ToList() ?? [], surfaces, new Dictionary<string, int>(brain?.Brains ?? new Dictionary<string, int>()),
                session?.CardRewards ?? 0, session?.CardRewardsSkipped ?? 0, 0, failure, 0, hello.Run.RunId);
            return new(outcome, session?.Summary() ?? "", steps, session?.MapHashes.ToList() ?? [], observations, brain?.Log.ToList() ?? [], failure,
                HeadlessGame.TakeErrors(), record, nodes);
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

    /// <summary>
    /// The full-run gate (docs: evaluation §5): the challenger's records against the champion's report, paired by seed,
    /// with an SPRT. The challenger is named by its run options, which must be the ones its records were played with.
    /// On a pass, --promote PATH writes the challenger's report to PATH and makes it the champion; its report faces the
    /// next challengers, so it must cover the whole seed set. The SPRT may pass early: play the rest, then promote.
    /// </summary>
    private static int GateCommand(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("champion", out var championPath) || !opts.TryGetValue("records", out var recordFiles))
            return Fail("usage: gate --champion eval/champions/ENV.json --records A.jsonl[,B.jsonl] [--metric floor|win] [--delta D] " +
                        "[--alpha 0.05] [--beta 0.05] [--promote PATH] --policy ... [--macro ...] [--value ...] [--worlds ...] [--nodes ...]");
        try
        {
            var file = ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(championPath));
            var champion = ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(file.Report));
            var runs = recordFiles.Split(',').SelectMany(File.ReadAllLines).Where(l => l.Length > 0).Select(ProtocolJson.Deserialize<RunRecord>).ToList();
            var challenger = ChampionOptions.Parse(opts);
            if (runs.FirstOrDefault(r => r.Config.Champion != challenger.Name) is { } other)
                return Fail($"run {other.Seed} was played by {other.Config.Champion}, not by the options given ({challenger.Name})");
            var metric = opts.GetValueOrDefault("metric", "floor");
            double delta = opts.TryGetValue("delta", out var d) ? double.Parse(d, CultureInfo.InvariantCulture) : metric == "floor" ? 1.5 : 0.1;
            double Real(string key, double fallback) => opts.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
            var result = Gate.Sprt(Gate.Pairs(champion, runs), metric, delta, Real("alpha", 0.05), Real("beta", 0.05));
            Console.WriteLine($"champion: {file.Champion} ({file.Report})");
            Console.WriteLine($"challenger: {challenger.Name}, {runs.Count} runs");
            Console.WriteLine(result.Summary());
            if (result.Verdict != "pass" || !opts.TryGetValue("promote", out var outPath)) return result.Verdict == "pass" ? 0 : 1;

            var seedFile = Path.Combine("eval", "seeds", champion.SeedSet + ".txt");
            var report = EvalReport.Of(runs, champion.SeedSet, EvalReport.ReadSeeds(seedFile), DateTime.Now.ToString("yyyy-MM-dd"));
            if (report.Runs.Count != report.SetSize)
                return Fail($"not promoted: {report.Runs.Count} of {champion.SeedSet}'s {report.SetSize} seeds were run; the champion's report needs them all");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath + ".json", report.Json());
            File.WriteAllText(outPath + ".md", report.Markdown(champion));
            var env = Enum.Parse<PlayEnvironment>(file.Env);
            var promoted = new ChampionFile(file.Env, challenger.Name, challenger.Options(env), outPath + ".json", report.Date, result);
            File.WriteAllText(championPath, ProtocolJson.Serialize(promoted) + "\n");
            Console.WriteLine($"promoted: {championPath} now names {challenger.Name}");
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or IOException or JsonException)
        {
            return Fail(e.Message);
        }
    }

    private static int Leaderboards(Dictionary<string, string> opts)
    {
        var reports = Directory.GetFiles(opts.GetValueOrDefault("reports", "eval/reports"), "*.json").Order()
            .Select(path => ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(path))).ToList();
        var markdown = Leaderboard.Markdown(reports);
        File.WriteAllText(opts.GetValueOrDefault("out", "eval/leaderboard.md"), markdown);
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
    private static ulong Digest(IEnumerable<ulong> hashes)
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

    /// <summary>--champion FILE: the champion file's options, under the ones given (docs: evaluation §5).</summary>
    private static Dictionary<string, string> WithChampion(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("champion", out var path)) return opts;
        var file = ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(path));
        foreach (var (key, value) in file.Options) opts.TryAdd(key, value);
        return opts;
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
