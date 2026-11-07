using System.CommandLine;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// brain: the brain alone (docs: harness §5), which serves the hosts that connect to the socket, one at a time, with the
/// champion its options name. Each host names its environment: NoSL hosts get --policy's combat brain, SL hosts
/// --sl-policy's (the SL planner by default), with the same macro brain (docs: harness/routing §1). A host may ask for
/// another effort of the SL planner for its run (<see cref="Hello.Effort"/>), which then plays with that preset's
/// budget. Prints a line per run, with the game errors of its search workers.
/// </summary>
internal static class BrainCommand
{
    private static readonly Option<string> Socket = Opt.Of("--socket", BrainSocket.DefaultPath, "the Unix socket to listen at");
    private static readonly Option<string?> Log = Opt.Maybe<string?>("--log", "writes Harness's decision log, one JSON line per decision");
    private static readonly Option<string?> Wire = Opt.Maybe<string?>("--wire", "writes every message the hosts send, one JSON line each (what clones replays)");
    private static readonly Option<string> SlPolicy = Opt.Of("--sl-policy", "plan", "the combat brain of SL hosts; --policy is NoSL hosts'")
        .AcceptOnlyFromAmong(["pimc", "plan", .. Policies.CombatNames]);
    private static readonly ChampionArgs Champion = new(file: true);

    public static Command Command()
    {
        var command = Opt.Command("brain", "the brain alone: serves the hosts that connect, one at a time (docs: harness §5)", [Socket, Log, Wire, SlPolicy],
            Champion.Options);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        var path = r.GetValue(Socket)!;
        var slPolicy = r.GetValue(SlPolicy)!;
        var champion = Champion.Read(r, planElsewhere: slPolicy == "plan");
        var sl = Champion.Read(r, slPolicy);

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
            log: Logs.Factory.CreateLogger("brain"), received: wireFile == null ? null : message => wireFile.WriteLine(ProtocolJson.Serialize(message)),
            ended: (hello, harness, end) =>
        {
            foreach (var entry in harness.Log) logFile?.WriteLine(ProtocolJson.Serialize(entry));
            var errors = workerPool?.TakeGameErrors() ?? [];
            Console.WriteLine($"{hello.Run.RunId} ({hello.Role}) {end.Reason} floor {end.Floor}, {harness.Brains.Values.Sum()} decisions" +
                              (harness.Replans > 0 ? $", {harness.Replans} replans" : "") +
                              (errors.Count > 0 ? $", game Log.Error lines in workers: {errors.Count}" : ""));
            foreach (var e in errors.Take(3)) Console.WriteLine($"  game error: {Logs.FirstLine(e)}");
        });
        Console.WriteLine($"brain: NoSL {champion}; SL {sl}; listening on {path}");
        server.Listen(path);
        return 0;
    }
}
