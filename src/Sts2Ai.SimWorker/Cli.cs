using System.CommandLine;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

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
    private const int MaxRounds = 50;
    private const int MaxActions = 1000;

    private static readonly Option<bool> Verbose = new("--verbose")
    {
        Recursive = true,
        Description = "logs at Debug too: the game's own log, and each failure's stack",
    };

    /// <summary>No response files: an argument that starts with @ is just an argument.</summary>
    private static readonly ParserConfiguration Parser = new() { ResponseFileTokenReplacer = null };

    private static readonly RootCommand Root = Build();

    private static RootCommand Build()
    {
        var root = new RootCommand("The real sts2.dll headless: combats, runs, the brain and evaluation (docs: simulator)") { Verbose };
        Command[] commands =
        [
            ListCommand.Command(), CombatCommand.Command(), BenchCommand.Command(), SelfTest.Command(), ForkBench.Command(),
            RunCommand.Command(), BrainCommand.Command(), Panel.Command(), Fusion.Command(), Serve.Command(),
            ReportCommand.Command(), GateCommand.Command(), LeaderboardCommand.Command(), SeedsCommand.Command(),
            RanksCommand.Command(), WeightsCommand.Command(),
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
            Logs.Sim.LogCritical(e, "{Type}: {Message}", e.GetType().Name, FirstLine(e.Message));
            return 70;
        }
    }

    /// <summary>The action of a command that needs the game: boots it, and installs the pump on this thread, first.</summary>
    public static Func<ParseResult, int> Game(Func<ParseResult, Pump, int> action) => r =>
    {
        HeadlessGame.Boot();
        return action(r, Pump.InstallOnCurrentThread());
    };

    /// <summary>The commit this program was built from, as the build stamped it.</summary>
    public static readonly string Commit =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').ElementAtOrDefault(1) ?? "unknown";

    /// <summary>The encounter of this id, any case, and its act.</summary>
    public static (EncounterModel Encounter, int Act) Encounter(string id)
    {
        var encounter = ModelDb.AllEncounters.FirstOrDefault(e => e.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException($"no encounter {id} (see list)");
        return (encounter, Enumerable.Range(0, ModelDb.ActsByIndex.Count).FirstOrDefault(i => ModelDb.ActsByIndex[i].Any(a => a.AllEncounters.Contains(encounter))));
    }

    /// <summary>Plays until the combat ends or hits the round/action cap. Calls <paramref name="beforeAction"/> before each action.</summary>
    public static void PlayOut(CombatSession session, ICombatPolicy policy, Action<CombatSession>? beforeAction = null)
    {
        while (!IsCapped(session))
        {
            beforeAction?.Invoke(session);
            session.Execute(policy.Choose(Decision(session)));
        }
    }

    /// <summary>Whether a combat is over or at the round/action cap.</summary>
    public static bool IsCapped(CombatSession session) =>
        session.IsOver || session.State.RoundNumber > MaxRounds || session.ActionsExecuted >= MaxActions;

    public static CombatDecision Decision(CombatSession session) => new(session.LegalActions(), session.Observation);

    /// <summary>A game-style seed string (the game's alphabet, 12 chars), derived deterministically from (seed, index).</summary>
    public static string MakeSeed(int seed, int index)
    {
        const string alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var rng = new Random(unchecked(seed * 1_000_003 + index * 7_919) ^ 0x5eed);
        return string.Concat(Enumerable.Range(0, 12).Select(_ => alphabet[rng.Next(alphabet.Length)]));
    }

    /// <summary>How to start this program again: its apphost, or dotnet with its dll if that is how it was started.</summary>
    public static IReadOnlyList<string> SelfCommand()
    {
        var exe = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(exe) == "dotnet" ? [exe, typeof(Cli).Assembly.Location] : [exe];
    }

    public static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    /// <summary>The run records in these files (comma-separated).</summary>
    public static List<RunRecord> ReadRecords(string files) =>
        files.Split(',').SelectMany(File.ReadAllLines).Where(l => l.Length > 0).Select(ProtocolJson.Deserialize<RunRecord>).ToList();
}

/// <summary>
/// What pimc values its leaves with (docs: combat-brain §4): --leaf FILE's weights (the default ones without it), and
/// --combat-net FILE's net, which then values the leaves still in progress at those weights' prices.
/// </summary>
internal sealed record LeafChoice(LeafFile? Weights, string? WeightsPath, CombatNet? Net, string? NetPath)
{
    public static LeafChoice Read(string? weightsPath, string? netPath)
    {
        var weights = weightsPath == null ? null : ProtocolJson.Deserialize<LeafFile>(File.ReadAllText(weightsPath));
        var net = netPath == null ? null : CombatNet.Load(ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(netPath)), weights?.Weights);
        return new(weights, weightsPath, net, netPath);
    }

    public bool IsDefault => Weights == null && Net == null;

    /// <summary>What follows pimc in the brain's name: the weights' name, then the net's, each when given.</summary>
    public string Suffix => (Weights != null ? $" {Weights.Name}" : "") + (Net != null ? $" {Net.Name}" : "");

    public ICombatLeaf Leaf => Net ?? (ICombatLeaf)new HandLeaf(Weights?.Weights);

    /// <summary>The options that give workers the same leaf: they start from the same folder, by full path anyway.</summary>
    public IReadOnlyList<string>? WorkerOptions()
    {
        var options = new List<string>();
        if (WeightsPath != null) options.AddRange(["--leaf", Path.GetFullPath(WeightsPath)]);
        if (NetPath != null) options.AddRange(["--combat-net", Path.GetFullPath(NetPath)]);
        return options.Count > 0 ? options : null;
    }
}
