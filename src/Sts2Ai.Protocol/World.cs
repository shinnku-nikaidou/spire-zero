namespace Sts2Ai.Protocol;

/// <summary>What the AI may know of outcomes the game has not shown yet (docs: environments §1).</summary>
public enum PlayEnvironment { NoSL, SL }

public enum CombatStatus { InProgress, Won, Lost }

/// <summary>
/// A simulated combat that search can act in (docs: protocol §7). Search never touches a game directly: it gets
/// worlds from an <see cref="IWorldSource"/>. A NoSL world is one sample of the hidden state that agrees with the
/// observation; an SL world is an exact copy of the real combat.
/// </summary>
public interface ICombatWorld
{
    CombatObservation Observe();
    /// <summary>Empty once the combat is over.</summary>
    IReadOnlyList<CombatAction> Legal();
    CombatStatus Status { get; }
    /// <summary>
    /// What <see cref="Observe"/> hides that changes how the same plays turn out from here (the draw pile's order, the
    /// position of each random stream, a monster's queued move), as a hash. Together with the observation it tells
    /// states apart for search, within this world.
    /// </summary>
    ulong HiddenKey();
    /// <summary>Card prompts raised by the action are answered inside the world (docs: combat-brain §2, rule 7).</summary>
    void Execute(CombatAction action);
    WorldSnapshot Snapshot();
    /// <summary>Goes back to a snapshot taken from this same world.</summary>
    void Restore(WorldSnapshot snapshot);
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
/// An SL clone of the current node (docs: environments §6): the game's save at the node's entry, and every decision
/// since, as "surface action" (picks of a card prompt as the game's own indexes). Opaque outside the simulator, which
/// loads the save as the game's Continue does and replays the decisions.
/// </summary>
public sealed record NodeClone(string Save, IReadOnlyList<string> Trace);

/// <summary>An action that search scored: its mean value over the worlds, the 95% confidence half-width, and how many worlds.</summary>
/// <param name="Values">Its value in each world, in world order, to 4 decimals: the Q that ExIt's targets are made of
/// (docs: learning §2).</param>
/// <param name="Nodes">The actions the search simulated below it in each world.</param>
public sealed record Candidate(string Action, double Value, double Ci, int Samples, IReadOnlyList<double>? Values = null, IReadOnlyList<int>? Nodes = null);
