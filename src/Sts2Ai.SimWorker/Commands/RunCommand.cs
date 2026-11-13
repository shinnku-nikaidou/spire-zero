using System.CommandLine;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// run: whole runs through Harness. --policy picks the brain of every decision of a combat, its card prompts among
/// them; --macro picks the brain of every decision outside combat.
/// --check plays each seed again over the wire, to a brain in this process, and compares: observations, records and
/// decision logs included (each observation must survive a JSON round trip); and replays each node from its
/// checkpoint (the SL clone). Reports results, hashes and surface coverage.
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
    private static readonly Option<bool> Check = Opt.Of("--check", false, "plays each seed again over the wire and compares, and replays each node from its checkpoint");
    private static readonly Option<bool> Trace = Opt.Of("--trace", false, "prints every decision");
    private static readonly Option<bool> Obs = Opt.Of("--obs", false, "prints every observation");
    private static readonly Option<string?> Log = Opt.Maybe<string?>("--log", "writes Harness's decision log, one JSON line per decision");
    private static readonly Option<string?> Records = Opt.Maybe<string?>("--records", "writes one run record per line");
    private static readonly Option<string?> AutoWin = Opt.Maybe<string?>("--auto-win",
        "acts, numbered from 1 as the game shows them, such as 1,2: their plain monster fights, an event's included, are won at their start " +
        "(the game's win command) with their rewards, so only elites and bosses are fought there; for self-play, as no report or gate takes such a run");
    private static readonly ChampionArgs Champion = new(file: true);
    private static readonly LoadoutArgs Loadout = new();

    public static Command Command()
    {
        var command = Opt.Command("run", "whole runs through Harness",
            [Seeds, N, Start, Seed, Asc, Env, MaxDecisions, Check, Trace, Obs, Log, Records, AutoWin], Champion.Options, Loadout.Options);
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
        var champion = Champion.Read(r);
        if (champion.Policy == "plan" && env != PlayEnvironment.SL) throw new CliException("plan plays in SL only (--env SL)");
        if (champion.Engine && check) throw new CliException("--check plays each seed again over the wire, where no live combat reaches the engine: not with --combat-world engine");
        using var workerPool = champion.Pool();
        var harness = champion.Harness(workerPool, log);
        // --check plays each seed again over the wire, to a brain server in this process with the same champion, whose
        // decision log is compared too.
        Dispatcher? served = null;
        using var checkWire = check ? BrainConnection.InProcess(new BrainServer(hello => served = harness(hello))) : null;
        Func<Hello, IRunBrain> firstBrain = hello => new LocalBrain(harness(hello));
        IRunBrain secondBrain(Hello hello) => WireBrain.Start(checkWire!.Wire, hello);
        string character = Loadout.Character(r);
        var loadout = Loadout.Loadout(r);


        var outcomes = new Dictionary<string, int>();
        var surfaces = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        int mismatches = 0, gameErrors = 0, nodes = 0, nodesExact = 0, replans = 0;
        var cloneMs = new List<double>();
        Console.WriteLine($"run: {n} runs in {env}, {champion}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = new RunSetup(character, seeds?[i] ?? GameSeeds.MakeSeed(seed, i), asc, loadout, autoWin);
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
            var (clonesExact, cloneMismatch) = ClonesReplay(pump, first, autoWin, cloneMs);
            nodes += first.Nodes.Count;
            nodesExact += clonesExact;
            if (cloneMismatch != null) Console.WriteLine($"  CLONE MISMATCH: {cloneMismatch}");
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
                                     $"{nodesExact}/{nodes} nodes replay exactly from their checkpoint" +
                                     (cloneMs.Count > 0 ? $" ({cloneMs.Average():F0} ms each, max {cloneMs.Max():F0})" : ""));
        Console.WriteLine($"game Log.Error lines: {gameErrors}" + (workerPool != null ? $"; worker restarts: {workerPool.Restarts}" : "") +
                          (champion.Policy == "plan" ? $"; replans: {replans}" : ""));
        return failures.Count == 0 && mismatches == 0 && nodesExact == nodes && gameErrors == 0 ? 0 : 1;
    }

    /// <summary>
    /// Loads each node of a played run from its checkpoint and replays the node's decisions: the
    /// clone must show the run's observations one for one, and be at its horizon once it has travelled. Returns how
    /// many nodes did, and what went wrong in the first that did not; each node's load and replay time goes to
    /// <paramref name="ms"/>. The clones keep the run's rule, <paramref name="autoWin"/>.
    /// </summary>
    private static (int Exact, string? Mismatch) ClonesReplay(Pump pump, RunResult run, IReadOnlyList<int>? autoWin, List<double> ms)
    {
        int exact = 0;
        string? mismatch = null;
        for (int k = 0; k < run.Nodes.Count; k++)
        {
            var (save, start) = run.Nodes[k];
            int end = k + 1 < run.Nodes.Count ? run.Nodes[k + 1].Start : run.Trace.Count;
            var seen = new List<ulong>();
            string? wrong;
            var clock = Stopwatch.StartNew();
            try
            {
                var clone = new NodeClone(save, [.. run.Trace.Skip(start).Take(end - start)], autoWin);
                using var session = RunSession.Load(pump, clone, Unasked.Instance, Unasked.Instance, Unasked.Instance,
                    obs => seen.Add(ProtocolJson.Hash(obs)));
                int differs = Enumerable.Range(0, end - start).FirstOrDefault(i => seen.ElementAtOrDefault(i) != run.Observations[start + i], -1);
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
                wrong = $"{e.GetType().Name}: {Logs.FirstLine(e.Message)}";
            }
            ms.Add(clock.Elapsed.TotalMilliseconds);
            if (HeadlessGame.TakeErrors() is [var error, ..]) wrong ??= $"game error: {Logs.FirstLine(error)}";
            if (wrong != null) mismatch ??= $"node {k} (decisions {start}..{end - 1}): {wrong}";
            else exact++;
        }
        return (exact, mismatch);
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
    private sealed record RunResult(string Outcome, string Summary, IReadOnlyList<string> Trace, IReadOnlyList<ulong> MapHashes,
        IReadOnlyList<ulong> Observations, IReadOnlyList<DecisionEntry> Log, string? Failure, List<string> Errors, RunRecord Record,
        IReadOnlyList<(string Save, int Start)> Nodes, int Replans);

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
                observations.Add(ProtocolJson.Hash(obs));
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
            var config = new RunConfig(HeadlessGame.GameVersion, Program.Commit, brain?.Champion ?? "none", hello.Env, setup.AutoWin);
            var player = session?.Player;
            var record = new RunRecord(config, setup.Seed, setup.Character, setup.Ascension, outcome,
                session?.Run.CurrentActIndex ?? 0, session?.Run.TotalFloor ?? 0, player?.Creature.CurrentHp ?? 0, player?.Creature.MaxHp ?? 0,
                player?.Gold ?? 0, session?.Combats.ToList() ?? [], surfaces, new Dictionary<string, int>(brain?.Brains ?? new Dictionary<string, int>()),
                session?.CardRewards ?? 0, session?.CardRewardsSkipped ?? 0, 0, failure, 0, hello.Run.RunId, brain?.Search);
            return new(outcome, session?.Summary() ?? "", steps, session?.MapHashes.ToList() ?? [], observations, brain?.Log.ToList() ?? [], failure,
                HeadlessGame.TakeErrors(), record, nodes, brain?.Replans ?? 0);
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

    /// <summary>One hash for a run's observation hashes, to compare runs across processes.</summary>
    private static ulong Digest(IEnumerable<ulong> hashes)
    {
        ulong digest = 14695981039346656037;
        foreach (var h in hashes) digest = (digest ^ h) * 1099511628211;
        return digest;
    }
}
