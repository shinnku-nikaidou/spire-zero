using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// run: whole runs through Harness. Combat prompts go to p0; --macro picks the brain of every decision outside combat.
/// --check plays each seed again over the wire, to a brain in this process, and compares: observations, records and
/// decision logs included (each observation must survive a JSON round trip); and replays each node from its
/// checkpoint (the SL clone, docs: simulator §7). Reports results, hashes and surface coverage.
/// </summary>
internal static class RunCommand
{
    private static readonly Option<string?> Seeds = Opt.Maybe<string?>("--seeds", "a seed file: plays its seeds from --start, all of them unless --n");
    private static readonly Option<int?> N = Opt.Maybe<int?>("--n", "runs (default: 1, or the rest of --seeds)");
    private static readonly Option<int> Start = Opt.Of("--start", 0, "the first run's index");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base, without --seeds");
    private static readonly Option<int> Asc = Opt.Of("--asc", 0, "the ascension");
    private static readonly Option<PlayEnvironment> Env = Opt.Of("--env", PlayEnvironment.NoSL, "the environment (docs: environments)");
    private static readonly Option<int> MaxDecisions = Opt.Of("--max-decisions", 20_000, "a run stops here, truncated");
    private static readonly Option<bool> Check = Opt.Of("--check", false, "plays each seed again over the wire and compares, and replays each node from its checkpoint");
    private static readonly Option<bool> Trace = Opt.Of("--trace", false, "prints every decision");
    private static readonly Option<bool> Obs = Opt.Of("--obs", false, "prints every observation");
    private static readonly Option<string?> Log = Opt.Maybe<string?>("--log", "writes Harness's decision log, one JSON line per decision");
    private static readonly Option<string?> Records = Opt.Maybe<string?>("--records", "writes one run record per line (docs: evaluation §6)");
    private static readonly Option<string?> Brain = Opt.Maybe<string?>("--brain",
        "plays against the brain listening at this socket instead, which picks the champion (--check replays against it too)");
    private static readonly ChampionArgs Champion = new(file: true);
    private static readonly LoadoutArgs Loadout = new();

    public static Command Command()
    {
        var command = Opt.Command("run", "whole runs through Harness",
            [Seeds, N, Start, Seed, Asc, Env, MaxDecisions, Check, Trace, Obs, Log, Records, Brain], Champion.Options, Loadout.Options);
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
        var brainPath = r.GetValue(Brain);
        if (brainPath != null && Champion.Champion.Append(Log).FirstOrDefault(o => r.GetResult(o) is { Implicit: false }) is { } brainOption)
            throw new CliException($"{brainOption.Name} belongs to the brain at {brainPath}, not to this host");
        var champion = brainPath == null ? Champion.Read(r) : null;
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
        string character = Loadout.Character(r);
        var loadout = Loadout.Loadout(r);


        var outcomes = new Dictionary<string, int>();
        var surfaces = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        int mismatches = 0, gameErrors = 0, nodes = 0, nodesExact = 0, nodesRelicState = 0;
        var cloneMs = new List<double>();
        Console.WriteLine($"run: {n} runs in {env}, {(champion?.ToString() ?? $"brain at {brainPath}")}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = new RunSetup(character, seeds?[i] ?? Cli.MakeSeed(seed, i), asc, loadout);
            var started = clock.Elapsed;
            var first = PlayRun(pump, setup, env, firstBrain, maxDecisions, trace, observe, printObservations);
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
            foreach (var e in first.Errors.Take(3)) Console.WriteLine($"  game error: {Cli.FirstLine(e)}");
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
                wrong = $"{e.GetType().Name}: {Cli.FirstLine(e.Message)}";
            }
            ms.Add(clock.Elapsed.TotalMilliseconds);
            if (HeadlessGame.TakeErrors() is [var error, ..]) wrong ??= $"game error: {Cli.FirstLine(error)}";
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
            Logs.Sim.LogError(e, "run {Seed} failed", setup.Seed);
            result = Result("failed", $"{e.GetType().Name}: {Cli.FirstLine(e.Message)}");
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
            var config = new RunConfig(HeadlessGame.GameVersion, Cli.Commit, brain?.Champion ?? "none", hello.Env, "R1");
            var player = session?.Player;
            var record = new RunRecord(config, setup.Seed, setup.Character, setup.Ascension, outcome,
                session?.Run.CurrentActIndex ?? 0, session?.Run.TotalFloor ?? 0, player?.Creature.CurrentHp ?? 0, player?.Creature.MaxHp ?? 0,
                player?.Gold ?? 0, session?.Combats.ToList() ?? [], surfaces, new Dictionary<string, int>(brain?.Brains ?? new Dictionary<string, int>()),
                session?.CardRewards ?? 0, session?.CardRewardsSkipped ?? 0, 0, failure, 0, hello.Run.RunId);
            return new(outcome, session?.Summary() ?? "", steps, session?.MapHashes.ToList() ?? [], observations, brain?.Log.ToList() ?? [], failure,
                HeadlessGame.TakeErrors(), record, nodes);
        }
    }

    /// <summary>One hash for a run's observation hashes, to compare runs across processes.</summary>
    private static ulong Digest(IEnumerable<ulong> hashes)
    {
        ulong digest = 14695981039346656037;
        foreach (var h in hashes) digest = (digest ^ h) * 1099511628211;
        return digest;
    }
}

