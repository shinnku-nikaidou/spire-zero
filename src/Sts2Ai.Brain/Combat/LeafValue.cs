using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The tunable numbers of <see cref="LeafValue"/>. Hand-set for v0; SPSA tunes them (docs: combat-brain/leaf §2), and a
/// <see cref="LeafFile"/> holds the result.
/// </summary>
public sealed record LeafWeights
{
    /// <summary>Value of winning the combat at 0 HP left, then per fraction of max HP left.</summary>
    public double WinBase { get; init; } = 0.2;
    public double PerHp { get; init; } = 0.6;
    /// <summary>Per potion still held: what a potion is worth in later combats.</summary>
    public double PerPotion { get; init; } = 0.02;
    /// <summary>Damage the player deals per turn, to turn the enemies' HP into turns left.</summary>
    public double Dps { get; init; } = 10;
    /// <summary>Share of the projected incoming damage that gets through block.</summary>
    public double Unblocked { get; init; } = 0.5;
    /// <summary>Damage per turn assumed for an enemy that shows no attack this turn.</summary>
    public double MinIncoming { get; init; } = 3;
    /// <summary>Share of the poison's remaining damage (n + (n-1) + ... + 1) counted against the enemy's HP.</summary>
    public double PoisonShare { get; init; } = 0.8;
}

/// <summary>
/// What a search leaf is worth (docs: combat-brain/leaf §1): a state after the turn ends, or after the combat does, with
/// the public record of the enemies' moves before it (<see cref="MoveRecord"/>).
/// </summary>
public interface ICombatLeaf
{
    double Value(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history);
}

/// <summary>The hand-made leaf, <see cref="LeafValue"/>, which reads the observation alone.</summary>
public sealed class HandLeaf(LeafWeights? weights = null) : ICombatLeaf
{
    public double Value(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history) => LeafValue.Of(status, obs, weights);
}

/// <summary>Leaf weights with their name, as <c>--leaf FILE</c> reads them; the name goes into the champion's.</summary>
public sealed record LeafFile(string Name, LeafWeights Weights)
{
    public static LeafFile Initial { get; } = new("leaf0", LeafValue.Default);
}

/// <summary>
/// v0 leaf value (docs: combat-brain/leaf §2): a hand-made estimate of P(win run) from the observation alone, so it cannot
/// see hidden state in either environment. A won combat scores by the HP and potions left. Mid-combat, the rest of
/// the fight is projected: the player kills the enemies one after another at a fixed damage rate, the most dangerous
/// per HP first, and each enemy keeps hitting at its shown rate until it dies. The projected HP is scored like a win.
/// So damage is worth more on an enemy that hits hard and dies soon, overkill is worth nothing, and a mid-combat
/// state never beats the win it could reach at the same HP.
/// </summary>
public static class LeafValue
{
    public static double Of(CombatStatus status, CombatObservation obs, LeafWeights? weights = null)
    {
        var w = weights ?? Default;
        if (status == CombatStatus.Lost) return 0;
        int maxHp = Math.Max(1, obs.Player.MaxHp);
        double potions = w.PerPotion * obs.Potions.Count(p => p != null);
        if (status == CombatStatus.Won) return Win(w, obs.Player.Hp / (double)maxHp) + potions;

        var enemies = new List<(double Hp, double Hits)>();
        foreach (var enemy in obs.Enemies)
        {
            if (enemy.Hp <= 0) continue;
            int poison = Amount(enemy.Powers, "POISON_POWER");
            double poisonDamage = Math.Min(enemy.Hp, w.PoisonShare * poison * (poison + 1) / 2.0);
            double hits = 0;
            foreach (var intent in enemy.Intents ?? []) hits += (intent.Damage ?? 0) * (intent.Hits ?? 1);
            enemies.Add((Math.Max(0, enemy.Hp + enemy.Block - poisonDamage), Math.Max(w.MinIncoming, hits)));
        }
        double dps = Math.Max(1, w.Dps + 2 * Amount(obs.Player.Powers, "STRENGTH_POWER"));
        // Smith's rule: this order minimizes the total of (damage rate x time until dead).
        double turns = 0, taken = 0;
        foreach (var (hp, hits) in enemies.OrderByDescending(e => e.Hits / Math.Max(1, e.Hp)))
        {
            turns += hp / dps;
            taken += hits * turns;
        }
        double hpLeft = obs.Player.Hp - w.Unblocked * Math.Max(0, taken - obs.Player.Block);
        return Win(w, hpLeft / maxHp) + potions;
    }

    public static readonly LeafWeights Default = new();

    /// <summary>
    /// Linear in the HP share left; below 0 (a projected death) it decays toward 0 instead, with the same slope at 0,
    /// so it stays above a loss and keeps ordering states however far below: a flat value would make every line look
    /// the same, and the search would stop trying.
    /// </summary>
    private static double Win(LeafWeights w, double hpShare) =>
        hpShare >= 0 ? w.WinBase + w.PerHp * hpShare : w.WinBase * Math.Exp(w.PerHp * hpShare / w.WinBase);

    private static int Amount(IReadOnlyList<PowerView> powers, string id)
    {
        foreach (var p in powers) if (p.Id == id) return p.Amount;
        return 0;
    }
}
