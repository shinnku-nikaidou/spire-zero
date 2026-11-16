using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// V under a rule of the game: a potion can be thrown away at any time, for nothing. So a belt is never worth less
/// than the belt with any of its potions gone, and a run is worth the best of V over the belts it can leave itself
/// with. A potion V rates below an empty slot is then worth the empty slot, not less: drinking it for nothing, throwing
/// it away, or leaving it on a screen never wins by V's dislike of it alone, and a potion V likes keeps its value.
///
/// V rated many potions below an empty slot, a bias it inherited from self-play that threw away 69-79 % of its potions
/// (v9g0, v9g1). Priced by it, the SL planner drank such potions in a hallway fight for nothing (a Duplicator, then the
/// turn ended) and l0 threw them away where it could. The user saw both in the real game (2026-11-16).
///
/// l0 values its options by this (<see cref="MacroBrain"/>); the combat net prices its endings the same way
/// (<see cref="Combat.CombatNet"/>, with <see cref="Spread"/> over the subsets it prices anyway).
/// </summary>
public sealed class FreeDisposal(IRunValue value) : IRunValue
{
    public string Name => value.Name;

    public double Value(RunView run) => Best(run.Potions, potions => value.Value(run with { Potions = potions }));

    /// <summary>The best of <paramref name="value"/> over the belts <paramref name="potions"/> can be left as.</summary>
    public static double Best(IReadOnlyList<string?> potions, Func<IReadOnlyList<string?>, double> value)
    {
        var belts = Kept(potions);
        var values = belts.Select(value).ToArray();
        Spread(values);
        return values[^1];
    }

    /// <summary>
    /// Each belt <paramref name="potions"/> can be left as, by the mask of the potions held that it keeps: bit i keeps
    /// the i-th potion held, in slot order. The last keeps them all.
    /// </summary>
    public static IReadOnlyList<string?>[] Kept(IReadOnlyList<string?> potions)
    {
        var held = Enumerable.Range(0, potions.Count).Where(s => potions[s] != null).ToList();
        return [.. Enumerable.Range(0, 1 << held.Count).Select(mask =>
        {
            var belt = potions.ToArray();
            for (int i = 0; i < held.Count; i++)
                if ((mask >> i & 1) == 0) belt[held[i]] = null;
            return (IReadOnlyList<string?>)belt;
        })];
    }

    /// <summary>Values by the mask of potions kept (<see cref="Kept"/>), each made the best of its own and its subsets':
    /// what each belt is worth when any of its potions can be thrown away.</summary>
    public static void Spread(double[] byMask)
    {
        for (int bit = 1; bit < byMask.Length; bit <<= 1)
            for (int mask = 0; mask < byMask.Length; mask++)
                if ((mask & bit) != 0) byMask[mask] = Math.Max(byMask[mask], byMask[mask ^ bit]);
    }
}
