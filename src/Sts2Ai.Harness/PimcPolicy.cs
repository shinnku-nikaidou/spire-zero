using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="PooledPimc"/> as a combat policy, for whole runs: the run stays live in this process while the workers
/// search. Each decision draws its seeds from <paramref name="seed"/> and the decision's number, so a run replays
/// exactly.
/// </summary>
public sealed class PimcPolicy(PooledPimc pimc, Budget budget, ulong seed) : ICombatPolicy
{
    private ulong _decisions;

    public CombatAction Choose(CombatDecision decision)
    {
        ulong s = Mix(seed, _decisions++);
        if (decision.Legal.Count == 1) return decision.Legal[0];
        return pimc.Decide(decision.Observation, decision.Legal, s.ToString("X16"), budget, s).First;
    }

    /// <summary>SplitMix64 of <paramref name="a"/> stepped <paramref name="b"/> + 1 times.</summary>
    private static ulong Mix(ulong a, ulong b)
    {
        ulong z = a + 0x9E3779B97F4A7C15 * (b + 1);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
