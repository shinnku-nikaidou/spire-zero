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
/// The champion of one environment, every character's, as <c>eval/champions/ENV.json</c> holds it (docs: evaluation §5):
/// the run options that play it (<c>run --champion FILE</c>; a job gives its own character and ascension), its name as
/// the reports show it, by character the report that challengers of that character are paired against (a report is one
/// character at one ascension), and the gate it passed (none for the first champion).
/// </summary>
public sealed record ChampionFile(string Env, string Champion, IReadOnlyDictionary<string, string> Options,
    IReadOnlyDictionary<string, string> Reports, string Since, Gate.Result? Gate = null);
