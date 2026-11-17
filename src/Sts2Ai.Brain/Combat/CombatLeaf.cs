using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>
/// What a search leaf is worth: a state after the turn ends, or after the combat does, with
/// the run around it as it stands there (<see cref="ICombatWorld.Run"/>) and the public record of the enemies' moves
/// before it (<see cref="MoveRecord"/>). The combat net (<see cref="CombatNet"/>), its endings priced by V.
/// </summary>
public interface ICombatLeaf
{
    /// <summary>This leaf in <paramref name="env"/>: one that reads the environment (<see cref="CombatNet"/>) marks its
    /// states so; another is the same in every environment.</summary>
    ICombatLeaf In(PlayEnvironment env) => this;

    double Value(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history) =>
        Outcome(status, obs, run, history).Value;

    /// <summary>The value with how the combat is expected to end, from one evaluation: what the SL planner keeps for each line, to show.</summary>
    LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history);

    /// <summary>
    /// The outcomes of several states, in their order: the ends of one turn's search, which the SL planner asks about
    /// together once the turn is searched. A leaf that reads a net evaluates them in batches, which is far cheaper a
    /// state than one evaluation each; the batches depend on the states and their order alone, so the same search
    /// gets the same values wherever it runs.
    /// </summary>
    IReadOnlyList<LeafOutcome> Outcomes(IReadOnlyList<LeafState> states) =>
        [.. states.Select(s => Outcome(s.Status, s.Obs, s.Run, s.History))];
}

/// <summary>A state a leaf is asked about: what <see cref="ICombatLeaf.Outcome"/> takes.</summary>
public readonly record struct LeafState(CombatStatus Status, CombatObservation Obs, RunView Run, IReadOnlyList<EnemyTurns> History);

/// <summary>A leaf's value of a state, the chance to win the combat from it, and the HP held at the end of a win.</summary>
public readonly record struct LeafOutcome(double Value, double Win, double Hp);
