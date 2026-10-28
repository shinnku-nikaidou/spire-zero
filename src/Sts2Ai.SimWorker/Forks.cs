using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>One decision of a logged run: what was asked and answered, and the hash of what the brain was shown.</summary>
/// <param name="Act">On a macro decision, the act it was in, from its run view.</param>
internal sealed record LoggedStep(string Surface, string Chosen, string ObsHash, int Legal, bool Explored, int? Act);

/// <summary>A run of a decision log (docs: harness §6) with its setup from its run record: its decisions in order.</summary>
internal sealed record LoggedRun(string RunId, RunSetup Setup, IReadOnlyList<LoggedStep> Steps)
{
    /// <summary>
    /// The runs of the logs that have a run record, in the logs' order. A log holds each run's lines together, in order;
    /// a run stopped midway has no record and may end in a cut line, so it is left out, as is a run with a line missing.
    /// </summary>
    public static IEnumerable<LoggedRun> Read(string logs, string records)
    {
        var setups = new Dictionary<string, RunSetup>();
        foreach (var r in Cli.ReadRecords(records)) setups.TryAdd(RunHeader.IdOf(r.Seed), new RunSetup(r.Character, r.Seed, r.Ascension));
        foreach (var path in logs.Split(','))
        {
            string? run = null;
            var steps = new List<LoggedStep>();
            bool whole = true;
            foreach (var line in File.ReadLines(path))
            {
                if (Parse(line) is not var (id, decision, step)) continue;
                if (id != run)
                {
                    if (run != null && whole && setups.TryGetValue(run, out var setup)) yield return new(run, setup, steps);
                    (run, steps, whole) = (id, [], true);
                }
                whole &= decision == steps.Count;
                steps.Add(step);
            }
            if (run != null && whole && setups.TryGetValue(run, out var last)) yield return new(run, last, steps);
        }
    }

    private static (string Run, int DecisionId, LoggedStep Step)? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            int? act = e.TryGetProperty("run_view", out var view) ? view.GetProperty("act").GetInt32() : null;
            bool explored = e.TryGetProperty("explored", out var x) && x.GetBoolean();
            return (e.GetProperty("run").GetString()!, e.GetProperty("decision_id").GetInt32(), new LoggedStep(e.GetProperty("surface").GetString()!,
                e.GetProperty("chosen").GetString()!, e.GetProperty("obs_hash").GetString()!, e.GetProperty("legal").GetInt32(), explored, act));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Answers a logged run's decisions in order, card prompts included, and checks each against the log: the same surface
/// (else it throws), and the same observation, by hash (else it counts a mismatch: the observation's format may have
/// changed since). <paramref name="seen"/> sees each macro decision it answers. Past the log, <paramref name="next"/>
/// answers the first decision, which must be a macro one, then <paramref name="then"/> the rest.
/// </summary>
internal sealed class Script(IReadOnlyList<LoggedStep> steps, IRunBrain? then, Func<MacroDecision, string>? next = null,
    Action<MacroDecision, int>? seen = null) : ICombatPolicy, IChoicePolicy, IMacroPolicy
{
    private int _done;
    private bool _nextAsked;

    public int Pending => steps.Count - _done;

    /// <summary>Decisions whose observation's hash differs from the log's.</summary>
    public int Mismatches { get; private set; }

    public CombatAction Choose(CombatDecision decision) =>
        _done < steps.Count ? CombatAction.Parse(Step(Surfaces.Combat, ProtocolJson.Hash(decision.Observation))) : Then().Choose(decision);

    public IReadOnlyList<int> Choose(ChoiceDecision decision) =>
        _done < steps.Count
            ? Step(Surfaces.CardChoice, ProtocolJson.Hash(decision.Choice)).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()
            : Then().Choose(decision);

    public string Choose(MacroDecision decision)
    {
        if (_done < steps.Count)
        {
            seen?.Invoke(decision, _done);
            return Step(decision.Surface, ProtocolJson.Hash(decision.Observation));
        }
        if (next == null || _nextAsked) return Then().Choose(decision);
        _nextAsked = true;
        return next(decision);
    }

    private string Step(string surface, ulong hash)
    {
        var step = steps[_done++];
        if (step.Surface != surface) throw new InvalidOperationException($"replay diverged at decision {_done - 1}: logged on {step.Surface}, asked on {surface}");
        if (hash.ToString("x16") != step.ObsHash) Mismatches++;
        return step.Chosen;
    }

    private IRunBrain Then()
    {
        if (next != null && !_nextAsked) throw new InvalidOperationException($"the first decision past the log was not a macro one");
        return then ?? throw new InvalidOperationException("asked beyond the log");
    }
}

/// <summary>An option of a macro decision as L0 valued it; <paramref name="After"/> is its afterstate as the run view's
/// fields that differ from the decision's (<see cref="Logged.Changes"/>), and <paramref name="Features"/> V v0's
/// features of it, which V v2 reads too.</summary>
internal sealed record ValuedOption(string Action, double Value, JsonObject After, IReadOnlyDictionary<string, double> Features);

/// <summary>One macro decision of a logged run, with every option L0 weighed (afterstates).</summary>
/// <param name="Same">Whether the replayed observation's hash is the log's.</param>
internal sealed record AfterstateEntry(string Run, int DecisionId, string Surface, string Chosen, bool Explored, bool Same, IReadOnlyList<ValuedOption> Options);

/// <summary>One branch of a fork, at its horizon: the end of the fork's act, or of the run.</summary>
/// <param name="Decisions">The decisions it made after the fork.</param>
/// <param name="Horizon">The run at the horizon, and V v0's features of it.</param>
internal sealed record ForkBranch(string Action, bool Died, int Act, int Floor, int Hp, int MaxHp, int Gold, int Decisions, double ElapsedMs, RunView Horizon,
    IReadOnlyDictionary<string, double> HorizonFeatures);

/// <param name="Logged">What the logged run chose at the decision.</param>
/// <param name="Mismatches">Replayed observations whose hash differs from the log's, before the decision.</param>
/// <param name="RunView">The run at the decision.</param>
internal sealed record ForkEntry(string Run, string Seed, int DecisionId, string Surface, int Act, string Logged, bool Explored, int Mismatches,
    RunView RunView, IReadOnlyList<ValuedOption> Options, IReadOnlyList<ForkBranch> Branches);

internal static class Logged
{
    public static readonly Option<string> Log = Opt.Required<string>("--log", "decision logs, comma-separated");
    public static readonly Option<string> Records = Opt.Required<string>("--records", "the run records of the logged runs, comma-separated");
    public static readonly Option<string> Out = Opt.Required<string>("--out", "writes one JSON line per entry");
    public static readonly Option<int> Start = Opt.Of("--start", 0, "the first logged run, by its index among the logged runs with a record");
    public static readonly Option<int?> N = Opt.Maybe<int?>("--n", "logged runs (default: the rest)");

