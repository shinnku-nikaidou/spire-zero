using System.Globalization;

namespace Sts2Ai.Harness;

/// <summary>
/// The full-run gate (docs: evaluation/gates §6): a challenger against the champion on the same seeds (CRN), pair by pair in
/// the seed set's order, with an SPRT that stops at the first pair where the evidence is clear. The champion's side is
/// its report: runs are deterministic, so the same seed played again gives the same run.
///
/// It tests wins: only the pairs where exactly one side won count, and the challenger's share of them (H0: 0.5, H1: 0.5 +
/// delta). The mean floor difference, challenger minus champion, is listed beside it, not tested.
///
/// A not-worse gate, for a change meant to be cheaper rather than stronger, shifts both hypotheses down by delta: H0,
/// the challenger is worse by delta; H1, it is not worse (an even share of the wins).
/// </summary>
public static class Gate
{
    /// <param name="Verdict">pass (H1: the challenger is better by delta, or not worse in a not-worse gate), fail (H0: it is
    /// not better, or worse by delta), open (the pairs ran out first), or void (the challenger's runs break a must-be-zero
    /// rule, docs: evaluation/metrics §2).</param>
    /// <param name="Decided">The pair at which the SPRT stopped, or every pair when it did not.</param>
    /// <param name="Llr">The log-likelihood ratio at <paramref name="Decided"/>; it passes at <paramref name="Upper"/> and
    /// fails at <paramref name="Lower"/>.</param>
    public sealed record Result(
        double Delta, double Alpha, double Beta, int Pairs, int Decided, double Llr, double Lower, double Upper,
        double FloorDiffMean, int ChallengerOnlyWins, int ChampionOnlyWins, string Verdict, string? VoidReason, bool NotWorse = false)
    {
        public string Summary() => string.Create(CultureInfo.InvariantCulture,
            $"gate {Verdict}{(VoidReason != null ? $" ({VoidReason})" : "")}: wins{(NotWorse ? ", not worse" : "")}, delta {Delta}, after {Decided} of {Pairs} pairs; " +
            $"LLR {Llr:F2} in [{Lower:F2}, {Upper:F2}]; floor difference {FloorDiffMean:+0.00;-0.00}; " +
            $"wins only by the challenger {ChallengerOnlyWins}, only by the champion {ChampionOnlyWins}");
    }

