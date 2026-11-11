using System.CommandLine;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// brain: the brain alone, which serves the host that started it, the mod, on its stdin and stdout,
/// with the champion its options name, and ends when stdin closes. Each host names its environment: NoSL hosts get --policy's combat brain, SL hosts
/// --sl-policy's (the SL planner by default), with the same macro brain, whose afterstates
/// the environment sets (<see cref="Champions"/>), so one champion file serves both. A host may ask for
/// another effort of the SL planner for its run (<see cref="Hello.Effort"/>), which then plays with that preset's
/// budget. Prints a line per run on stderr, with the game errors of its search workers.
/// </summary>
internal static class BrainCommand
{
    private static readonly Option<string?> Log = Opt.Maybe<string?>("--log", "writes Harness's decision log, one JSON line per decision");
    private static readonly Option<string?> Wire = Opt.Maybe<string?>("--wire", "writes every message the hosts send, one JSON line each (what clones replays)");
    private static readonly Option<string> SlPolicy = Opt.Of("--sl-policy", "plan", "the combat brain of SL hosts; --policy is NoSL hosts'")
        .AcceptOnlyFromAmong(["pimc", "plan", .. Policies.CombatNames]);
    private static readonly ChampionArgs Champion = new(file: true);

    public static Command Command()
    {
        var command = Opt.Command("brain", "the brain alone: serves the host that starts it on stdin and stdout, until stdin closes", [Log, Wire, SlPolicy],
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
        using var logFile = r.GetValue(Log) is { } logPath ? new StreamWriter(logPath) { AutoFlush = true } : null;
        using var wireFile = r.GetValue(Wire) is { } wirePath ? new StreamWriter(wirePath) { AutoFlush = true } : null;
        // One Harness maker per environment and effort asked for, made at the first run that asks.
        var harnesses = new Dictionary<(string Env, string? Effort), Func<Hello, Dispatcher>>();
        Func<Hello, Dispatcher> HarnessFor(Hello hello)
        {
            if (harnesses.TryGetValue((hello.Env, hello.Effort), out var made)) return made;
            var options = hello.Env == nameof(PlayEnvironment.SL) ? sl : champion;
            // An effort is the SL planner's budget; another combat brain plays as it is.
            if (hello.Effort is { } effort && options.Policy == "plan") options = options with { Budget = Budget.Effort(effort) };
            return harnesses[(hello.Env, hello.Effort)] = options.Harness(workerPool, logFile != null, explain: true);
        }
        var server = new BrainServer(hello => HarnessFor(hello)(hello),
            received: wireFile == null ? null : message => wireFile.WriteLine(ProtocolJson.Serialize(message)),
            ended: (hello, harness, end) =>
        {
            foreach (var entry in harness.Log) logFile?.WriteLine(ProtocolJson.Serialize(entry));
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
    /// The champion of NoSL hosts and of SL hosts. L0 values its options on the afterstates the screen predicts in NoSL and
    /// on the node's clone in SL, the only pairing Harness takes, whatever --afterstates says.
    /// </summary>
    internal static (ChampionOptions NoSL, ChampionOptions SL) Champions(ParseResult r)
    {
        var slPolicy = r.GetValue(SlPolicy)!;
        var noSl = Champion.Read(r, planElsewhere: slPolicy == "plan") with { OnClone = false };
        var sl = Champion.Read(r, slPolicy);
        // The clone values a fight an option starts with the combat net; without one, Harness refuses SL hosts.
        return (noSl, sl.Macro == "l0" && sl.Leaf.Net != null ? sl with { OnClone = true } : sl);
    }
}