    public static IEnumerable<LoggedRun> Runs(ParseResult r)
    {
        var runs = LoggedRun.Read(r.GetValue(Log)!, r.GetValue(Records)!).Skip(r.GetValue(Start));
        return r.GetValue(N) is { } n ? runs.Take(n) : runs;
    }

    public static IReadOnlyList<ValuedOption> Options(MacroBrain l0, MacroDecision decision) =>
        l0.Options(decision).Select(o => new ValuedOption(o.Action, o.Value, Changes(decision.Observation.Run, o.After), RunValue.Features(o.After))).ToList();

    /// <summary>
    /// The fields of <paramref name="after"/> that differ from <paramref name="before"/>, in their JSON form, to one level
    /// inside an object field (a map's visited points); a field gone is null. An afterstate is what its action changes.
    /// </summary>
    public static JsonObject Changes(RunView before, RunView after) =>
        Diff(JsonNode.Parse(ProtocolJson.Serialize(before))!.AsObject(), JsonNode.Parse(ProtocolJson.Serialize(after))!.AsObject(), nested: true);

    private static JsonObject Diff(JsonObject before, JsonObject after, bool nested)
    {
        var diff = new JsonObject();
        foreach (var (key, value) in after)
        {
            var old = before[key];
            if (JsonNode.DeepEquals(old, value)) continue;
            diff[key] = nested && old is JsonObject o && value is JsonObject v ? Diff(o, v, nested: false) : value?.DeepClone();
        }
        foreach (var (key, _) in before)
            if (!after.ContainsKey(key)) diff[key] = null;
        return diff;
    }
}

/// <summary>
/// afterstates (docs: learning §2): replays logged runs from their seeds, answering each decision as the log did, and
/// writes each macro decision's options as L0 weighs them with the champion's V: the log keeps only the run before
/// the decision and what was chosen.
/// </summary>
internal static class AfterstatesCommand
{
    private static readonly ChampionArgs Champion = new(file: true);

