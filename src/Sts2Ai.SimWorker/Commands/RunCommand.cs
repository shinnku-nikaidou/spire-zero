using System.CommandLine;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// run: whole runs through Harness. --policy picks the brain of every decision of a combat, its card prompts among
/// them; --macro picks the brain of every decision outside combat. plan searches in the engine.
/// --check plays each seed again over the wire, to a brain in this process, and compares: observations, records and
/// decision logs included (each observation must survive a JSON round trip); and replays each node from its
/// checkpoint (the SL clone), and from each save the game made inside a finished room, which the game's Continue loads.
/// Reports results, hashes and surface coverage.
/// </summary>
internal static class RunCommand
{
    private static readonly Option<string?> Seeds = Opt.Maybe<string?>("--seeds", "a seed file: plays its seeds from --start, all of them unless --n");
    private static readonly Option<int?> N = Opt.Maybe<int?>("--n", "runs (default: 1, or the rest of --seeds)");
    private static readonly Option<int> Start = Opt.Of("--start", 0, "the first run's index");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base, without --seeds");
    private static readonly Option<int> Asc = Opt.Of("--asc", 0, "the ascension");
    private static readonly Option<PlayEnvironment> Env = Opt.Of("--env", PlayEnvironment.NoSL, "the environment");
    private static readonly Option<int> MaxDecisions = Opt.Of("--max-decisions", 20_000, "a run stops here, truncated");
    private static readonly Option<bool> Check = Opt.Of("--check", false, "plays each seed again over the wire and compares, and replays each node from its checkpoint and from each save in a finished room");
    private static readonly Option<bool> Trace = Opt.Of("--trace", false, "prints every decision");
    private static readonly Option<bool> Obs = Opt.Of("--obs", false, "prints every observation");
    private static readonly Option<string?> Log = Opt.Maybe<string?>("--log", "writes Harness's decision log, one JSON line per decision");
    private static readonly Option<string?> Records = Opt.Maybe<string?>("--records", "writes one run record per line");
    private static readonly Option<bool> EventLines = Opt.Of("--event-lines", false,
        "NoSL self-play with --log (pimc, l0): each event option whose result its button does not show is also played out on the node's clone " +
        "as NoSL plays it, for V's training; the answers never read the clone (plans/2026-11-18-1340-nosl-v0.md, 3.3)");
    private static readonly Option<string?> AutoWin = Opt.Maybe<string?>("--auto-win",
        "acts, numbered from 1 as the game shows them, such as 1,2: their plain monster fights, an event's included, are won at their start " +
        "(the game's win command) with their rewards, so only elites and bosses are fought there; for self-play, as no report or gate takes such a run");
    private static readonly Option<string?> Mode = Opt.Maybe<string?>("--mode",
        "the game mode: dread, which the game plays at ascension 10 only (--asc 10); the standard game if not given");
    private static readonly ChampionArgs Champion = new(file: true);
    private static readonly LoadoutArgs Loadout = new();