    /// <summary>The pairs: every seed of <paramref name="champion"/> that the challenger played too, in the champion's order.</summary>
    /// <exception cref="ArgumentException">The two sides played in different environments or games, or as
    /// different characters or at different ascensions, or the challenger by the auto-win rule.</exception>
    public static IReadOnlyList<(RunRecord Champion, RunRecord Challenger)> Pairs(EvalReport champion, IReadOnlyList<RunRecord> challenger)
    {
        var c = champion.Config;
        if (challenger.FirstOrDefault(r => r.Config.AutoWin != null) is { } ruled) throw new ArgumentException(EvalReport.RuleRefused(ruled));
        if (challenger.FirstOrDefault(r => (r.Config.Game, r.Config.Env) != (c.Game, c.Env)) is { } other)
            throw new ArgumentException($"run {other.Seed} was played in {other.Config.Game} {other.Config.Env}, the champion in {c.Game} {c.Env}");
        // A report holds one character at one ascension (EvalReport.Of).
        var (character, ascension) = (champion.Runs[0].Character, champion.Runs[0].Ascension);
        if (challenger.FirstOrDefault(r => (r.Character, r.Ascension) != (character, ascension)) is { } stranger)
            throw new ArgumentException($"run {stranger.Seed} was played as {stranger.Character} at ascension {stranger.Ascension}, the champion as {character} at ascension {ascension}");
        if (challenger.Select(r => r.Config.Champion).Distinct().Count() > 1) throw new ArgumentException("the challenger's runs come from more than one champion");
        var bySeed = challenger.GroupBy(r => r.Seed).ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First() : throw new ArgumentException($"seed {g.Key} was run {g.Count()} times"));
        return champion.Runs.Where(r => bySeed.ContainsKey(r.Seed)).Select(r => (r, bySeed[r.Seed])).ToList();
    }

    public static Result Sprt(IReadOnlyList<(RunRecord Champion, RunRecord Challenger)> pairs, double delta, double alpha = 0.05,
        double beta = 0.05, bool notWorse = false)
    {
        double upper = Math.Log((1 - beta) / alpha), lower = Math.Log(beta / (1 - alpha));
        var challenger = Metrics.Of([.. pairs.Select(p => p.Challenger)]);
        string? voidReason = challenger.Void
            ? $"random decisions {challenger.RandomDecisions}, failed {challenger.Failed}, truncated {challenger.Truncated}, game errors {challenger.GameErrors}"
            : null;

        // A not-worse gate tests the same as a gain of delta would, from a share lower by delta.
        double p0 = notWorse ? 0.5 - delta : 0.5, p1 = p0 + delta, sum = 0, llr = 0;
        int only = 0, championOnly = 0, decided = pairs.Count;
        string verdict = "open";
        for (int n = 1; n <= pairs.Count; n++)
        {
            var (a, b) = pairs[n - 1];
            sum += b.Floor - a.Floor;
            bool aWon = a.Outcome == "won", bWon = b.Outcome == "won";
            if (bWon && !aWon) only++;
            if (aWon && !bWon) championOnly++;
            llr = only * Math.Log(p1 / p0) + championOnly * Math.Log((1 - p1) / (1 - p0));
            if (llr >= upper || llr <= lower)
            {
                verdict = llr >= upper ? "pass" : "fail";
                decided = n;
                break;
            }
        }
        // The loop stopped at the decided pair, so the counts cover exactly the pairs the verdict rests on.
        return new(delta, alpha, beta, pairs.Count, decided, llr, lower, upper, decided == 0 ? 0 : sum / decided, only, championOnly,
            voidReason != null ? "void" : verdict, voidReason, notWorse);
    }
}

/// <summary>
/// The gate of a pair of nets (docs: evaluation/gates §6): the champion's nets and the new ones both play the same seed
/// set as each character, at the same commit, and the runs are paired by seed. The nets are every character's, so the
/// verdict is over all the characters' pairs together: promote when the 95 % one-sided lower bound of the paired
/// difference in wins (1 or 0, the challenger less the champion) is above 0. Each character's own difference, and the
/// floors', are listed beside it, not tested.
/// </summary>
public static class NetGate
{
    /// <summary>Paired differences, the challenger less the champion: their mean, its standard error, and for the wins the
    /// 95 % one-sided lower bound (the mean less 1.645 standard errors).</summary>
    /// <param name="Character">The character, or "all" for every character's pairs together.</param>
    public sealed record Paired(string Character, int Pairs, int Won, int ChampionWon, double WinsDiff, double WinsDiffSe, double WinsLower95,
        double FloorDiff, double FloorDiffSe);

    /// <param name="Against">The champion the challenger was paired with, as its runs name it.</param>
    /// <param name="All">Every character's pairs together, which decide.</param>
    public sealed record Result(string Against, string SeedSet, IReadOnlyList<Paired> Characters, Paired All, bool Promote)
    {
        /// <summary>A line per character, then all of them together and the verdict.</summary>
        public string Summary()
        {
            static string Line(Paired p) => string.Create(CultureInfo.InvariantCulture,
                $"{p.Character,-12} {p.Pairs,5} pairs, won {p.Won,4} against {p.ChampionWon,4}: {p.WinsDiff * 100:+0.0;-0.0;0.0} ± {p.WinsDiffSe * 100:0.0} points, " +
                $"lower bound {p.WinsLower95 * 100:+0.0;-0.0;0.0}; floors {p.FloorDiff:+0.00;-0.00;0.00} ± {p.FloorDiffSe:0.00}");
            return string.Join("\n", [.. Characters.Select(Line), Line(All),
                $"net gate {(Promote ? "pass" : "fail")}: the lower bound of the wins over all pairs is {(Promote ? "above" : "not above")} 0"]);
        }
    }

