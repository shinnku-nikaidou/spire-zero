using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>A combat the run entered: where, which, the HP and potions going in, and how it ended.</summary>
/// <param name="Room">The room type: Monster, Elite or Boss.</param>
/// <param name="Potions">The potions held at its first decision.</param>
/// <param name="End">Null while it lasts, and for good if the run stopped in it for another reason than a death
/// (failed or truncated).</param>
public sealed record CombatEntry(int Act, int Floor, string Encounter, string Room, int Hp, int MaxHp, int Potions = 0, CombatEnd? End = null);

/// <summary>How a combat ended, the target of the combat outcome head (docs: learning §1): a death, or the HP, max HP and
/// potions left after it.</summary>
public sealed record CombatEnd(bool Died, int Hp, int MaxHp, int Potions);

/// <summary>What played a run, the same for every run of one evaluation (docs: evaluation §6).</summary>
/// <param name="Champion">The brain on each side of the routing table, with its budget.</param>
/// <param name="Rung">R1 to R4 (docs: evaluation §1).</param>
/// <param name="AutoWin">True for a boss gauntlet's run (<c>run --auto-win</c>): every combat won at its start, so the
/// run says nothing of how it would have fared; null otherwise.</param>
public sealed record RunConfig(string Game, string Commit, string Champion, string Env, string Rung, bool? AutoWin = null);

/// <summary>One run of an evaluation, as the report needs it (docs: evaluation §3).</summary>
/// <param name="RunId">The run id the brain knew the run by, as in the decision log (docs: harness §6).</param>
/// <param name="Outcome">won, lost, truncated (the decision cap), or failed (an error in the driver or a brain; never re-run).</param>
/// <param name="Brains">Decisions per brain name, from Harness's log.</param>
/// <param name="CardRewards">Card rewards offered, and how many of them were left untaken (skipped, or never opened).</param>
public sealed record RunRecord(
    RunConfig Config, string Seed, string Character, int Ascension, string Outcome, int Act, int Floor, int Hp, int MaxHp, int Gold,
    IReadOnlyList<CombatEntry> Combats, IReadOnlyDictionary<string, int> Surfaces, IReadOnlyDictionary<string, int> Brains,
    int CardRewards, int CardRewardsSkipped, int GameErrors, string? Failure, double ElapsedMs, string? RunId = null)
{
    /// <summary>The run records in these files (comma-separated), one per line.</summary>
    public static List<RunRecord> Read(string files) =>
        files.Split(',').SelectMany(File.ReadAllLines).Where(l => l.Length > 0).Select(ProtocolJson.Deserialize<RunRecord>).ToList();
}
