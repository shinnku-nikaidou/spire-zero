using System.Globalization;

namespace Sts2Ai.Harness;

/// <summary>
/// The full-run gate (docs: evaluation/gates §6): a challenger against the champion on the same seeds (CRN), pair by pair in
/// the seed set's order, with an SPRT that stops at the first pair where the evidence is clear. The champion's side is
/// its report: runs are deterministic, so the same seed played again gives the same run.
///
/// Two metrics. <c>floor</c> tests the mean floor difference, challenger minus champion (H0: 0, H1: delta floors), as
/// a Gaussian SPRT whose variance is estimated from the pairs so far; it is for while runs rarely win. <c>win</c>
/// looks only at the pairs where exactly one side won, and tests the challenger's share of them (H0: 0.5, H1: 0.5 +
/// delta).
///
/// A not-worse gate, for a change meant to be cheaper rather than stronger, shifts both hypotheses down by delta: H0,
/// the challenger is worse by delta; H1, it is not worse (a floor difference of 0, or an even share of the wins).
/// </summary>
public static class Gate
{
    /// <summary>
    /// A floor SPRT decides nothing before this many pairs differ: the variance estimate needs a few. Tied pairs do not
    /// count, since a change that alters only some runs (a boss's budget) ties most pairs, and ties alone would put
    /// the variance at its floor and fail the change at once.
    /// </summary>
    public const int MinPairs = 10;

    /// <param name="Verdict">pass (H1: the challenger is better by delta, or not worse in a not-worse gate), fail (H0: it is
    /// not better, or worse by delta), open (the pairs ran out first), or void (the challenger's runs break a must-be-zero
    /// rule, docs: evaluation/metrics §2).</param>
    /// <param name="Decided">The pair at which the SPRT stopped, or every pair when it did not.</param>
    /// <param name="Llr">The log-likelihood ratio at <paramref name="Decided"/>; it passes at <paramref name="Upper"/> and
    /// fails at <paramref name="Lower"/>.</param>
    public sealed record Result(
        string Metric, double Delta, double Alpha, double Beta, int Pairs, int Decided, double Llr, double Lower, double Upper,
        double FloorDiffMean, int ChallengerOnlyWins, int ChampionOnlyWins, string Verdict, string? VoidReason, bool NotWorse = false)
    {
        public string Summary() => string.Create(CultureInfo.InvariantCulture,
            $"gate {Verdict}{(VoidReason != null ? $" ({VoidReason})" : "")}: {Metric}{(NotWorse ? ", not worse" : "")}, delta {Delta}, after {Decided} of {Pairs} pairs; " +
            $"LLR {Llr:F2} in [{Lower:F2}, {Upper:F2}]; floor difference {FloorDiffMean:+0.00;-0.00}; " +
            $"wins only by the challenger {ChallengerOnlyWins}, only by the champion {ChampionOnlyWins}");
    }

    /// <summary>The pairs: every seed of <paramref name="champion"/> that the challenger played too, in the champion's order.</summary>
    /// <exception cref="ArgumentException">The two sides played in different environments or games, or as
    /// different characters or at different ascensions.</exception>
    public static IReadOnlyList<(RunRecord Champion, RunRecord Challenger)> Pairs(EvalReport champion, IReadOnlyList<RunRecord> challenger)
    {
        var c = champion.Config;
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

    public static Result Sprt(IReadOnlyList<(RunRecord Champion, RunRecord Challenger)> pairs, string metric, double delta, double alpha = 0.05,
        double beta = 0.05, bool notWorse = false)
    {
        if (metric is not ("floor" or "win")) throw new ArgumentException($"unknown gate metric {metric} (floor, win)");
        double upper = Math.Log((1 - beta) / alpha), lower = Math.Log(beta / (1 - alpha));
        var challenger = Metrics.Of(pairs.Select(p => p.Challenger).ToList());
        string? voidReason = challenger.Void
            ? $"random decisions {challenger.RandomDecisions}, failed {challenger.Failed}, truncated {challenger.Truncated}, game errors {challenger.GameErrors}"
            : null;

        // A not-worse gate tests the same as a gain of delta would, on differences shifted up by delta.
        double shift = notWorse ? delta : 0, sum = 0, sumSq = 0, llr = 0;
        int only = 0, championOnly = 0, differing = 0, decided = pairs.Count;
        string verdict = "open";
        for (int n = 1; n <= pairs.Count; n++)
        {
            var (a, b) = pairs[n - 1];
            double d = b.Floor - a.Floor;
            sum += d;
            sumSq += d * d;
            if (d != 0) differing++;
            bool aWon = a.Outcome == "won", bWon = b.Outcome == "won";
            if (bWon && !aWon) only++;
            if (aWon && !bWon) championOnly++;
            if (metric == "floor")
            {
                if (differing < MinPairs) continue;
                double variance = Math.Max(1, (sumSq - sum * sum / n) / (n - 1));
                llr = delta / variance * (sum + n * shift - n * delta / 2);
            }
            else
            {
                double p0 = 0.5 - shift, p1 = p0 + delta;
                llr = only * Math.Log(p1 / p0) + championOnly * Math.Log((1 - p1) / (1 - p0));
            }
            if (llr >= upper || llr <= lower)
            {
                verdict = llr >= upper ? "pass" : "fail";
                decided = n;
                break;
            }
        }
        // The loop stopped at the decided pair, so the sums cover exactly the pairs the verdict rests on.
        return new(metric, delta, alpha, beta, pairs.Count, decided, llr, lower, upper, decided == 0 ? 0 : sum / decided, only, championOnly,
            voidReason != null ? "void" : verdict, voidReason, notWorse);
    }
}

/// <summary>
/// The champion of one character in one environment, as <c>eval/CHARACTER/champions/ENV.json</c> holds it (docs:
/// evaluation §5): the run options that play it (<c>run --champion FILE</c>), its character and ascension included, its
/// name as the reports show it, the report that challengers are paired against, and the gate it passed (none for the
/// first champion).
/// </summary>
public sealed record ChampionFile(string Env, string Champion, IReadOnlyDictionary<string, string> Options, string Report, string Since,
    Gate.Result? Gate = null);
