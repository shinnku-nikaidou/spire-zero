using System.CommandLine;
using System.Text.Json;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>report: the evaluation report over run records, PATH.json and PATH.md.</summary>
internal static class ReportCommand
{
    private static readonly Option<string> Seeds = Opt.Required<string>("--seeds", "the seed file the records were played on");
    private static readonly Option<string> Records = Opt.Required<string>("--records", "run record files, comma-separated");
    private static readonly Option<string> Out = Opt.Required<string>("--out", "the report's path, without .json or .md");
    private static readonly Option<string?> Previous = Opt.Maybe<string?>("--previous", "an earlier report to compare with");

    public static Command Command()
    {
        var command = Opt.Command("report", "the evaluation report over these records: PATH.json and PATH.md", [Seeds, Records, Out, Previous]);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        var seedFile = r.GetValue(Seeds)!;
        var outPath = r.GetValue(Out)!;
        var runs = RunRecord.Read(r.GetValue(Records)!);
        var seeds = RunRecord.SeedsLeft(r.GetValue(Records)!, EvalReport.ReadSeeds(seedFile));
        var previous = r.GetValue(Previous) is { } previousPath ? ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(previousPath)) : null;
        var report = Of(runs, Path.GetFileNameWithoutExtension(seedFile), seeds);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath + ".json", report.Json());
        var markdown = report.Markdown(previous);
        File.WriteAllText(outPath + ".md", markdown);
        Console.Write(markdown);
        return 0;
    }

    /// <summary><see cref="EvalReport.Of"/> dated today, its refusal of the records a mistake in the command line.</summary>
    public static EvalReport Of(IReadOnlyList<RunRecord> runs, string seedSet, IReadOnlyList<string> seeds)
    {
        try
        {
            return EvalReport.Of(runs, seedSet, seeds, DateTime.Now.ToString("yyyy-MM-dd"));
        }
        catch (ArgumentException e)
        {
            throw new CliException(e.Message);
        }
    }
}

/// <summary>
/// gate: the gate of a change to the code, the challenger's records against the champion's
/// report of their character, paired by seed, with an SPRT. The challenger is named by its run options, which must be
/// the ones its records were played with, and its runs must be of that report's ascension. It judges and writes
/// nothing: new nets are judged and promoted by <c>promote</c>.
/// </summary>
internal static class GateCommand
{
    private static readonly Option<string> ChampionPath = Opt.Required<string>("--champion", "the champion file to beat, such as eval/champions/nosl.json");
    private static readonly Option<string> Records = Opt.Required<string>("--records", "the challenger's run record files, comma-separated");
    private static readonly Option<double> Delta = Opt.Of("--delta", 0.1, "the challenger's share of the wins only one side has, above an even one, that the SPRT tells from none");
    private static readonly Option<double> Alpha = Opt.Of("--alpha", 0.05, "the SPRT's chance to pass a challenger no better (with --not-worse: worse by --delta)");
    private static readonly Option<double> Beta = Opt.Of("--beta", 0.05, "the SPRT's chance to fail a challenger better by --delta (with --not-worse: no worse)");
    private static readonly Option<bool> NotWorse = Opt.Of("--not-worse", false,
        "for a change meant to be cheaper, not stronger: passes a challenger that is not worse (H0: worse by --delta, H1: no difference)");
    private static readonly Option<PlayEnvironment?> Env = Opt.Maybe<PlayEnvironment?>("--env", "the challenger's environment, which must be the champion's");
    private static readonly ChampionArgs Challenger = new(file: false);