    public static Command Command()
    {
        var command = Opt.Command("afterstates", "each macro decision of logged runs, replayed, with every option's afterstate and value",
            [Logged.Log, Logged.Records, Logged.Out, Logged.Start, Logged.N], Champion.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        var champion = Champion.Read(r);
        if (champion.Macro != "l0") throw new CliException("afterstates weighs options with l0's V: it needs --macro l0");
        var l0 = new MacroBrain(champion.Value);
        using var output = new StreamWriter(r.GetValue(Logged.Out)!) { AutoFlush = true };
        int runs = 0, failed = 0, decisions = 0, mismatches = 0;
        var clock = Stopwatch.StartNew();
        foreach (var run in Logged.Runs(r))
        {
            var entries = new List<AfterstateEntry>();
            var script = new Script(run.Steps, null, seen: (decision, i) =>
            {
                var step = run.Steps[i];
                bool same = ProtocolJson.Hash(decision.Observation).ToString("x16") == step.ObsHash;
                entries.Add(new(run.RunId, i, step.Surface, step.Chosen, step.Explored, same, Logged.Options(l0, decision)));
            });
            try
            {
                using var session = RunSession.Start(pump, run.Setup, script, script, script);
                while (script.Pending > 0 && !session.IsOver) session.Step();
                if (script.Pending > 0) throw new InvalidOperationException($"the run ended with {script.Pending} logged decisions left");
            }
            catch (Exception e)
            {
                Logs.Sim.LogError(e, "run {Run} failed", run.RunId);
                failed++;
                continue;
            }
            foreach (var entry in entries) output.WriteLine(ProtocolJson.Serialize(entry));
            (runs, decisions, mismatches) = (runs + 1, decisions + entries.Count, mismatches + script.Mismatches);
        }
        Console.WriteLine($"afterstates: {runs} runs replayed, {failed} failed; {decisions} macro decisions; {mismatches} observations differ from the log; {clock.Elapsed.TotalSeconds:F0} s");
        return failed > 0 ? 1 : 0;
    }
}

/// <summary>
/// fork (docs: learning §2): counterfactual pairs from logged runs. Each picked decision is reached again by replaying
/// its run from the seed, then played twice from there: once with L0's best action, once with another of the screen's
/// own actions drawn uniformly (not a potion's, which every screen offers; a decision without another is passed over).
/// The champion plays both on to the end of the decision's act (past its boss) or of the run. The two
/// branches share everything up to the decision, the game's random state and the brains' seeds included, so what
/// tells them apart is the choice, as far as the game's random streams stay in step after it.
/// </summary>
internal static class ForkCommand
{
    private static readonly Option<int> PerRun = Opt.Of("--per-run", 2, "decisions forked per logged run, drawn among the ones that qualify, a surface at a time");
    private static readonly Option<string> ForkSurfaces = Opt.Of("--surfaces", "map,card_reward,rest,shop", "the macro surfaces to fork on, comma-separated");
    private static readonly Option<int> Act = Opt.Of("--act", 0, "forks decisions of this act only");
    private static readonly Option<int> MaxDecisions = Opt.Of("--max-decisions", 20_000, "a branch stops here");
    private static readonly ChampionArgs Champion = new(file: true);

