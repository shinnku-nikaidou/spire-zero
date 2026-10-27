using System.Globalization;
using System.Text;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>A combat the run entered: where, which, and the HP going in.</summary>
/// <param name="Room">The room type: Monster, Elite or Boss.</param>
public sealed record CombatEntry(int Act, int Floor, string Encounter, string Room, int Hp, int MaxHp);

/// <summary>What played a run, the same for every run of one evaluation (docs: evaluation §6).</summary>
/// <param name="Champion">The brain on each side of the routing table, with its budget.</param>
/// <param name="Rung">R1 to R4 (docs: evaluation §1).</param>
public sealed record RunConfig(string Game, string Commit, string Champion, string Env, string Rung);

/// <summary>One run of an evaluation, as the report needs it (docs: evaluation §3).</summary>
/// <param name="Outcome">won, lost, truncated (the decision cap), or failed (an error in the driver or a brain; never re-run).</param>
/// <param name="Brains">Decisions per brain name, from Harness's log.</param>
/// <param name="CardRewards">Card reward screens, and how many of them ended with no card taken.</param>
public sealed record RunRecord(
    RunConfig Config, string Seed, string Character, int Ascension, string Outcome, int Act, int Floor, int Hp, int MaxHp, int Gold,
    IReadOnlyList<CombatEntry> Combats, IReadOnlyDictionary<string, int> Surfaces, IReadOnlyDictionary<string, int> Brains,
    int CardRewards, int CardRewardsSkipped, int GameErrors, string? Failure, double ElapsedMs);

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
/// The metrics of docs: evaluation §3. Failed and truncated runs count as not won; they are also listed with the
/// must-be-zero metrics, and any of those above zero makes the evaluation void.
/// </summary>
public sealed record Metrics(
    Rate Wins, double FloorMean, double FloorMedian, IReadOnlyList<BossEntry> Bosses,
    int RandomDecisions, int Failed, int Truncated, int GameErrors,
    IReadOnlyDictionary<string, int> Surfaces, double ElitesPerRun, Rate CardRewardSkips, double GoldMean,
    double MsPerDecision, double SecondsPerRun)
{
    /// <summary>The brain name every placeholder goes by (docs: harness §4: a random decision voids the evaluation).</summary>
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
/// One evaluation (docs: evaluation §6): what was played, on which seeds, the metrics, and every run's record.
/// <see cref="Markdown"/> is the one-page report; the JSON is this record.
/// </summary>
/// <param name="SeedSet">The seed file's name; <paramref name="SetSize"/> seeds are in it, and every one must be run.</param>
public sealed record EvalReport(RunConfig Config, string SeedSet, int SetSize, string Date, Metrics Metrics, IReadOnlyList<RunRecord> Runs)
{
    /// <summary>
    /// The report over <paramref name="runs"/>, which may come from several processes. They must share one config,
    /// and each run a different seed of the set.
    /// </summary>
    /// <exception cref="ArgumentException">The runs disagree on the config, repeat a seed, or play one not in the set.</exception>
    public static EvalReport Of(IReadOnlyList<RunRecord> runs, string seedSet, IReadOnlyList<string> seeds, string date)
    {
        if (runs.Count == 0) throw new ArgumentException("no runs");
        var config = runs[0].Config;
        if (runs.FirstOrDefault(r => r.Config != config) is { } other) throw new ArgumentException($"run {other.Seed} was played by {other.Config}, not {config}");
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
        if (m.Void) Line($"**Void** (docs: evaluation §3): {VoidReasons()}.");
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