/// <summary>
/// brain: the brain alone (docs: protocol §2), which serves the hosts that connect to the socket, one at a time, with the
/// champion its options name. Prints a line per run, with the game errors of its search workers.
/// </summary>
internal static class BrainCommand
{
    private static readonly Option<string> Socket = Opt.Of("--socket", BrainSocket.DefaultPath, "the Unix socket to listen at");
    private static readonly Option<string?> Log = Opt.Maybe<string?>("--log", "writes Harness's decision log, one JSON line per decision");
    private static readonly ChampionArgs Champion = new(file: true);

    public static Command Command()
    {
        var command = Opt.Command("brain", "the brain alone: serves the hosts that connect, one at a time (docs: protocol §2)", [Socket, Log], Champion.Options);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        var path = r.GetValue(Socket)!;
        var champion = Champion.Read(r);

        using var workerPool = champion.Pool();
        var pooled = workerPool == null ? null : new PooledPimc(workerPool);
        using var logFile = r.GetValue(Log) is { } logPath ? new StreamWriter(logPath) { AutoFlush = true } : null;
        var server = new BrainServer(champion.Harness(pooled, logFile != null), log: Logs.Factory.CreateLogger("brain"), ended: (hello, harness, end) =>
        {
            foreach (var entry in harness.Log) logFile?.WriteLine(ProtocolJson.Serialize(entry));
            var errors = pooled?.TakeGameErrors() ?? [];
            Console.WriteLine($"{hello.Run.RunId} ({hello.Role}) {end.Reason} floor {end.Floor}, {harness.Brains.Values.Sum()} decisions" +
                              (errors.Count > 0 ? $", game Log.Error lines in workers: {errors.Count}" : ""));
            foreach (var e in errors.Take(3)) Console.WriteLine($"  game error: {Cli.FirstLine(e)}");
        });
        Console.WriteLine($"brain: {champion}, listening on {path}");
        server.Listen(path);
        return 0;
    }
}

