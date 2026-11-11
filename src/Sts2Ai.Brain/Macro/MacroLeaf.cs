using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// What a run is worth where the macro brain stops looking ahead (docs: macro-brain/search §2): the one place its
/// options get their values, by one rule.
///
/// The game's rules come first. A run that is over is worth 1 if won and 0 if lost: the game kills the players when it
/// records a win too, so V would read a won run as lost. A combat about to start is worth what the combat leaf says, the
/// HP it will cost and each ending priced by V: V reads a run in a room as the state after that room, so alone it would
/// take the fight as won at no cost.
///
/// Any other state is V's, and V reads a run at one of two kinds of state, which it does not value on one scale: done
/// with the point it is at (a run in a room, or standing on the map), or entering a point (a map move's afterstate,
/// <see cref="MapView.Entering"/>). So the options of one decision are all valued at the same kind. Inside a node, done
/// with its point: where the node's screens end or the search stops. On the map, entering the next point: a move as
/// its afterstate, and an option that stays on the map (a potion's) as the best move after it (<see cref="BestMove"/>);
/// valued as the state it stays in, such an option won for nothing (v9-2: staying beat the best move in 19 of 26 map
/// decisions, by 1 point at the median).
/// </summary>
/// <param name="fights">Values a combat about to start (an event's fight); a state that needs it without it throws.</param>
public sealed class MacroLeaf(IRunValue value, ICombatLeaf? fights = null)
{
    /// <summary>A run that is over and lost.</summary>
    public const double Lost = 0;

    /// <summary>A run that is over and won.</summary>
    public const double Won = 1;

    /// <summary>A run not over and not about to fight: V.</summary>
    public double Value(RunView run) => value.Value(run);

    /// <summary>A state the game was played to.</summary>
    public double Value(Afterstate after) => after switch
    {
        { Surface: null } => (after.Won ?? throw new InvalidDataException("a run over with no outcome")) ? Won : Lost,
        { Screen: CombatScreen { Combat: var combat } } =>
            (fights ?? throw new InvalidOperationException("an answer starts a combat, and the search has no combat leaf to value it"))
            .Value(CombatStatus.InProgress, combat, after.Run, []),
        _ => value.Value(after.Run),
    };

    /// <summary>A run standing on the map, valued as the map's moves are: entering the best of <paramref name="moves"/>.</summary>
    public double BestMove(RunView run, IReadOnlyList<MacroAction> moves) =>
        moves.Max(move => value.Value(ScreenAfterstates.Entered(run, move.Coord)));
}
