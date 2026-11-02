namespace Sts2Ai.Protocol;

/// <summary>What the AI may know of outcomes the game has not shown yet (docs: environments §2).</summary>
public enum PlayEnvironment { NoSL, SL }

public enum CombatStatus { InProgress, Won, Lost }

/// <summary>
/// A simulated combat that search can act in (docs: protocol/worlds §1). Search never touches a game directly: it gets
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
    /// <summary>Card prompts raised by the action are answered inside the world (docs: combat-brain/shared §1), the first
    /// by <see cref="CombatAction.Pick"/> if it has one.</summary>
    void Execute(CombatAction action);
    /// <summary>The first card prompt the last <see cref="Execute"/> raised, if another answer to it could be worth a look; else null.</summary>
    PromptSeen? LastPrompt => null;
    WorldSnapshot Snapshot();
    /// <summary>Goes back to a snapshot taken from this same world.</summary>
    void Restore(WorldSnapshot snapshot);
}

/// <summary>A card prompt that asks for one card, as a world met it.</summary>
/// <param name="Options">How many cards it offered.</param>
/// <param name="Answer">The option the world's prompt policy gave.</param>
public readonly record struct PromptSeen(int Options, int Answer);

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
/// An SL clone of the current node (docs: environments/information §7): the node's checkpoint at its entry (the game's save and the
/// relic fields it drops), and every decision since, as "surface action" (picks of a card prompt as the game's own
/// indexes). Opaque outside the simulator, which loads the save as the game's Continue does, puts the relic fields back
/// and replays the decisions. <see cref="Gauntlet"/> is true for a run of the boss gauntlet (<c>run --gauntlet</c>),
/// whose rule the clone keeps: a plain monster encounter won at its start.
/// </summary>
public sealed record NodeClone(string Save, IReadOnlyList<string> Trace, bool? Gauntlet = null);

/// <summary>An action that search scored: its mean value over the worlds, the 95% confidence half-width, and how many worlds.</summary>
/// <param name="Values">Its value in each world, in world order, to 4 decimals: the Q that ExIt's targets are made of
/// (docs: learning/loop §5).</param>
/// <param name="Nodes">The actions the search simulated below it in each world.</param>
public sealed record Candidate(string Action, double Value, double Ci, int Samples, IReadOnlyList<double>? Values = null, IReadOnlyList<int>? Nodes = null);