/// <summary>The champion that --policy and --macro name (docs: harness §4), pimc's budget, and l0's V (--value).</summary>
/// <param name="ValueFile">Where <paramref name="Value"/> came from; null for the initial weights.</param>
/// <param name="MacroExplore">For self-play only (docs: learning §5): l0's softmax temperature over V's values, in V's
/// units (--macro-explore); 0 for none.</param>
/// <param name="CombatExplore">The same for pimc over the search's values, in the leaf's units (--combat-explore).</param>
internal sealed record ChampionOptions(string Policy, string Macro, Budget Budget, int Workers, IRunValue Value, string? ValueFile,
    LeafChoice Leaf, double MacroExplore, double CombatExplore)
{
    /// <summary>Whether it explores, which only self-play does.</summary>
    public bool Explores => MacroExplore > 0 || CombatExplore > 0;

    /// <summary>pimc's search workers. A run holds its process's one live game, so search runs in other processes.</summary>
    public WorkerPool? Pool() => Policy == "pimc" ? new WorkerPool(Cli.SelfCommand(), Workers, Leaf.WorkerOptions()) : null;

    /// <summary>
    /// One Harness per run. Combat prompts go to p0 (<see cref="PromptBrain"/>), which search worlds ask too.
    /// Placeholders draw from one generator seeded by the run id, so a run plays the same whatever is logged, in
    /// this process or over the wire; so do the exploring brains, each from its own. In SL, pimc searches the node's
    /// clone; nothing else looks further than in NoSL.
    /// </summary>
    public Func<Hello, Dispatcher> Harness(PooledPimc? pooled, bool log) => hello =>
    {
        if (!Enum.TryParse<PlayEnvironment>(hello.Env, out var env)) throw new NotSupportedException($"no environment {hello.Env}");
        Random Rng(string stream) => new(unchecked((int)ProtocolJson.Hash(hello.Run.RunId + stream)));
        var rng = Rng("");
        var combat = pooled?.Route(Budget, Combat) ?? CombatRoute.Of(Policy, Policies.Combat(Policy, rng));
        if (CombatExplore > 0) combat = combat.Exploring(new Softmax(CombatExplore, Rng(":explore combat")));
        var prompts = new Route<IChoicePolicy>("p0", PromptBrain.Instance);
        Dispatcher dispatcher;
        if (Macro == "l0")
        {
            var l0 = new MacroBrain(Value, MacroExplore > 0 ? new Softmax(MacroExplore, Rng(":explore")) : null);
            dispatcher = new Dispatcher(hello.Run.RunId, env, combat, prompts, new(L0, l0), new(L0, l0), log);
        }
        else dispatcher = new Dispatcher(hello.Run.RunId, env, combat, prompts, new("random", new RandomMacro(rng)), new("random", new RandomChoices(rng)), log);
        if (dispatcher.Champion != Name) throw new InvalidOperationException($"champion {dispatcher.Champion} goes by {Name} in champion files");
        return dispatcher;
    };

    /// <summary>The combat brain, pimc by its leaf's names unless it is the default one, then its exploration if any.</summary>
    private string Combat => Policy + Leaf.Suffix + Exploration(CombatExplore);

    /// <summary>l0 by its V's name, unless V has the initial weights, then its exploration if any.</summary>
    private string L0 => (Value == RunValue.Initial ? "l0" : $"l0 {Value.Name}") + Exploration(MacroExplore);

    private static string Exploration(double temperature) => temperature > 0 ? FormattableString.Invariant($" explore {temperature}") : "";

    /// <summary>The champion as Harness names it (<see cref="Dispatcher.Champion"/>), which runs check.</summary>
    public string Name
    {
        get
        {
            string macro = Macro == "l0" ? L0 : "random";
            return $"combat {Combat}{(Policy == "pimc" ? $" {Budget}" : "")}, combat prompts p0, macro {macro}, macro prompts {macro}";
        }
    }

    /// <summary>The options that play this champion, for a champion file.</summary>
    public Dictionary<string, string> Options(PlayEnvironment env)
    {
        var options = new Dictionary<string, string> { ["env"] = env.ToString(), ["policy"] = Policy, ["macro"] = Macro };
        if (Policy == "pimc")
        {
            options["worlds"] = $"{Budget.Worlds}";
            options["nodes"] = $"{Budget.NodesPerWorld}";
            if (Budget.Elite != 1) options["elite-x"] = Budget.Elite.ToString(CultureInfo.InvariantCulture);
            if (Budget.Boss != 1) options["boss-x"] = Budget.Boss.ToString(CultureInfo.InvariantCulture);
            if (Budget.Halving) options["halving"] = "true";
            if (Budget.Follow) options["follow"] = "true";
        }
        if (ValueFile != null) options["value"] = ValueFile;
        if (MacroExplore > 0) options["macro-explore"] = MacroExplore.ToString(CultureInfo.InvariantCulture);
        if (CombatExplore > 0) options["combat-explore"] = CombatExplore.ToString(CultureInfo.InvariantCulture);
        if (Leaf.WeightsPath != null) options["leaf"] = Leaf.WeightsPath;
        if (Leaf.NetPath != null) options["combat-net"] = Leaf.NetPath;
        return options;
    }

    /// <summary>
    /// The options of this challenger as the character's champion: its character and ascension, and each weights file
    /// it plays with (V, the leaf weights, the combat net) copied onto the character's main name in the environment,
    /// weights/CHARACTER/ENV/{value,leaf,combat}.json, which the champion file then names. The trainers' own files stay
    /// in weights/CHARACTER/history (docs: learning §6).
    /// </summary>
    public Dictionary<string, string> Promoted(PlayEnvironment env, string character, int ascension)
    {
        var options = Options(env);
        options["character"] = character;
        options["asc"] = ascension.ToString(CultureInfo.InvariantCulture);
        foreach (var (key, role) in new[] { ("value", "value"), ("leaf", "leaf"), ("combat-net", "combat") })
        {
            if (!options.TryGetValue(key, out var path)) continue;
            var main = Layout.MainWeights(character, env, role);
            if (Path.GetFullPath(path) != Path.GetFullPath(main))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(main)!);
                File.Copy(path, main, overwrite: true);
            }
            options[key] = main;
        }
        return options;
    }

    public override string ToString() =>
        $"policy {Combat}{(Policy == "pimc" ? $" budget {Budget} on {Workers} workers" : "")}, macro {(Macro == "l0" ? L0 : Macro)}";
}