    public static Command Command()
    {
        var command = Opt.Command("run", "whole runs through Harness",
            [Seeds, N, Start, Seed, Asc, Env, MaxDecisions, Check, Trace, Obs, Log, Records, EventLines, AutoWin, Mode], Champion.Options, Loadout.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        var seedFile = r.GetValue(Seeds);
        var seeds = seedFile != null ? EvalReport.ReadSeeds(seedFile) : null;
        int start = r.GetValue(Start);
        int n = r.GetValue(N) ?? (seeds == null ? 1 : seeds.Count - start);
        if (seeds != null && start + n > seeds.Count) throw new CliException($"{seedFile} has {seeds.Count} seeds, not {start + n}");
        int seed = r.GetValue(Seed);
        int asc = r.GetValue(Asc);
        var autoWin = Acts(r.GetValue(AutoWin));
        var mode = GameModeOf(r.GetValue(Mode), asc);
        int maxDecisions = r.GetValue(MaxDecisions);
        var env = r.GetValue(Env);
        bool check = r.GetValue(Check);
        bool trace = r.GetValue(Trace);
        bool printObservations = r.GetValue(Obs);
        bool observe = check || printObservations;
        // Both flush each line, so a run stopped midway (self-play, mostly) loses at most the line being written.
        using var logFile = r.GetValue(Log) is { } logPath ? new StreamWriter(logPath) { AutoFlush = true } : null;
        using var records = r.GetValue(Records) is { } recordsPath ? new StreamWriter(recordsPath) { AutoFlush = true } : null;
        bool log = check || logFile != null;
        bool eventLines = r.GetValue(EventLines);
        var champion = Champion.Read(r);
        if (champion.Policy == "plan" && env != PlayEnvironment.SL) throw new CliException("plan plays in SL only (--env SL)");
        if (eventLines && (env != PlayEnvironment.NoSL || !champion.LogsEventLines || !log))
            throw new CliException("--event-lines is NoSL self-play's (--env NoSL, pimc and l0) and goes with --log or --check");
        using var workerPool = champion.Pool(eventLines);
        var harness = champion.Harness(workerPool, log, eventLines: eventLines);
        // --check plays each seed again over the wire, to a brain server in this process with the same champion, whose
        // decision log is compared too.
        Dispatcher? served = null;
        using var checkWire = check ? BrainConnection.InProcess(new BrainServer(hello => (served = harness(hello), champion.Shown))) : null;
        Func<Hello, IRunBrain> firstBrain = hello => new LocalBrain(harness(hello));
        // The brain over the wire is the same champion's, which takes nodes as the one here does.
        IRunBrain secondBrain(Hello hello) => WireBrain.Start(checkWire!.Wire, hello, nodes: env == PlayEnvironment.SL || eventLines);
        string character = Loadout.Character(r);
        var loadout = Loadout.Loadout(r);


        var outcomes = new Dictionary<string, int>();
        var surfaces = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        int mismatches = 0, gameErrors = 0, nodes = 0, nodesExact = 0, replans = 0;
        // Saves inside a finished room, by kind: how many, and how many of their clones replayed exactly.
        var roomSaves = new SortedDictionary<string, (int All, int Exact)>();
        var cloneMs = new List<double>();
        Console.WriteLine($"run: {n} runs in {env}, {champion}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = new RunSetup(character, seeds?[i] ?? GameSeeds.MakeSeed(seed, i), asc, loadout, autoWin, mode);
            var started = clock.Elapsed;
            var first = PlayRun(pump, setup, env, firstBrain, maxDecisions, trace, observe, printObservations);
            if (workerPool != null) first.Errors.AddRange(workerPool.TakeGameErrors());
            foreach (var entry in first.Log) logFile?.WriteLine(ProtocolJson.Serialize(entry));
            var ms = (clock.Elapsed - started).TotalMilliseconds;
            records?.WriteLine(ProtocolJson.Serialize(first.Record with { GameErrors = first.Errors.Count, ElapsedMs = ms }));
            outcomes[first.Outcome] = outcomes.GetValueOrDefault(first.Outcome) + 1;
            foreach (var (surface, count) in first.Record.Surfaces) surfaces[surface] = surfaces.GetValueOrDefault(surface) + count;
            if (first.Failure != null) failures[first.Failure] = failures.GetValueOrDefault(first.Failure) + 1;
            gameErrors += first.Errors.Count;
            replans += first.Replans;
            Console.WriteLine($"#{i} {setup.Seed} {first.Outcome} {first.Summary} decisions {first.Trace.Count} {ms:F0} ms " +
                              (first.Replans > 0 ? $"replans {first.Replans} " : "") +
                              $"maps {string.Join(",", first.MapHashes.Select(h => h.ToString("x16")))}" +
                              (observe ? $" obs {Digest(first.Observations):x16}" : ""));
            if (first.Failure != null) Console.WriteLine($"  failure: {first.Failure}");
            foreach (var e in first.Errors.Take(3)) Console.WriteLine($"  game error: {Logs.FirstLine(e)}");
            if (!check) continue;
            foreach (var (kind, wrong) in ClonesReplay(pump, first, autoWin, cloneMs))
            {
                if (kind == Arrival) (nodes, nodesExact) = (nodes + 1, nodesExact + (wrong == null ? 1 : 0));
                else roomSaves[kind] = (roomSaves.GetValueOrDefault(kind).All + 1, roomSaves.GetValueOrDefault(kind).Exact + (wrong == null ? 1 : 0));
                if (wrong != null) Console.WriteLine($"  CLONE MISMATCH: {wrong}");
            }
            var second = PlayRun(pump, setup, env, secondBrain, maxDecisions, trace: false, observe, printObservations: false);
            workerPool?.TakeGameErrors();
            var secondLog = served!.Log;
            // Each decision's observation is shown before its action, so they pair up by index.
            int diverged = Enumerable.Range(0, Math.Min(first.Trace.Count, second.Trace.Count)).FirstOrDefault(k =>
                first.Trace[k] != second.Trace[k] || first.Observations.ElementAtOrDefault(k) != second.Observations.ElementAtOrDefault(k), -1);
            bool same = first.Trace.Count == second.Trace.Count && first.Summary == second.Summary &&
                first.MapHashes.SequenceEqual(second.MapHashes) && first.Observations.SequenceEqual(second.Observations) &&
                ProtocolJson.Serialize(first.Record with { Search = null }) == ProtocolJson.Serialize(second.Record);
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
                                     $"{nodesExact}/{nodes} nodes replay exactly from their checkpoint; saves in a finished room replay exactly: " +
                                     (roomSaves.Count == 0 ? "none made" : string.Join(", ", roomSaves.Select(kv => $"{kv.Key} {kv.Value.Exact}/{kv.Value.All}"))) +
                                     (cloneMs.Count > 0 ? $" ({cloneMs.Average():F0} ms each, max {cloneMs.Max():F0})" : ""));
        Console.WriteLine($"game Log.Error lines: {gameErrors}" + (workerPool != null ? $"; worker restarts: {workerPool.Restarts}" : "") +
                          (champion.Policy == "plan" ? $"; replans: {replans}" : ""));
        return failures.Count == 0 && mismatches == 0 && nodesExact == nodes && roomSaves.Values.All(v => v.Exact == v.All) && gameErrors == 0 ? 0 : 1;
    }

    /// <summary>The kind of a node's checkpoint taken on arrival, beside the kinds of saves in a finished room.</summary>
    private const string Arrival = "arrival";

    /// <summary>
    /// Loads each checkpoint of a played run and replays the decisions from it to its node's end: each node's, taken on
    /// arrival, and each save the game made inside a finished room (after a won fight, after an ancient), which loads as
    /// the game's Continue loads it. The clone must show the run's observations one for one, and be at its horizon once it
    /// has travelled. Returns each checkpoint's kind and what went wrong with it, or null; each load and replay time goes
    /// to <paramref name="ms"/>. The clones keep the run's rule, <paramref name="autoWin"/>.
    /// </summary>
    private static List<(string Kind, string? Wrong)> ClonesReplay(Pump pump, RunResult run, IReadOnlyList<int>? autoWin, List<double> ms)
    {
        var starts = run.Nodes.Select(n => n.Start).ToList();
        int EndOf(int at) => starts.FirstOrDefault(s => s > at, run.Trace.Count);
        var checkpoints = run.Nodes.Select((n, k) => (n.Save, n.Start, Kind: Arrival, Where: $"node {k}"))
            .Concat(run.RoomSaves.Select(r => (r.Save, r.Start, r.Kind, Where: $"{r.Kind} save in node {starts.Count(s => s <= r.Start) - 1}")));
        var results = new List<(string Kind, string? Wrong)>();
        foreach (var (save, start, kind, where) in checkpoints)
        {
            int end = EndOf(start);
            bool last = end == run.Trace.Count;
            var seen = new List<ulong>();
            string? wrong;
            var clock = Stopwatch.StartNew();
            try
            {
                var clone = new NodeClone(save, [.. run.Trace.Skip(start).Take(end - start)], autoWin);
                using var session = RunSession.Load(pump, clone, Unasked.Instance, Unasked.Instance, Unasked.Instance,
                    obs => seen.Add(ProtocolJson.Hash(obs)));
                int differs = Enumerable.Range(0, end - start).FirstOrDefault(i => seen.ElementAtOrDefault(i) != run.Observations[start + i], -1);
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
                wrong = $"{e.GetType().Name}: {Logs.FirstLine(e.Message)}";
            }
            ms.Add(clock.Elapsed.TotalMilliseconds);
            if (HeadlessGame.TakeErrors() is [var error, ..]) wrong ??= $"game error: {Logs.FirstLine(error)}";
            results.Add((kind, wrong == null ? null : $"{where} (decisions {start}..{end - 1}): {wrong}"));
        }
        return results;
    }

    /// <summary>Policies for a clone that replays everything: asking one means the recording ran out.</summary>
    internal sealed class Unasked : ICombatPolicy, IChoicePolicy, IMacroPolicy
    {
        public static readonly Unasked Instance = new();

        public CombatAction Choose(CombatDecision decision) => throw Exhausted();

        ChoiceAnswer IChoicePolicy.Choose(ChoiceDecision decision) => throw Exhausted();

        public MacroAnswer Choose(MacroDecision decision) => throw Exhausted();

        private static InvalidOperationException Exhausted() => new("asked beyond the recording");
    }

    /// <param name="Observations">Per decision, its observation's hash (when observing).</param>
    /// <param name="Log">Harness's decision log, when logging in this process.</param>
    /// <param name="Record">The run's record, its game errors and time still to fill in.</param>
    /// <param name="Nodes">Each node's checkpoint and where its decisions begin in the trace (when observing).</param>
    /// <param name="RoomSaves">Each save inside a finished room as a checkpoint, where the decisions after it begin, and
    /// the room's kind (when observing).</param>
    private sealed record RunResult(string Outcome, string Summary, IReadOnlyList<string> Trace, IReadOnlyList<ulong> MapHashes,
        IReadOnlyList<ulong> Observations, IReadOnlyList<DecisionEntry> Log, string? Failure, List<string> Errors, RunRecord Record,
        IReadOnlyList<(string Save, int Start)> Nodes, IReadOnlyList<(string Save, int Start, string Kind)> RoomSaves, int Replans);

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
        var observations = new List<ulong>();
        var nodes = new List<(string Save, int Start)>();
        var roomSaves = new List<(string Save, int Start, string Kind)>();
        RunSession? session = null;
        RunResult result;
        try
        {
            brain = brains(hello);
            var export = brain.TakesNodes ? new NodeExport(brain) : null;
            Action<Observation>? observed = !observe ? null : obs =>
            {
                var json = ProtocolJson.Serialize(obs);
                if (ProtocolJson.Serialize(ProtocolJson.Deserialize<Observation>(json)) != json) throw new InvalidOperationException($"observation does not round-trip: {json}");
                if (printObservations) Console.WriteLine($"  obs {observations.Count}: {json}");
                observations.Add(ProtocolJson.Hash(obs));
            };
            IRunBrain player = export ?? brain;
            session = RunSession.Start(pump, setup, player, player, player, observed);
            if (export != null) export.Session = session;
            if (observe) session.RoomSaved = (save, at, kind) => roomSaves.Add((save, at, kind));
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
            Logs.Sim.LogError(e, "run {Seed} failed", setup.Seed);
            result = Result("failed", $"{e.GetType().Name}: {Logs.FirstLine(e.Message)}");
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
            var config = new RunConfig(HeadlessGame.GameVersion, Program.Commit, brain?.Champion ?? "none", hello.Env, setup.AutoWin, Observe.Mode(setup.Mode));
            var player = session?.Player;
            var record = new RunRecord(config, setup.Seed, setup.Character, setup.Ascension, outcome,
                session?.Run.CurrentActIndex ?? 0, session?.Run.TotalFloor ?? 0, player?.Creature.CurrentHp ?? 0, player?.Creature.MaxHp ?? 0,
                player?.Gold ?? 0, session?.Combats.ToList() ?? [], surfaces, new Dictionary<string, int>(brain?.Brains ?? new Dictionary<string, int>()),
                session?.CardRewards ?? 0, session?.CardRewardsSkipped ?? 0, 0, failure, 0, hello.Run.RunId, brain?.Search);
            return new(outcome, session?.Summary() ?? "", steps, session?.MapHashes.ToList() ?? [], observations, brain?.Log.ToList() ?? [], failure,
                HeadlessGame.TakeErrors(), record, nodes, roomSaves, brain?.Replans ?? 0);
        }
    }