    public static Command Command()
    {
        var command = Opt.Command("gate", "the challenger's records against the champion's report, paired by seed, with an SPRT",
            [ChampionPath, Records, Delta, Alpha, Beta, NotWorse, Env], Challenger.Options);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        var championPath = r.GetValue(ChampionPath)!;
        var file = ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(championPath));
        if (r.GetValue(Env) is { } env && env.ToString() != file.Env) throw new CliException($"the challenger plays in {env}, the champion in {file.Env}");
        var runs = RunRecord.Read(r.GetValue(Records)!);
        if (runs.Count == 0) throw new CliException("no run in the records");
        var character = runs[0].Character;
        if (!file.Reports.TryGetValue(character, out var reportPath))
            throw new CliException($"the champion has no report of {character} to pair with; play one with its options first");
        var champion = ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(reportPath));
        var challenger = Challenger.Read(r);
        if (challenger.Explores) throw new CliException("--explore, --macro-explore, --favor and --refuse are for self-play; a challenger plays its best");
        if (runs.FirstOrDefault(run => run.Config.Champion != challenger.Name) is { } other)
            throw new CliException($"run {other.Seed} was played by {other.Config.Champion}, not by the options given ({challenger.Name})");
        IReadOnlyList<(RunRecord, RunRecord)> pairs;
        try
        {
            pairs = Gate.Pairs(champion, runs);
        }
        catch (ArgumentException e)
        {
            throw new CliException(e.Message);
        }
        var result = Gate.Sprt(pairs, r.GetValue(Delta), r.GetValue(Alpha), r.GetValue(Beta), r.GetValue(NotWorse));
        Console.WriteLine($"champion: {file.Champion} ({reportPath})");
        Console.WriteLine($"challenger: {challenger.Name}, {runs.Count} runs");
        Console.WriteLine(result.Summary());
        return result.Verdict == "pass" ? 0 : 1;
    }
}

/// <summary>
/// promote: the gate of a pair of nets and their promotion. The nets are every character's,
/// so each character has two reports of one gate, the champion's nets and the new ones on the same seed set at the same
/// commit; the reports are paired by seed and judged together (<see cref="NetGate"/>). The challenger is the champion
/// file's champion with the nets given (--value, --combat-net, --f-net), and must be what played its reports. On a pass the
/// champion file names the challenger: its options with the new nets where they are in weights/all, the challenger's
/// reports and the gate, and the characters' leaderboards are written again. Each environment has its own nets
/// (plans/2026-11-18-1340-nosl-v0.md), so no other champion file changes. --look prints the
/// verdict and writes nothing, and may pair reports of part of the seed set.
/// </summary>
internal static class PromoteCommand
{
    private static readonly Option<string> ChampionPath = Opt.Required<string>("--champion", "the champion file to beat and, on a pass, to rewrite, such as eval/champions/sl.json");
    private static readonly Option<string?> Value = Opt.Maybe<string?>("--value", "the challenger's V, weights/all/NAME.json (default: the champion's)");
    private static readonly Option<string?> CombatNet = Opt.Maybe<string?>("--combat-net", "the challenger's combat net, weights/all/NAME.json (default: the champion's)");
    private static readonly Option<string?> Saves = Opt.Maybe<string?>("--f-net", "the challenger's f, weights/all/NAME.json (default: the champion's)");
    private static readonly Option<string> Reports = Opt.Required<string>("--reports",
        "the challenger's reports, one per character, comma-separated: eval/CHARACTER/reports/NAME.json");
    private static readonly Option<string> Against = Opt.Required<string>("--against",
        "the champion's reports of the same gate, one per character, comma-separated");
    private static readonly Option<bool> Look = Opt.Of("--look", false, "prints the verdict and writes nothing; the reports may be of part of the seed set");

