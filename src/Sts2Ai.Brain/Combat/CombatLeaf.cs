using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// What a search leaf is worth (docs: combat-brain/leaf §1): a state after the turn ends, or after the combat does, with
/// the run around it as it stands there (<see cref="ICombatWorld.Run"/>) and the public record of the enemies' moves
/// before it (<see cref="MoveRecord"/>). The combat net (<see cref="CombatNet"/>), its endings priced by V.
/// </summary>
public interface ICombatLeaf
{
    double Value(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history) =>
        Outcome(status, obs, run, history).Value;

    /// <summary>The value with how the combat is expected to end, from one evaluation: what the SL planner keeps for each line, to show.</summary>
    LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history);
}

/// <summary>A leaf's value of a state, the chance to win the combat from it, and the HP held at the end of a win.</summary>
public readonly record struct LeafOutcome(double Value, double Win, double Hp);
