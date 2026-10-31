using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The SL planner's leaf for a net of target worth (<c>--leaf-prices v</c>, docs: combat-brain/leaf §5), every state in V's
/// units: a death is 0, a win V of the run with the HP and the potions it ends with, a state in progress the net's share
/// of V before the fight.
/// </summary>
public sealed class PricedLeaf(CombatNet net, EndPrices prices) : ICombatLeaf
{
    /// <summary>
    /// V's prices of the ends of the combat <paramref name="run"/> is starting: for each subset of the potions held, V of
    /// the run at each HP, the most of it at that HP or less, so the planner's bound (a win with the HP and potions it
    /// has) holds as far as the potions are concerned too.
    /// </summary>
    public static EndPrices Of(IRunValue v, RunView run)
    {
        var held = run.Potions.OfType<string>().Order(StringComparer.Ordinal).ToList();
        var tables = new Dictionary<string, IReadOnlyList<double>>();
        for (int subset = 0; subset < 1 << held.Count; subset++)
        {
            var kept = held.Where((_, i) => (subset >> i & 1) != 0).ToList();
            string key = string.Join(',', kept);
            if (tables.ContainsKey(key)) continue;
            var slots = kept.Cast<string?>().Concat(Enumerable.Repeat<string?>(null, run.Potions.Count - kept.Count)).ToList();
            var hp = new double[run.MaxHp + 1];
            double most = double.NegativeInfinity;
            for (int h = 0; h <= run.MaxHp; h++) hp[h] = most = Math.Max(most, v.Value(run with { Hp = Math.Max(1, h), Potions = slots }));
            tables[key] = hp;
        }
        return new(v.Value(run), tables, held);
    }

    public double Value(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history) => status switch
    {
        CombatStatus.Lost => 0,
        CombatStatus.Won => Win(obs),
        _ => net.Worth(obs, history) * prices.Before,
    };

    /// <summary>A win's price: a potion the fight gave has no price, as V was asked only of those held at its start.</summary>
    private double Win(CombatObservation obs)
    {
        var left = prices.Held.ToList();
        var table = prices.Hp[string.Join(',', obs.Potions.OfType<string>().Where(left.Remove).Order(StringComparer.Ordinal))];
        return table[Math.Clamp(obs.Player.Hp, 1, table.Count - 1)];
    }
}