    public static Command Command()
    {
        var command = Opt.Command("promote", "judges new nets by every character's two reports of one gate, paired by seed, and on a pass makes them the champion's",
            [ChampionPath, Value, CombatNet, Saves, Reports, Against, Look]);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        var championPath = r.GetValue(ChampionPath)!;
        var file = ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(championPath));
        // A promotion writes one set of nets for every character; a file that names nets by character is written by hand.
        if (file.Characters != null) throw new CliException($"{championPath} names nets by character (characters): no promotion writes it");
        bool look = r.GetValue(Look);
        // The new nets, by the champion file's keys, each where a champion's weights files are.
        var nets = new Dictionary<string, string>();
        foreach (var (key, given) in new[] { ("value", r.GetValue(Value)), ("combat-net", r.GetValue(CombatNet)), ("f-net", r.GetValue(Saves)) })
        {
            if (given == null) continue;
            if (Path.GetDirectoryName(Path.GetFullPath(given)) != Path.GetFullPath(Layout.Weights))
                throw new CliException($"{given} is not in {Layout.Weights}, where a champion's weights files are");
            nets[key] = $"{Layout.Weights.Replace(Path.DirectorySeparatorChar, '/')}/{Path.GetFileName(given)}";
        }
        var (challenger, options) = With(championPath, file, nets);
        if (challenger.Name == file.Champion) throw new CliException($"the nets given are the champion's own ({file.Champion}): give the new ones, --value and --combat-net");

        var theirs = Read(r.GetValue(Reports)!);
        var ours = Read(r.GetValue(Against)!);
        if (ours.Values.FirstOrDefault(c => c.Report.Config.Champion != file.Champion) is { } stale)
            throw new CliException($"{stale.Path} was played by {stale.Report.Config.Champion}, not by the champion ({file.Champion})");
        if (theirs.Values.FirstOrDefault(c => c.Report.Config.Champion != challenger.Name) is { } other)
            throw new CliException($"{other.Path} was played by {other.Report.Config.Champion}, not by the champion with the nets given ({challenger.Name})");
        if (theirs.Values.Concat(ours.Values).FirstOrDefault(c => c.Report.Config.Env != file.Env) is { } elsewhere)
            throw new CliException($"{elsewhere.Path} was played in {elsewhere.Report.Config.Env}, the champion in {file.Env}");
        if (!theirs.Keys.ToHashSet().SetEquals(ours.Keys))
            throw new CliException($"the challenger's reports are of {string.Join(", ", theirs.Keys)}, the champion's of {string.Join(", ", ours.Keys)}");
        if (theirs.Values.Select(c => c.Report.Config.Commit).Concat(ours.Values.Select(c => c.Report.Config.Commit)).Distinct().ToList() is { Count: > 1 } commits)
            throw new CliException($"the reports were played at more than one commit ({string.Join(", ", commits)}): both sides of a gate play at the same one");
        if (!look)
        {
            // A report the champion file names faces the readers and the leaderboards: whole, and with nothing that voids it.
            foreach (var (path, report) in theirs.Values.Concat(ours.Values))
            {
                if (report.Runs.Count != report.SetSize) throw new CliException($"not promoted: {path} has {report.Runs.Count} of {report.SeedSet}'s {report.SetSize} seeds");
                if (report.Metrics.Void) throw new CliException($"not promoted: {path} is void (random decisions, failed or truncated runs, or game errors)");
            }
            foreach (var (character, side) in theirs)
                if (Path.GetDirectoryName(Path.GetFullPath(side.Path)) != Path.GetFullPath(Path.Combine(Layout.Eval(character), "reports")))
                    throw new CliException($"not promoted: {side.Path} is not in {Path.Combine(Layout.Eval(character), "reports")}, where {character}'s reports are");
        }

        NetGate.Result gate;
        try
        {
            gate = NetGate.Judge([.. theirs.Select(c => (ours[c.Key].Report, c.Value.Report))]);
        }
        catch (ArgumentException e)
        {
            throw new CliException(e.Message);
        }
        Console.WriteLine($"champion: {file.Champion} ({championPath})");
        Console.WriteLine($"challenger: {challenger.Name}");
        Console.WriteLine(gate.Summary());
        if (!gate.Promote) return 1;
        if (look) return 0;