    /// <summary>--auto-win's acts as the run's rule: indexes from 0, in order, each once; null for none.</summary>
    private static IReadOnlyList<int>? Acts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int count = MegaCrit.Sts2.Core.Models.ModelDb.ActsByIndex.Count;
        var acts = text.Split(',').Select(a => int.TryParse(a, out int act) && act >= 1 && act <= count ? act - 1
            : throw new CliException($"--auto-win takes acts from 1 to {count}, such as 1,2; not {a}")).Distinct().Order().ToList();
        return acts;
    }

    /// <summary>The game mode of <c>--mode</c>: the standard game, or the Dread mode at ascension 10, where its lobby puts it.</summary>
    internal static GameMode GameModeOf(string? text, int asc)
    {
        if (string.IsNullOrWhiteSpace(text)) return GameMode.Standard;
        if (!text.Equals("dread", StringComparison.OrdinalIgnoreCase)) throw new CliException($"--mode takes dread, not {text}");
        if (asc != 10) throw new CliException($"the game plays the Dread mode at ascension 10 only, not {asc} (--asc 10)");
        return GameMode.Dread;
    }

    /// <summary>One hash for a run's observation hashes, to compare runs across processes.</summary>
    private static ulong Digest(IEnumerable<ulong> hashes)
    {
        ulong digest = 14695981039346656037;
        foreach (var h in hashes) digest = (digest ^ h) * 1099511628211;
        return digest;
    }
}
