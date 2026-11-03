using System.CommandLine;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Sts2Ai.SimWorker;

/// <summary>
/// The sim's command line (System.CommandLine): `--help` lists the commands, `COMMAND --help` a command's options. Each
/// command is a class with its options (Args.cs has the groups several commands share). stdout carries a command's results
/// only, which scripts read; logs go to stderr (<see cref="Logs"/>). Exit codes: 0 done; 1 done, with failures (combats
/// or runs that failed, a gate that did not pass); 2 a mistake in the command line or in a file it names; 70 a bug,
/// whose stack is logged.
/// </summary>
internal static class Cli
{
    private static readonly Option<bool> Verbose = new("--verbose")
    {
        Recursive = true,
        Description = "logs at Debug too: the game's own log",
    };

    /// <summary>No response files: an argument that starts with @ is just an argument.</summary>
    private static readonly ParserConfiguration Parser = new() { ResponseFileTokenReplacer = null };

    private static readonly RootCommand Root = Build();

    private static RootCommand Build()
    {
        var root = new RootCommand("The real sts2.dll headless: combats, runs, the brain and evaluation (docs: simulator)") { Verbose };
        Command[] commands =
        [
            ListCommand.Command(), TablesCommand.Command(), CombatCommand.Command(), BenchCommand.Command(), SelfTest.Command(),
            RunCommand.Command(), BrainCommand.Command(), Serve.Command(),
            ReportCommand.Command(), GateCommand.Command(), LeaderboardCommand.Command(), SeedsCommand.Command(), ClonesCommand.Command(),
        ];
        foreach (var command in commands) root.Subcommands.Add(command);
        return root;
    }

    /// <summary>The command line parsed, with the options of the champion file that --champion names merged in.</summary>
    public static ParseResult Parse(IReadOnlyList<string> args) => ChampionFileOption.Merge(Root, args, Root.Parse(args, Parser), Parser);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string[] args)
    {
        using var logs = Logs.Start(Root.Parse(args, Parser).GetValue(Verbose));
        try
        {
            var parsed = Parse(args);
            if (parsed.Errors.Count == 0) return parsed.Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
            foreach (var error in parsed.Errors) Logs.Sim.LogError("{Error}", error.Message);
            var command = parsed.CommandResult.Command;
            Logs.Sim.LogError("see {Help}", command == Root ? "--help" : $"{command.Name} --help");
            return 2;
        }
        catch (Exception e) when (e is CliException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Logs.Sim.LogError("{Message}", e.Message);
            return 2;
        }
        catch (Exception e)
        {
            Logs.Sim.LogCritical(e, "{Type}: {Message}", e.GetType().Name, Logs.FirstLine(e.Message));
            return 70;
        }
    }

    /// <summary>The action of a command that needs the game: boots it, and installs the pump on this thread, first.</summary>
    public static Func<ParseResult, int> Game(Func<ParseResult, Pump, int> action) => r =>
    {
        HeadlessGame.Boot();
        return action(r, Pump.InstallOnCurrentThread());
    };
}