        string since = DateTime.Now.ToString("yyyy-MM-dd");
        Write(championPath, file.Promoted(challenger.Name, options, since, theirs.ToDictionary(c => c.Key, c => c.Value.Path), gate));
        Console.WriteLine($"promoted: {championPath} now names {challenger.Name}");
        foreach (var character in theirs.Keys) LeaderboardCommand.Write(character);
        return 0;
    }

    /// <summary>The champion of a champion file with the nets of <paramref name="nets"/> it has a key for, and its options then:
    /// the file's own, which stay as they are written (a preset by its name), with those nets.</summary>
    private static (ChampionOptions Champion, Dictionary<string, string> Options) With(string path, ChampionFile file, IReadOnlyDictionary<string, string> nets)
    {
        var options = file.Options.ToDictionary(o => o.Key, o => nets.GetValueOrDefault(o.Key, o.Value));
        var given = nets.Where(n => file.Options.ContainsKey(n.Key)).SelectMany(n => new[] { "--" + n.Key, n.Value }).ToList();
        return (ChampionArgs.Of(path, given), options);
    }

    private static void Write(string path, ChampionFile file) => File.WriteAllText(path, ProtocolJson.Serialize(file) + "\n");

    /// <summary>A report and the file it is in.</summary>
    private sealed record Side(string Path, EvalReport Report);

    /// <summary>The reports of these files, by character; a character twice is refused.</summary>
    private static Dictionary<string, Side> Read(string paths)
    {
        var reports = new Dictionary<string, Side>();
        foreach (var path in paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var report = ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(path));
            if (report.Runs.Count == 0) throw new CliException($"{path} has no run");
            if (!reports.TryAdd(report.Runs[0].Character, new(path, report))) throw new CliException($"{path}: a second report of {report.Runs[0].Character}");
        }
        return reports;
    }
}

/// <summary>leaderboard: a character's leaderboards over every report in its folder.</summary>
internal static class LeaderboardCommand
{
    private static readonly Option<string> Character = Opt.Of("--character", "SILENT", "the character, whose eval folder holds its reports");

    public static Command Command()
    {
        var command = Opt.Command("leaderboard", "a character's leaderboards over every report in eval/CHARACTER/reports", [Character]);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        Console.Write(Write(r.GetValue(Character)!));
        return 0;
    }

    /// <summary>Writes the character's leaderboard.md over every report in its folder, and returns it.</summary>
    public static string Write(string character)
    {
        var folder = Layout.Eval(character);
        var reports = Directory.GetFiles(Path.Combine(folder, "reports"), "*.json").Order()
            .ToDictionary(Path.GetFullPath, path => ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(path)));
        // Each environment's champion, marked by its report of this character, if it has one.
        var champions = Directory.Exists(Layout.Champions)
            ? Directory.GetFiles(Layout.Champions, "*.json").Select(path => ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(path)).Reports)
                .Where(byCharacter => byCharacter.ContainsKey(character)).Select(byCharacter => Path.GetFullPath(byCharacter[character]))
                .Where(reports.ContainsKey).Select(path => reports[path]).ToList()
            : [];
        var markdown = Leaderboard.Markdown([.. reports.Values], champions);
        File.WriteAllText(Path.Combine(folder, "leaderboard.md"), markdown);
        return markdown;
    }
}

/// <summary>seeds: a seed file's seeds, MakeSeed(base, 0..n-1), one per line.</summary>
internal static class SeedsCommand
{
    private static readonly Option<int> Base = Opt.Required<int>("--base", "the seed base");
    private static readonly Option<int> N = Opt.Of("--n", 300, "seeds");

    public static Command Command()
    {
        var command = Opt.Command("seeds", "a seed file's seeds: MakeSeed(base, 0..n-1), one per line", [Base, N]);
        command.SetAction(r =>
        {
            foreach (int i in Enumerable.Range(0, r.GetValue(N))) Console.WriteLine(GameSeeds.MakeSeed(r.GetValue(Base), i));
        });
        return command;
    }
}
