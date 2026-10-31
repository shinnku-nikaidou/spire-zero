using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>A combat the run entered: where, which, the HP and potions going in, and how it ended.</summary>
/// <param name="Room">The room type: Monster, Elite or Boss.</param>
/// <param name="Potions">The potions held at its first decision.</param>
/// <param name="End">Null while it lasts, and for good if the run stopped in it for another reason than a death
/// (failed or truncated).</param>
public sealed record CombatEntry(int Act, int Floor, string Encounter, string Room, int Hp, int MaxHp, int Potions = 0, CombatEnd? End = null);

/// <summary>How a combat ended, the target of the combat outcome head (docs: learning/combat-net §3): a death, or the HP, max HP and
/// potions left after it; and the HP its enemies had left, against their max HP (how far a lost fight got; a won
/// fight's enemies are gone, 0 of 0).</summary>
public sealed record CombatEnd(bool Died, int Hp, int MaxHp, int Potions, int? EnemyHp = null, int? EnemyMaxHp = null);

/// <summary>What played a run, the same for every run of one evaluation (docs: evaluation/metrics §8).</summary>
/// <param name="Champion">The brain on each side of the routing table, with its budget.</param>
/// <param name="Rung">R1 to R4 (docs: evaluation §2).</param>
/// <param name="AutoWin">True for a boss gauntlet pilot's run (<c>run --auto-win</c>): every combat won at its start, so
/// the run says nothing of how it would have fared; null otherwise.</param>
/// <param name="Gauntlet">True for a boss gauntlet's run (<c>run --gauntlet</c>): plain monster encounters won at their
/// start, elites and bosses fought, so the run is no whole run (no report or gate takes it); null otherwise.</param>
public sealed record RunConfig(string Game, string Commit, string Champion, string Env, string Rung, bool? AutoWin = null, bool? Gauntlet = null);

/// <summary>One run of an evaluation, as the report needs it (docs: evaluation/metrics §8).</summary>
/// <param name="RunId">The run id the brain knew the run by, as in the decision log (docs: harness/decision-log).</param>
/// <param name="Outcome">won, lost, truncated (the decision cap), or failed (an error in the driver or a brain; never re-run).</param>
/// <param name="Brains">Decisions per brain name, from Harness's log.</param>
/// <param name="CardRewards">Card rewards offered, and how many of them were left untaken (skipped, or never opened).</param>
public sealed record RunRecord(
    RunConfig Config, string Seed, string Character, int Ascension, string Outcome, int Act, int Floor, int Hp, int MaxHp, int Gold,
    IReadOnlyList<CombatEntry> Combats, IReadOnlyDictionary<string, int> Surfaces, IReadOnlyDictionary<string, int> Brains,
    int CardRewards, int CardRewardsSkipped, int GameErrors, string? Failure, double ElapsedMs, string? RunId = null)
{
    /// <summary>The run records in these files (comma-separated), one per line. A failed run is as if never set: reports and
    /// gates see only the others (a host's memory fails runs at random: fleet references/maintenance.md).</summary>
    public static List<RunRecord> Read(string files) => Records(files).Where(r => r.Outcome != "failed").ToList();

    /// <summary>The seeds of a set less those these files' runs failed on: 2 failed of 500 is a set of 498.</summary>
    public static List<string> SeedsLeft(string files, IEnumerable<string> seeds)
    {
        var failed = Records(files).Where(r => r.Outcome == "failed").Select(r => r.Seed).ToHashSet();
        return seeds.Where(s => !failed.Contains(s)).ToList();
    }

    private static IEnumerable<RunRecord> Records(string files) =>
        files.Split(',').SelectMany(File.ReadAllLines).Where(l => l.Length > 0).Select(ProtocolJson.Deserialize<RunRecord>);
}
