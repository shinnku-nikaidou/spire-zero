using System.Globalization;
using System.Text;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>A proportion with its Wilson 95% interval.</summary>
public sealed record Rate(int Hits, int Of, double Value, double Low, double High)
{
    public static Rate Wilson(int hits, int of)
    {
        if (of == 0) return new(0, 0, 0, 0, 1);
        const double z = 1.96;
        double p = (double)hits / of, z2 = z * z / of;
        double center = (p + z2 / 2) / (1 + z2), half = z * Math.Sqrt(p * (1 - p) / of + z2 / (4 * of)) / (1 + z2);
        return new(hits, of, p, Math.Max(0, center - half), Math.Min(1, center + half));
    }

    public override string ToString() => FormattableString.Invariant($"{Value:P1} ({Hits}/{Of}, {Low:P1}–{High:P1})");
}

/// <summary>HP going into the first boss of an act, over the runs that got there.</summary>
public sealed record BossEntry(int Act, int Reached, double HpMean, double HpFractionMean);

/// <summary>
/// The metrics of docs: evaluation/metrics. Failed and truncated runs count as not won; they are also listed with the
/// must-be-zero metrics, and any of those above zero makes the evaluation void.
/// </summary>
public sealed record Metrics(
    Rate Wins, double FloorMean, double FloorMedian, IReadOnlyList<BossEntry> Bosses,
    int RandomDecisions, int Failed, int Truncated, int GameErrors,
    IReadOnlyDictionary<string, int> Surfaces, double ElitesPerRun, Rate CardRewardSkips, double GoldMean,
    double MsPerDecision, double SecondsPerRun)
{
    /// <summary>The brain name every placeholder goes by (docs: harness/routing §1: a random decision voids the evaluation).</summary>
    public const string RandomBrain = "random";

    public bool Void => RandomDecisions + Failed + Truncated + GameErrors > 0;

    public static Metrics Of(IReadOnlyList<RunRecord> runs)
    {
        var floors = runs.Select(r => (double)r.Floor).Order().ToList();
        var bosses = runs.SelectMany(r => r.Combats.Where(c => c.Room == "Boss").GroupBy(c => c.Act).Select(g => g.First()))
            .GroupBy(c => c.Act).OrderBy(g => g.Key)
            .Select(g => new BossEntry(g.Key, g.Count(), g.Average(c => c.Hp), g.Average(c => (double)c.Hp / c.MaxHp))).ToList();
        var surfaces = runs.SelectMany(r => r.Surfaces).GroupBy(kv => kv.Key).OrderByDescending(g => g.Sum(kv => kv.Value))
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        int decisions = surfaces.Values.Sum();
        double ms = runs.Sum(r => r.ElapsedMs);
        return new(
            Rate.Wilson(runs.Count(r => r.Outcome == "won"), runs.Count),
            floors.Count == 0 ? 0 : floors.Average(),
            floors.Count == 0 ? 0 : (floors[(floors.Count - 1) / 2] + floors[floors.Count / 2]) / 2,
            bosses,
            runs.Sum(r => r.Brains.GetValueOrDefault(RandomBrain)),
            runs.Count(r => r.Outcome == "failed"),
            runs.Count(r => r.Outcome == "truncated"),
            runs.Sum(r => r.GameErrors),
            surfaces,
            runs.Count == 0 ? 0 : runs.Average(r => r.Combats.Count(c => c.Room == "Elite")),
            Rate.Wilson(runs.Sum(r => r.CardRewardsSkipped), runs.Sum(r => r.CardRewards)),
            runs.Count == 0 ? 0 : runs.Average(r => r.Gold),
            decisions == 0 ? 0 : ms / decisions,
            runs.Count == 0 ? 0 : ms / 1000 / runs.Count);
    }
}

