using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// What a search leaf is worth (docs: combat-brain/leaf §1): a state after the turn ends, or after the combat does, with
/// the public record of the enemies' moves before it (<see cref="MoveRecord"/>). The combat net (<see cref="CombatNet"/>).
/// </summary>
public interface ICombatLeaf
{
    double Value(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history);

    /// <summary>
    /// The value with how the combat is expected to end, from one evaluation: what the SL planner keeps for each line, to
    /// show. A leaf that predicts no ending gives a combat still going no chance to win.
    /// </summary>
    LeafOutcome Outcome(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history) =>
        new(Value(status, obs, history), status == CombatStatus.Won ? 1 : 0, status == CombatStatus.Won ? obs.Player.Hp : 0);
}

/// <summary>A leaf's value of a state, the chance to win the combat from it, and the HP held at the end of a win.</summary>
public readonly record struct LeafOutcome(double Value, double Win, double Hp);

/// <summary>
/// What a finished combat is worth (docs: combat-brain/leaf §2): a loss nothing, a win by the HP share and the potions
/// left. The combat net values a state before the end on the same scale, by the ends it expects.
/// </summary>
public static class LeafValue
{
    /// <summary>A win at 0 HP left, then per share of max HP left.</summary>
    public const double WinBase = 0.2, PerHp = 0.6;

    /// <summary>Per potion still held: what a potion is worth in later combats.</summary>
    public const double PerPotion = 0.02;

    public static double Of(CombatStatus status, CombatObservation obs) => status switch
    {
        CombatStatus.Lost => 0,
        CombatStatus.Won => WinBase + PerHp * (obs.Player.Hp / (double)Math.Max(1, obs.Player.MaxHp)) + PerPotion * obs.Potions.Count(p => p != null),
        _ => throw new ArgumentException("a combat still going is the combat net's to value", nameof(status)),
    };
}