    /// <summary>
    /// Pairs each character's two reports (<see cref="Gate.Pairs"/>, on the seeds both sides finished) and judges them
    /// together.
    /// </summary>
    /// <exception cref="ArgumentException">A pair of reports that does not match (another environment, game, character,
    /// ascension or seed set), a character twice, sides played by more than one champion each, or no pair at all.</exception>
    public static Result Judge(IReadOnlyList<(EvalReport Champion, EvalReport Challenger)> sides)
    {
        if (sides.Count == 0) throw new ArgumentException("no reports to pair");
        if (sides.Select(s => s.Champion.Config.Champion).Distinct().Count() > 1) throw new ArgumentException("the champion's reports come from more than one champion");
        if (sides.Select(s => s.Challenger.Config.Champion).Distinct().Count() > 1) throw new ArgumentException("the challenger's reports come from more than one champion");
        if (sides.Select(s => s.Champion.SeedSet).Concat(sides.Select(s => s.Challenger.SeedSet)).Distinct().Count() > 1)
            throw new ArgumentException("the reports are of more than one seed set");
        var all = new List<(RunRecord Champion, RunRecord Challenger)>();
        var characters = new List<Paired>();
        foreach (var (champion, challenger) in sides)
        {
            string character = champion.Runs[0].Character;
            if (characters.Any(c => c.Character == character)) throw new ArgumentException($"{character} has two pairs of reports");
            var pairs = Gate.Pairs(champion, challenger.Runs);
            if (pairs.Count == 0) throw new ArgumentException($"{character}'s reports share no seed");
            characters.Add(Of(character, pairs));
            all.AddRange(pairs);
        }
        var together = Of("all", all);
        return new(sides[0].Champion.Config.Champion, sides[0].Champion.SeedSet, characters, together, together.WinsLower95 > 0);
    }

    private static Paired Of(string character, IReadOnlyList<(RunRecord Champion, RunRecord Challenger)> pairs)
    {
        static double Won(RunRecord r) => r.Outcome == "won" ? 1 : 0;
        var (wins, winsSe) = MeanSe([.. pairs.Select(p => Won(p.Challenger) - Won(p.Champion))]);
        var (floors, floorsSe) = MeanSe([.. pairs.Select(p => (double)(p.Challenger.Floor - p.Champion.Floor))]);
        return new(character, pairs.Count, pairs.Count(p => p.Challenger.Outcome == "won"), pairs.Count(p => p.Champion.Outcome == "won"),
            wins, winsSe, wins - 1.645 * winsSe, floors, floorsSe);
    }

    /// <summary>The mean and its standard error; one value has no standard error, and so never passes.</summary>
    private static (double Mean, double Se) MeanSe(IReadOnlyList<double> xs)
    {
        double mean = xs.Average();
        return (mean, xs.Count > 1 ? Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1) / xs.Count) : double.PositiveInfinity);
    }
}

/// <summary>
/// The champion of one environment, every character's, as <c>eval/champions/ENV.json</c> holds it (docs: evaluation §5):
/// the run options that play it (<c>run --champion FILE</c>; a job gives its own character and ascension), its name as
/// the reports show it, by character its report (a report is one character at one ascension), and the gate its nets
/// passed (<c>promote</c>; none for a champion that took its nets from another environment's promotion).
/// </summary>
public sealed record ChampionFile(string Env, string Champion, IReadOnlyDictionary<string, string> Options,
    IReadOnlyDictionary<string, string> Reports, string Since, NetGate.Result? Gate = null)
{
    /// <summary>
    /// The file after a promotion: the challenger's name, options and reports, and the gate it passed. For another
    /// environment's file that shares the nets, <paramref name="reports"/> and <paramref name="gate"/> are left out: its
    /// old reports were played by other nets, and it has none of its own yet.
    /// </summary>
    public ChampionFile Promoted(string champion, IReadOnlyDictionary<string, string> options, string since,
        IReadOnlyDictionary<string, string>? reports = null, NetGate.Result? gate = null) =>
        new(Env, champion, options, reports ?? new Dictionary<string, string>(), since, gate);
}