    public static Command Command()
    {
        var command = Opt.Command("fork", "counterfactual pairs: logged decisions replayed, then played with L0's best and with another action",
            [Logged.Log, Logged.Records, Logged.Out, Logged.Start, Logged.N, PerRun, ForkSurfaces, Act, MaxDecisions], Champion.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        var champion = Champion.Read(r);
        if (champion.Explores) throw new CliException("--macro-explore and --combat-explore are for self-play; the branches play the champion's best");
        if (champion.Macro != "l0") throw new CliException("fork picks the best action with l0's V: it needs --macro l0");
        var l0 = new MacroBrain(champion.Value);
        using var pool = champion.Pool();
        var harness = champion.Harness(pool, log: false);
        var surfaces = r.GetValue(ForkSurfaces)!.Split(',').ToHashSet();
        int perRun = r.GetValue(PerRun), act = r.GetValue(Act), maxDecisions = r.GetValue(MaxDecisions);
        using var output = new StreamWriter(r.GetValue(Logged.Out)!) { AutoFlush = true };
        Console.WriteLine($"fork: {champion}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        int forks = 0, failed = 0;
        foreach (var run in Logged.Runs(r))
        {
            // A surface at a time, in a random order, each surface's decisions in a random order: a run has far more of
            // some surfaces than of others.
            var rng = new Random(unchecked((int)ProtocolJson.Hash(run.RunId + ":fork")));
            var bySurface = Enumerable.Range(0, run.Steps.Count)
                .Where(i => run.Steps[i] is { Act: { } a, Legal: >= 2 } s && a == act && surfaces.Contains(s.Surface))
                .GroupBy(i => run.Steps[i].Surface).OrderBy(_ => rng.Next()).Select(g => g.OrderBy(_ => rng.Next()).ToList()).ToList();
            var qualify = Enumerable.Range(0, bySurface.Select(g => g.Count).DefaultIfEmpty(0).Max())
                .SelectMany(round => bySurface.Where(g => round < g.Count).Select(g => g[round]));
            int done = 0;
            foreach (int k in qualify)
            {
                if (done == perRun) break;
                try
                {
                    if (Fork(pump, run, k, l0, harness, maxDecisions) is not { } entry) continue;
                    output.WriteLine(ProtocolJson.Serialize(entry));
                    (forks, done) = (forks + 1, done + 1);
                    var (a, b) = (entry.Branches[0], entry.Branches[1]);
                    Console.WriteLine($"{run.Setup.Seed} {k} {entry.Surface}: {a.Action} {(a.Died ? "died" : "lived")} floor {a.Floor} hp {a.Hp}, " +
                                      $"{b.Action} {(b.Died ? "died" : "lived")} floor {b.Floor} hp {b.Hp} ({(a.ElapsedMs + b.ElapsedMs) / 1000:F0} s)");
                }
                catch (Exception e)
                {
                    Logs.Sim.LogError(e, "fork {Run} {Decision} failed", run.RunId, k);
                    failed++;
                }
                pool?.TakeGameErrors();
            }
        }
        Console.WriteLine($"fork: {forks} forks, {failed} failed");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>The fork at decision <paramref name="k"/>, or null when it has no other action to try.</summary>
    private static ForkEntry? Fork(Pump pump, LoggedRun run, int k, MacroBrain l0, Func<Hello, Dispatcher> harness, int maxDecisions)
    {
        MacroDecision? at = null;
        IReadOnlyList<ValuedOption> options = [];
        string best = "", other = "";
        int mismatches = 0;
        ForkBranch first;
        try
        {
            first = Branch(pump, run, k, harness, maxDecisions, decision =>
            {
                (at, options) = (decision, Logged.Options(l0, decision));
                best = options[Softmax.Best(options.Select(o => o.Value).ToList())].Action;
                var others = options.Select(o => o.Action).Where(a => a != best && !a.StartsWith("potion:") && !a.StartsWith("discard_potion:")).ToList();
                if (others.Count == 0) throw new NoOther();
                other = others[new Random(unchecked((int)ProtocolJson.Hash($"{run.RunId}:fork {k}"))).Next(others.Count)];
                return best;
            }, out mismatches);
        }
        catch (Exception e) when (e.GetBaseException() is NoOther)
        {
            return null;
        }
        var second = Branch(pump, run, k, harness, maxDecisions, decision =>
        {
            if (ProtocolJson.Hash(decision.Observation) != ProtocolJson.Hash(at!.Observation)) throw new InvalidOperationException("the second branch reached another decision");
            return other;
        }, out _);
        var step = run.Steps[k];
        return new(run.RunId, run.Setup.Seed, k, step.Surface, step.Act ?? 0, step.Chosen, step.Explored, mismatches, at!.Observation.Run, options, [first, second]);
    }

    private sealed class NoOther : Exception;

    /// <summary>The run replayed to decision <paramref name="k"/>, answered there by <paramref name="pick"/>, then the
    /// champion's to the end of the act or the run.</summary>
    private static ForkBranch Branch(Pump pump, LoggedRun run, int k, Func<Hello, Dispatcher> harness, int maxDecisions, Func<MacroDecision, string> pick,
        out int mismatches)
    {
        var clock = Stopwatch.StartNew();
        var setup = run.Setup;
        var brain = new LocalBrain(harness(new Hello(HeadlessGame.GameVersion, PlayEnvironment.NoSL.ToString(), "worker",
            new RunHeader(run.RunId, setup.Character, setup.Ascension))));
        string? action = null;
        var script = new Script(run.Steps.Take(k).ToList(), brain, decision => action = pick(decision));
        using var session = RunSession.Start(pump, setup, script, script, script);
        int act = run.Steps[k].Act ?? 0;
        while (!session.IsOver && session.Run.CurrentActIndex <= act && session.Trace.Count < maxDecisions) session.Step();
        if (action == null) throw new InvalidOperationException($"the replay never reached decision {k}");
        mismatches = script.Mismatches;
        var player = session.Player;
        var horizon = Observe.Run(session.Run, player);
        return new(action, session.IsOver && !session.Won, session.Run.CurrentActIndex, session.Run.TotalFloor, player.Creature.CurrentHp, player.Creature.MaxHp,
            player.Gold, session.Trace.Count - k - 1, clock.Elapsed.TotalMilliseconds, horizon, RunValue.Features(horizon));
    }
}
