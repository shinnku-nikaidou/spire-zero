namespace Sts2Ai.Protocol;

/// <summary>What the AI may know of outcomes the game has not shown yet.</summary>
public enum PlayEnvironment { NoSL, SL }

public enum CombatStatus { InProgress, Won, Lost }

/// <summary>
/// A simulated combat that search can act in. Search never touches a game directly: it gets
/// worlds from an <see cref="IWorldSource"/>. A NoSL world is one sample of the hidden state that agrees with the
/// observation; an SL world is an exact copy of the real combat.
/// </summary>
public interface ICombatWorld
{
    CombatObservation Observe();
    /// <summary>
    /// The run around the combat as it stands in this world, as V reads it: the deck with what
    /// the combat has changed for good (a card grown, one stolen), the relics, the potions, HP, max HP and gold.
    /// </summary>
    RunView Run();
    /// <summary>The plays the game allows. Empty once the combat is over, and at a card prompt, whose answers are the
    /// actions then (<see cref="CombatObservation.Prompt"/>, <see cref="CardChoice.Answers"/>).</summary>
    IReadOnlyList<CombatAction> Legal();
    CombatStatus Status { get; }
    /// <summary>
    /// What <see cref="Observe"/> hides that changes how the same plays turn out from here (the draw pile's order, the
    /// position of each random stream, a monster's queued move), as a hash. Together with the observation it tells
    /// states apart for search, within this world.
    /// </summary>
    ulong HiddenKey();
    /// <summary>
    /// Takes the action and goes on to where the player can act again: their turn, or a card prompt the action raised,
    /// which is a state like any other, answered by the next action.
    /// </summary>
    void Execute(CombatAction action);
    WorldSnapshot Snapshot();
    /// <summary>Goes back to a snapshot taken from this same world.</summary>
    void Restore(WorldSnapshot snapshot);
    /// <summary>
    /// The hash of an observation this world has just made, as a plan keeps it for each step
    /// (<see cref="ProtocolJson.Hash{T}"/>). A world that wrote the observation as text has it from the text.
    /// </summary>
    ulong Hash(CombatObservation observation) => ProtocolJson.Hash(observation);
    /// <summary>
    /// The state as search tells states apart (<see cref="ProtocolJson.StateKey"/>) of an observation this world has
    /// just made. A world that can tell its state apart without the observation's records does.
    /// </summary>
    ulong StateKey(CombatObservation observation) => ProtocolJson.StateKey(observation);
    /// <summary>
    /// Told that a snapshot taken from this world will not be restored again, so the world lets go of what it kept
    /// for it: search says so when it leaves the state. A world whose snapshots hold nothing of its own (the steps
    /// to replay) has nothing to do.
    /// </summary>
    void Forget(WorldSnapshot snapshot) { }
}

/// <summary>Hands out the worlds for one decision.</summary>
public interface IWorldSource
{
    PlayEnvironment Environment { get; }
    /// <summary>NoSL: <paramref name="k"/> samples drawn from <paramref name="searchSeed"/>. SL: the one exact copy.</summary>
    IReadOnlyList<ICombatWorld> Worlds(int k, ulong searchSeed);
}

/// <summary>
/// A point in a world, as the steps taken since the world began, in their text form. Opaque to search: only the world
/// that made it reads it, and restores it by replay.
/// </summary>
public sealed record WorldSnapshot(IReadOnlyList<string> Steps);

/// <summary>
/// An SL clone of the current node: the node's checkpoint at its entry (the game's save and the
/// relic fields it drops), and every decision since, as "surface action" (picks of a card prompt as the game's own
/// indexes). Opaque outside the simulator, which loads the save as the game's Continue does, puts the relic fields back
/// and replays the decisions. <see cref="AutoWin"/> is the run's rule (<see cref="RunView.AutoWin"/>), which the clone
/// keeps: null in the normal game.
/// </summary>
public sealed record NodeClone(string Save, IReadOnlyList<string> Trace, IReadOnlyList<int>? AutoWin = null);

/// <summary>An action that search scored: its mean value over the worlds, the 95% confidence half-width, and how many worlds.
/// On a macro decision or a card prompt outside combat the value is V's, the chance to win the run.</summary>
/// <param name="Values">Its value in each world, in world order, to 4 decimals: the Q that ExIt's targets are made of.</param>
/// <param name="Win">The SL planner's: the chance to win the combat on the action's best line, 1 or 0 where the line
/// reaches the combat's end, else the combat net's.</param>
/// <param name="Hp">With <paramref name="Win"/>: the HP held at the end of a win on that line, expected where it is the net's.</param>
/// <param name="Plan">On the map, the route to the boss after the action, as the macro brain would take it. Elsewhere
/// outside combat in SL, the answers after it within the node along which its value was found (the node's clone search),
/// such as the card a card reward's opening was valued by.</param>
public sealed record Candidate(string Action, double Value, double Ci, int Samples, IReadOnlyList<double>? Values = null,
    double? Win = null, double? Hp = null, IReadOnlyList<string>? Plan = null);
