using System.CommandLine;
using System.Globalization;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// brain: the brain alone, which serves the host that started it, the mod, on its stdin and stdout,
/// with the champion its options name, and ends when stdin closes. Each host names its environment: NoSL hosts get --policy's combat brain, SL hosts
/// --sl-policy's (the SL planner by default), with the same macro brain, whose afterstates
/// the environment sets (<see cref="Champions"/>), so one champion file serves both; or SL hosts get their own file's
/// champion (--sl-champion), as the mod ships them: each environment has its own nets. The SL planner searches in the
/// engine, on the live combat an SL host sends with each combat decision
/// (<see cref="DecisionRequest.Export"/>), and crosses a card prompt on the node's clone in the search workers. A host may ask for
/// another effort of the SL planner for its run (<see cref="Hello.Effort"/>), which then plays with that preset's
/// budget. Prints a line per run on stderr, with the game errors of its search workers.
/// </summary>
internal static class BrainCommand
{
    private static readonly Option<string?> Log = Opt.Maybe<string?>("--log", "writes Harness's decision log, one JSON line per decision as it is answered");
    private static readonly Option<string?> Wire = Opt.Maybe<string?>("--wire", "writes every message the hosts send, one JSON line each (what clones replays)");
    private static readonly Option<string> SlPolicy = Opt.Of("--sl-policy", "plan", "the combat brain of SL hosts; --policy is NoSL hosts'")
        .AcceptOnlyFromAmong(["pimc", "plan", .. Policies.CombatNames]);
    private static readonly Option<string?> SlChampion = Opt.Maybe<string?>("--sl-champion",
        "SL hosts' own champion file, played as run --champion FILE plays it, with this command's --workers (not with --sl-policy); " +
        "without it SL hosts play --champion's with --sl-policy's combat brain");
    private static readonly ChampionArgs Champion = new(file: true);

    public static Command Command()
    {
        var command = Opt.Command("brain", "the brain alone: serves the host that starts it on stdin and stdout, until stdin closes", [Log, Wire, SlPolicy, SlChampion],
            Champion.Options);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        // The wire owns stdout: every other line goes to stderr, from the first.
        var wireOut = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        var (champion, sl) = Champions(r);

        using var workerPool = champion.Pool() ?? sl.Pool();
        // Added to: the mod starts a brain again in the same game session when one ends, with the same files.
        using var logFile = r.GetValue(Log) is { } logPath ? new StreamWriter(logPath, append: true) { AutoFlush = true } : null;
        using var wireFile = r.GetValue(Wire) is { } wirePath ? new StreamWriter(wirePath, append: true) { AutoFlush = true } : null;
        // One Harness maker per environment, effort and turn decay asked for, made at the first run that asks, with the
        // champion as the host shows it.
        var harnesses = new Dictionary<(string Env, string? Effort, bool TurnDecay), (Func<Hello, Dispatcher> Make, string Shown)>();
        (Func<Hello, Dispatcher> Make, string Shown) HarnessFor(Hello hello)
        {
            var key = (hello.Env, hello.Effort, hello.TurnDecay);
            if (harnesses.TryGetValue(key, out var made)) return made;
            var options = hello.Env == nameof(PlayEnvironment.SL) ? sl : champion;
            // An effort is the SL planner's budget, and the turn decay is its too; another combat brain plays as it is.
            if (hello.Effort is { } effort && options.Policy == "plan") options = options with { Budget = Budget.Effort(effort) };
            if (hello.TurnDecay && options.Policy == "plan") options = options with { Budget = options.Budget with { TurnDecay = SlPlanner.PlayerTurnDecay } };
            // The game takes its time between decisions, which max's solver searches on (SolverRoute).
            return harnesses[key] = (options.Harness(workerPool, logFile != null, explain: true, background: true), options.Shown);
        }
        // Each decision is written as it is answered, so a game that crashes or is closed mid-run keeps every decision
        // made so far. A connection serves its runs one after another, each with its own Harness.
        (Dispatcher? Run, int Written) logged = (null, 0);
        void WriteLog(Dispatcher run)
        {
            if (logFile == null) return;
            if (!ReferenceEquals(run, logged.Run)) logged = (run, 0);
            for (; logged.Written < run.Log.Count; logged.Written++) logFile.WriteLine(ProtocolJson.Serialize(run.Log[logged.Written]));
        }
        var server = new BrainServer(hello =>
            {
                var (make, shown) = HarnessFor(hello);
                return (make(hello), shown);
            },
            received: wireFile == null ? null : message => wireFile.WriteLine(ProtocolJson.Serialize(message)),
            answered: WriteLog,
            ended: (hello, harness, end) =>
        {
            WriteLog(harness);
            var errors = workerPool?.TakeGameErrors() ?? [];
            Console.WriteLine($"{hello.Run.RunId} ({hello.Role}) {end.Reason} floor {end.Floor}, {harness.Brains.Values.Sum()} decisions" +
                              (harness.Replans > 0 ? $", {harness.Replans} replans" : "") +
                              (errors.Count > 0 ? $", game Log.Error lines in workers: {errors.Count}" : ""));
            foreach (var e in errors.Take(3)) Console.WriteLine($"  game error: {Logs.FirstLine(e)}");
        });
        Console.WriteLine($"brain: NoSL {champion}; SL {sl}; serving stdin and stdout");
        server.Serve(new WireStream(Console.OpenStandardInput(), wireOut));
        return 0;
    }

    /// <summary>
    /// The champion of NoSL hosts and of SL hosts: --champion's, and SL's own file (--sl-champion) when given, since
    /// each environment has its own nets (2026-11-18). L0 values its options on the afterstates the screen predicts in
    /// NoSL and on the node's clone in SL, the only pairing Harness takes, whatever --afterstates says.
    /// </summary>
    internal static (ChampionOptions NoSL, ChampionOptions SL) Champions(ParseResult r)
    {
        var slPolicy = r.GetValue(SlPolicy)!;
        var slFile = r.GetValue(SlChampion);
        if (slFile != null && r.GetResult(SlPolicy) is { Implicit: false }) throw new CliException("--sl-champion names SL's champion whole: not with --sl-policy");
        var noSl = Champion.Read(r, planElsewhere: slFile == null && slPolicy == "plan") with { OnClone = false };
        var sl = slFile == null ? Champion.Read(r, slPolicy)
            : ChampionArgs.Of(slFile, r.GetValue<int?>("--workers") is { } workers ? ["--workers", workers.ToString(CultureInfo.InvariantCulture)] : []);
        // The clone values a fight an option starts with the combat net; without one, Harness refuses SL hosts.
        return (noSl, sl.Macro == "l0" && sl.Leaf.Net != null ? sl with { OnClone = true } : sl);
    }
}