/// <summary>
/// One evaluation (docs: evaluation/metrics §7): what was played, on which seeds, the metrics, and every run's record.
/// <see cref="Markdown"/> is the one-page report; the JSON is this record.
/// </summary>
/// <param name="SeedSet">The seed file's name; <paramref name="SetSize"/> seeds are in it, and every one must be run.</param>
public sealed record EvalReport(RunConfig Config, string SeedSet, int SetSize, string Date, Metrics Metrics, IReadOnlyList<RunRecord> Runs)
{
    /// <summary>
    /// The report over <paramref name="runs"/>, which may come from several processes. They must share one config,
    /// one character and one ascension, and each run a different seed of the set.
    /// </summary>
    /// <exception cref="ArgumentException">The runs disagree on the config, the character or the ascension, repeat a
    /// seed, or play one not in the set.</exception>
    public static EvalReport Of(IReadOnlyList<RunRecord> runs, string seedSet, IReadOnlyList<string> seeds, string date)
    {
        if (runs.Count == 0) throw new ArgumentException("no runs");
        var config = runs[0].Config;
        if (runs.FirstOrDefault(r => r.Config != config) is { } other) throw new ArgumentException($"run {other.Seed} was played by {other.Config}, not {config}");
        if (runs.FirstOrDefault(r => (r.Character, r.Ascension) != (runs[0].Character, runs[0].Ascension)) is { } stranger)
            throw new ArgumentException($"run {stranger.Seed} was played as {stranger.Character} at ascension {stranger.Ascension}, run {runs[0].Seed} as {runs[0].Character} at ascension {runs[0].Ascension}");
        if (runs.GroupBy(r => r.Seed).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new ArgumentException($"seed {twice.Key} was run {twice.Count()} times");
        var set = seeds.ToHashSet();
        if (runs.FirstOrDefault(r => !set.Contains(r.Seed)) is { } stray) throw new ArgumentException($"seed {stray.Seed} is not in {seedSet}");
        var order = seeds.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        var sorted = runs.OrderBy(r => order[r.Seed]).ToList();
        return new(config, seedSet, seeds.Count, date, Metrics.Of(sorted), sorted);
    }

    public string Markdown(EvalReport? previous = null)
    {
        var m = Metrics;
        var md = new StringBuilder();
        void Line(FormattableString text) => md.AppendLine(text.ToString(CultureInfo.InvariantCulture));

        Line($"# Evaluation {Date}: {Config.Env} {Config.Rung}, {SeedSet}");
        md.AppendLine();
        if (m.Void) Line($"**Void** (docs: evaluation/metrics §2): {VoidReasons()}.");
        if (Runs.Count < SetSize) Line($"**Partial:** {Runs.Count} of the set's {SetSize} seeds were run.");
        if (m.Void || Runs.Count < SetSize) md.AppendLine();
        md.AppendLine("| | |").AppendLine("|---|---|");
        Line($"| game | {Config.Game} |");
        Line($"| commit | `{Config.Commit}` |");
        Line($"| champion | {Config.Champion} |");
        Line($"| environment, rung | {Config.Env}, {Config.Rung} |");
        Line($"| seed set | {SeedSet}: {Runs.Count} of {SetSize} runs, {string.Join(" ", Runs.Select(r => r.Character).Distinct())} A{Runs[0].Ascension} |");
        md.AppendLine();
        md.AppendLine("## Main").AppendLine();
        Line($"- win rate: {m.Wins}");
        Line($"- floor reached: mean {m.FloorMean:F2}, median {m.FloorMedian:F1}");
        foreach (var b in m.Bosses)
            Line($"- act {b.Act + 1} boss: reached in {b.Reached} of {Runs.Count} runs, entering with {b.HpMean:F1} HP ({b.HpFractionMean:P0} of max) on average");
        md.AppendLine();
        md.AppendLine("## Must be zero").AppendLine();
        Line($"- random decisions (placeholders): {m.RandomDecisions}");
        Line($"- failed runs: {m.Failed}; truncated runs: {m.Truncated}");
        Line($"- game errors: {m.GameErrors}");
        foreach (var (failure, count) in Runs.Where(r => r.Failure != null).GroupBy(r => r.Failure!).Select(g => (g.Key, g.Count())).OrderByDescending(p => p.Item2))
            Line($"  - {count}x {failure}");
        md.AppendLine();
        md.AppendLine("## Auxiliary").AppendLine();
        Line($"- elites fought per run: {m.ElitesPerRun:F2}; card rewards skipped: {m.CardRewardSkips}; gold at the end: {m.GoldMean:F0} on average");
        Line($"- compute: {m.SecondsPerRun:F1} s per run, {m.MsPerDecision:F1} ms per decision");
        Line($"- surfaces: {string.Join(", ", m.Surfaces.Select(kv => $"{kv.Key} {kv.Value}"))}");
        if (previous != null)
        {
            md.AppendLine();
            md.AppendLine($"## Against {previous.Date} ({previous.Config.Champion}, commit `{previous.Config.Commit}`)").AppendLine();
            if (previous.SeedSet != SeedSet || previous.Config.Env != Config.Env || previous.Config.Rung != Config.Rung)
                Line($"Not comparable: that one is {previous.Config.Env} {previous.Config.Rung} on {previous.SeedSet}.");
            else
            {
                var p = previous.Metrics;
                md.AppendLine("| | then | now |").AppendLine("|---|---|---|");
                Line($"| win rate | {p.Wins} | {m.Wins} |");
                Line($"| floor mean | {p.FloorMean:F2} | {m.FloorMean:F2} |");
                foreach (int act in p.Bosses.Select(b => b.Act).Union(m.Bosses.Select(b => b.Act)).Order())
                    Line($"| act {act + 1} boss reached | {p.Bosses.FirstOrDefault(b => b.Act == act)?.Reached ?? 0} | {m.Bosses.FirstOrDefault(b => b.Act == act)?.Reached ?? 0} |");
                Line($"| random decisions | {p.RandomDecisions} | {m.RandomDecisions} |");
                Line($"| failed | {p.Failed} | {m.Failed} |");
            }
        }
        return md.ToString();
    }

    private string VoidReasons()
    {
        var m = Metrics;
        var reasons = new List<string>();
        if (m.RandomDecisions > 0) reasons.Add($"{m.RandomDecisions} random decisions");
        if (m.Failed > 0) reasons.Add($"{m.Failed} failed runs");
        if (m.Truncated > 0) reasons.Add($"{m.Truncated} truncated runs");
        if (m.GameErrors > 0) reasons.Add($"{m.GameErrors} game errors");
        return string.Join(", ", reasons);
    }

    /// <summary>The seeds in a seed file: one per line; blank lines and lines from '#' on are skipped.</summary>
    public static List<string> ReadSeeds(string path) =>
        File.ReadAllLines(path).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToList();

    public string Json() => ProtocolJson.Serialize(this);
}

/// <summary>
/// The leaderboards of docs: evaluation/metrics §9, over a folder of reports: per seed set and rung, one table per environment,
/// and then the SL − NoSL difference of each champion played in both, paired by seed. Partial reports are left out.
/// </summary>
public static class Leaderboard
{
    /// <param name="champions">The reports the champion files name (docs: evaluation/champions §1), marked on the boards.</param>
    public static string Markdown(IReadOnlyList<EvalReport> reports, IReadOnlyCollection<EvalReport> champions)
    {
        var accepted = new HashSet<EvalReport>(champions, ReferenceEqualityComparer.Instance);
        var md = new StringBuilder();
        void Line(FormattableString text) => md.AppendLine(text.ToString(CultureInfo.InvariantCulture));

        md.AppendLine("# Leaderboards").AppendLine();
        md.AppendLine("Generated by `leaderboard` from a character's reports in `eval/CHARACTER/reports/` (docs: evaluation/metrics §9). Partial");
        md.AppendLine("reports are left out; a void one is marked, and so is each environment's champion (`eval/CHARACTER/champions/`).");
        md.AppendLine("The two environments are never ranked together, nor two characters or ascensions.");
        foreach (var board in reports.Where(r => r.Runs.Count == r.SetSize)
                     .GroupBy(r => (r.Runs[0].Character, r.Runs[0].Ascension, r.SeedSet, r.Config.Rung)).OrderBy(g => g.Key))
        {
            md.AppendLine();
            Line($"## {board.Key.Character}, ascension {board.Key.Ascension}: {board.Key.SeedSet}, {board.Key.Rung}");
            foreach (var env in board.GroupBy(r => r.Config.Env).OrderBy(g => g.Key == "SL"))
            {
                md.AppendLine();
                Line($"### {env.Key}");
                md.AppendLine();
                md.AppendLine("| champion | date | commit | win rate | floor mean | act 1 boss | s per run |");
                md.AppendLine("|---|---|---|---|---|---|---|");
                foreach (var r in env.OrderBy(r => r.Metrics.Void).ThenByDescending(r => r.Metrics.Wins.Value).ThenByDescending(r => r.Metrics.FloorMean))
                {
                    var m = r.Metrics;
                    Line($"| {r.Config.Champion}{(m.Void ? " (void)" : "")}{(accepted.Contains(r) ? " **(champion)**" : "")} | {r.Date} | `{Short(r.Config.Commit)}` | {m.Wins} | {m.FloorMean:F2} | {m.Bosses.FirstOrDefault(b => b.Act == 0)?.Reached ?? 0} | {m.SecondsPerRun:F1} |");
                }
            }
            var latest = board.GroupBy(r => (r.Config.Champion, r.Config.Env)).ToDictionary(g => g.Key, g => g.MaxBy(r => r.Date)!);
            var both = latest.Keys.Where(k => k.Env == "SL" && latest.ContainsKey((k.Champion, "NoSL"))).Select(k => k.Champion).Order().ToList();
            if (both.Count == 0) continue;
            md.AppendLine();
            md.AppendLine("### SL − NoSL").AppendLine();
            md.AppendLine("The value of in-node information: each champion's latest report in each environment, on the seeds both played.");
            md.AppendLine();
            foreach (var champion in both)
            {
                var (sl, nosl) = (latest[(champion, "SL")], latest[(champion, "NoSL")]);
                var noslRuns = nosl.Runs.ToDictionary(r => r.Seed);
                var pairs = sl.Runs.Where(r => noslRuns.ContainsKey(r.Seed)).Select(r => (Sl: r, NoSl: noslRuns[r.Seed])).ToList();
                var wins = Paired(pairs.Select(p => (p.Sl.Outcome == "won" ? 1.0 : 0) - (p.NoSl.Outcome == "won" ? 1 : 0)).ToList());
                var floors = Paired(pairs.Select(p => (double)p.Sl.Floor - p.NoSl.Floor).ToList());
                Line($"- {champion} (SL `{Short(sl.Config.Commit)}`, NoSL `{Short(nosl.Config.Commit)}`):");
                Line($"  - win rate: {wins.Mean * 100:+0.0;-0.0;0.0} pp (95% {wins.Low * 100:+0.0;-0.0;0.0} to {wins.High * 100:+0.0;-0.0;0.0}), {pairs.Count(p => p.Sl.Outcome == "won")} against {pairs.Count(p => p.NoSl.Outcome == "won")} of {pairs.Count}");
                Line($"  - floor reached: {floors.Mean:+0.00;-0.00;0.00} (95% {floors.Low:+0.00;-0.00;0.00} to {floors.High:+0.00;-0.00;0.00}); SL went further on {pairs.Count(p => p.Sl.Floor > p.NoSl.Floor)} seeds, as far on {pairs.Count(p => p.Sl.Floor == p.NoSl.Floor)}, less far on {pairs.Count(p => p.Sl.Floor < p.NoSl.Floor)}");
            }
        }
        return md.ToString();
    }

    /// <summary>The mean of paired differences, with a normal 95% interval.</summary>
    private static (double Mean, double Low, double High) Paired(IReadOnlyList<double> diffs)
    {
        if (diffs.Count < 2) return (diffs.FirstOrDefault(), double.NegativeInfinity, double.PositiveInfinity);
        double mean = diffs.Average(), sd = Math.Sqrt(diffs.Sum(d => (d - mean) * (d - mean)) / (diffs.Count - 1));
        double half = 1.96 * sd / Math.Sqrt(diffs.Count);
        return (mean, mean - half, mean + half);
    }

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] + (commit.EndsWith("-dirty") ? "-dirty" : "") : commit;
}
