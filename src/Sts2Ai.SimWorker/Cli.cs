using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Brain;
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
///   selftest [--n 20] [--seed 1] [--act 0] [--rooms weak,regular]
///                                          checks determinism, restore and determinize over an encounter pool
///   forkbench [--n 30] [--seed 1] [--act 0] [--rooms weak,regular] [--branches 20]
///                                          cost of restoring a combat and of one search branch
///   run [--n 1] [--start 0] [--seed 1] [--asc 0] [--policy play] [--max-decisions 20000] [--check] [--trace] [--obs]
///                                          whole runs, every non-combat decision random; --check plays each seed
///                                          twice and compares, observations included (each one must survive a JSON
///                                          round trip); --obs prints them; reports results, hashes and surface coverage
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
        if (args.Length == 0) { Console.Error.WriteLine("usage: list | combat --encounter ID | bench | selftest | forkbench | run"); return 2; }
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
        int n = Int(opts, "n", 1);
        int start = Int(opts, "start", 0);
        int seed = Int(opts, "seed", 1);
        int maxDecisions = Int(opts, "max-decisions", 20_000);
        bool check = opts.ContainsKey("check");
        bool observe = check || opts.ContainsKey("obs");
        var policy = opts.GetValueOrDefault("policy", "play");
        if (!Policies.CombatNames.Contains(policy)) return Fail($"unknown policy {policy} (one of {string.Join(", ", Policies.CombatNames)})");
        var loadout = Loadout.Parse(opts.GetValueOrDefault("deck"), opts.GetValueOrDefault("relics"), opts.GetValueOrDefault("potions"),
            opts.GetValueOrDefault("hp"), opts.GetValueOrDefault("max-hp"));

        var outcomes = new Dictionary<string, int>();
        var surfaces = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        int mismatches = 0, gameErrors = 0;
        Console.WriteLine($"run: {n} runs, policy {policy}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = new RunSetup(opts.GetValueOrDefault("character", "SILENT"), MakeSeed(seed, i), Int(opts, "asc", 0), loadout);
            var started = clock.Elapsed;
            var first = PlayRun(pump, setup, seed * 1_000_003 + i, policy, maxDecisions, opts.ContainsKey("trace"), observe, opts.ContainsKey("obs"));
            var ms = (clock.Elapsed - started).TotalMilliseconds;
            outcomes[first.Outcome] = outcomes.GetValueOrDefault(first.Outcome) + 1;
            foreach (var step in first.Trace)
            {
                var surface = step[..step.IndexOf(' ')];
                surfaces[surface] = surfaces.GetValueOrDefault(surface) + 1;
            }
            if (first.Failure != null) failures[first.Failure] = failures.GetValueOrDefault(first.Failure) + 1;
            gameErrors += first.Errors.Count;
            Console.WriteLine($"#{i} {setup.Seed} {first.Outcome} {first.Summary} decisions {first.Trace.Count} {ms:F0} ms " +
                              $"maps {string.Join(",", first.MapHashes.Select(h => h.ToString("x16")))}" +
                              (observe ? $" obs {Digest(first.Observations):x16}" : ""));
            if (first.Failure != null) Console.WriteLine($"  failure: {first.Failure}");
            foreach (var e in first.Errors.Take(3)) Console.WriteLine($"  game error: {FirstLine(e)}");
            if (!check) continue;
            var second = PlayRun(pump, setup, seed * 1_000_003 + i, policy, maxDecisions, trace: false, observe, printObservations: false);
            // Each decision's observation is shown before its action, so they pair up by index.
            int diverged = Enumerable.Range(0, Math.Min(first.Trace.Count, second.Trace.Count)).FirstOrDefault(k =>
                first.Trace[k] != second.Trace[k] || first.Observations.ElementAtOrDefault(k) != second.Observations.ElementAtOrDefault(k), -1);
            if (diverged < 0 && first.Trace.Count == second.Trace.Count && first.Summary == second.Summary &&
                first.MapHashes.SequenceEqual(second.MapHashes) && first.Observations.SequenceEqual(second.Observations)) continue;
            mismatches++;
            Console.WriteLine(diverged < 0 ? $"  NOT DETERMINISTIC: {first.Trace.Count} vs {second.Trace.Count} decisions; {second.Summary}"
                : first.Trace[diverged] != second.Trace[diverged] ? $"  NOT DETERMINISTIC: decision {diverged}: {first.Trace[diverged]} vs {second.Trace[diverged]}"
                : $"  NOT DETERMINISTIC: observation {diverged} ({first.Trace[diverged]}) differs");
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
        Console.WriteLine($"game Log.Error lines: {gameErrors}");
        return failures.Count == 0 && mismatches == 0 && gameErrors == 0 ? 0 : 1;
    }

    /// <param name="Observations">Per decision, its observation's hash (when observing).</param>
    private sealed record RunResult(string Outcome, string Summary, IReadOnlyList<string> Trace, IReadOnlyList<ulong> MapHashes,
        IReadOnlyList<ulong> Observations, string? Failure, List<string> Errors);

    /// <summary>
    /// One run to the end (or the decision cap), every policy drawing from one generator seeded by <paramref name="rngSeed"/>.
    /// Observing checks that each observation survives a JSON round trip, and keeps its hash.
    /// </summary>
    private static RunResult PlayRun(Pump pump, RunSetup setup, int rngSeed, string policy, int maxDecisions, bool trace, bool observe, bool printObservations)
    {
        var rng = new Random(rngSeed);
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
            session = RunSession.Start(pump, setup, new RandomChoices(rng), Policies.Combat(policy, rng), new RandomMacro(rng), observed);
            int printed = 0;
            while (!session.IsOver && session.Trace.Count < maxDecisions)
            {
                session.Step();
                if (!trace) continue;
                for (; printed < session.Trace.Count; printed++) Console.WriteLine($"  {printed}: {session.Trace[printed]}");
            }
            var outcome = session.Won ? "won" : session.IsOver ? "lost" : "truncated";
            return new(outcome, session.Summary(), session.Trace.ToList(), session.MapHashes.ToList(), observations, null, HeadlessGame.TakeErrors());
        }
        catch (Exception e)
        {
            if (HeadlessGame.Verbose) Console.Error.WriteLine(e);
            return new("failed", session?.Summary() ?? "", session?.Trace.ToList() ?? [], session?.MapHashes.ToList() ?? [], observations,
                $"{e.GetType().Name}: {FirstLine(e.Message)}", HeadlessGame.TakeErrors());
        }
        finally
        {
            session?.Dispose();
        }
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
