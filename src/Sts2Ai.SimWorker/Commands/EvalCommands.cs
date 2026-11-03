using System.CommandLine;
using System.Text.Json;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>report: the evaluation report over run records (docs: evaluation/metrics §7), PATH.json and PATH.md.</summary>
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
/// gate: the full-run gate (docs: evaluation/gates §6), the challenger's records against the champion's report, paired by
/// seed, with an SPRT. The challenger is named by its run options, which must be the ones its records were played
/// with, and its runs must be of the champion's character and ascension. On a pass, --promote PATH writes the
/// challenger's report to PATH and makes it the champion, naming its weights files where they are, in weights/all
/// (<see cref="ChampionOptions.Promoted"/>); its report faces the next challengers, so it must cover the
/// whole seed set. The SPRT may pass early: play the rest, then promote.
/// </summary>
internal static class GateCommand
{
    private static readonly Option<string> ChampionPath = Opt.Required<string>("--champion", "the champion file to beat, such as eval/silent/champions/nosl.json");
    private static readonly Option<string> Records = Opt.Required<string>("--records", "the challenger's run record files, comma-separated");
    private static readonly Option<string> Metric = Opt.Of("--metric", "floor", "what the SPRT compares").AcceptOnlyFromAmong("floor", "win");
    private static readonly Option<double?> Delta = Opt.Maybe<double?>("--delta", "the difference the SPRT tells from none (default: 1.5 floors, or 0.1 wins)");
    private static readonly Option<double> Alpha = Opt.Of("--alpha", 0.05, "the SPRT's chance to pass a challenger no better (with --not-worse: worse by --delta)");
    private static readonly Option<double> Beta = Opt.Of("--beta", 0.05, "the SPRT's chance to fail a challenger better by --delta (with --not-worse: no worse)");
    private static readonly Option<bool> NotWorse = Opt.Of("--not-worse", false,
        "for a change meant to be cheaper, not stronger: passes a challenger that is not worse (H0: worse by --delta, H1: no difference)");
    private static readonly Option<string?> Promote = Opt.Maybe<string?>("--promote", "on a pass, the challenger's report path, and it becomes the champion");
    private static readonly Option<PlayEnvironment?> Env = Opt.Maybe<PlayEnvironment?>("--env", "the challenger's environment, which must be the champion's");
    private static readonly ChampionArgs Challenger = new(file: false);

    public static Command Command()
    {
        var command = Opt.Command("gate", "the challenger's records against the champion's report, paired by seed, with an SPRT",
            [ChampionPath, Records, Metric, Delta, Alpha, Beta, NotWorse, Promote, Env], Challenger.Options);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        var championPath = r.GetValue(ChampionPath)!;
        var file = ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(championPath));
        var champion = ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(file.Report));
        if (r.GetValue(Env) is { } env && env.ToString() != file.Env) throw new CliException($"the challenger plays in {env}, the champion in {file.Env}");
        var runs = RunRecord.Read(r.GetValue(Records)!);
        var challenger = Challenger.Read(r);
        if (challenger.Explores) throw new CliException("--macro-explore and --combat-explore are for self-play; a challenger plays its best");
        if (runs.FirstOrDefault(run => run.Config.Champion != challenger.Name) is { } other)
            throw new CliException($"run {other.Seed} was played by {other.Config.Champion}, not by the options given ({challenger.Name})");
        var metric = r.GetValue(Metric)!;
        double delta = r.GetValue(Delta) ?? (metric == "floor" ? 1.5 : 0.1);
        IReadOnlyList<(RunRecord, RunRecord)> pairs;
        try
        {
            pairs = Gate.Pairs(champion, runs);
        }
        catch (ArgumentException e)
        {
            throw new CliException(e.Message);
        }
        var result = Gate.Sprt(pairs, metric, delta, r.GetValue(Alpha), r.GetValue(Beta), r.GetValue(NotWorse));
        Console.WriteLine($"champion: {file.Champion} ({file.Report})");
        Console.WriteLine($"challenger: {challenger.Name}, {runs.Count} runs");
        Console.WriteLine(result.Summary());
        if (result.Verdict != "pass" || r.GetValue(Promote) is not { } outPath) return result.Verdict == "pass" ? 0 : 1;

        var seedFile = Layout.Seeds(champion.SeedSet);
        var report = ReportCommand.Of(runs, champion.SeedSet, RunRecord.SeedsLeft(r.GetValue(Records)!, EvalReport.ReadSeeds(seedFile)));
        if (report.Runs.Count != report.SetSize)
            throw new CliException($"not promoted: {report.Runs.Count} of {champion.SeedSet}'s {report.SetSize} seeds were run; the champion's report needs them all");
        var options = challenger.Promoted(Enum.Parse<PlayEnvironment>(file.Env), report.Runs[0].Character, report.Runs[0].Ascension);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath + ".json", report.Json());
        File.WriteAllText(outPath + ".md", report.Markdown(champion));
        var promoted = new ChampionFile(file.Env, challenger.Name, options, outPath + ".json", report.Date, result);
        File.WriteAllText(championPath, ProtocolJson.Serialize(promoted) + "\n");
        Console.WriteLine($"promoted: {championPath} now names {challenger.Name}");
        return 0;
    }
}

/// <summary>leaderboard: a character's leaderboards over every report in its folder (docs: evaluation/metrics §9).</summary>
internal static class LeaderboardCommand
{
    private static readonly Option<string> Character = Opt.Of("--character", "SILENT", "the character, whose eval folder holds the reports and champions");

    public static Command Command()
    {
        var command = Opt.Command("leaderboard", "a character's leaderboards over every report in eval/CHARACTER/reports (docs: evaluation/metrics §9)", [Character]);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        var folder = Layout.Eval(r.GetValue(Character)!);
        var reports = Directory.GetFiles(Path.Combine(folder, "reports"), "*.json").Order()
            .ToDictionary(Path.GetFullPath, path => ProtocolJson.Deserialize<EvalReport>(File.ReadAllText(path)));
        var championDir = Path.Combine(folder, "champions");
        var champions = Directory.Exists(championDir)
            ? Directory.GetFiles(championDir, "*.json").Select(path => Path.GetFullPath(ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(path)).Report))
                .Where(reports.ContainsKey).Select(path => reports[path]).ToList()
            : [];
        var markdown = Leaderboard.Markdown(reports.Values.ToList(), champions);
        File.WriteAllText(Path.Combine(folder, "leaderboard.md"), markdown);
        Console.Write(markdown);
        return 0;
    }
}

/// <summary>seeds: a seed file's seeds, MakeSeed(base, 0..n-1), one per line.</summary>
internal static class SeedsCommand
{
    private static readonly Option<int> Base = Opt.Required<int>("--base", "the seed base (docs: evaluation/metrics §6)");
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
