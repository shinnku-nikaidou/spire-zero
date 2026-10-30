using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The SL planner's leaf when V prices the combat's ends (<c>--leaf-prices v</c>, docs: combat-brain/leaf §5): the combat
/// net's endings, each at V's price, instead of the leaf weights' hand-set ones.
/// </summary>
public sealed class PricedLeaf(CombatNet net, EndPrices prices) : ICombatLeaf
{
    /// <summary>
    /// V's prices of the ends of the combat <paramref name="run"/> is in: a death ends the run on its floor; a win is V
    /// of the run at each HP, the most of it at that HP or less, so the planner's bound (a win with the HP it has)
    /// holds; a potion is what V gives up with one of those held, on average, and never less than nothing.
    /// </summary>
    public static EndPrices Of(IRunValue v, RunView run)
    {
        var hp = new double[run.MaxHp + 1];
        double most = double.NegativeInfinity;
        for (int h = 0; h <= run.MaxHp; h++) hp[h] = most = Math.Max(most, v.Value(run with { Hp = Math.Max(1, h) }));
        var held = Enumerable.Range(0, run.Potions.Count).Where(i => run.Potions[i] != null).ToList();
        double now = v.Value(run);
        double potion = held.Count == 0 ? 0
            : Math.Max(0, held.Average(i => now - v.Value(run with { Potions = run.Potions.Select((p, k) => k == i ? null : p).ToList() })));
        return new(run.Floor, hp, potion, held.Count);
    }

    public double Value(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history) => net.Value(status, obs, history, prices);
}
