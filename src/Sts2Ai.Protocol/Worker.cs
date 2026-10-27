using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

/// <summary>
/// The messages between the decision side and a simulator worker (docs: simulator §8), one JSON line each: the worker
/// says hello once it has booted, then answers each request in order. A request carries everything the worker needs,
/// so any worker can serve it and a crashed worker's request can be sent again.
/// </summary>
public sealed record WorkerHello(string Game);

/// <summary>A request, with its type; its reply carries the same id.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PimcWorldRequest), "pimc_world")]
[JsonDerivedType(typeof(SlWorldRequest), "sl_world")]
public abstract record WorkerRequest(long Id);

/// <summary>
/// PIMC's search of one NoSL world of a combat decision (docs: combat-brain §2): the world is built from
/// <see cref="Observation"/> with <see cref="BuildSeed"/>, then determinized as world <see cref="World"/> of
/// <see cref="Worlds"/> drawn from <see cref="SearchSeed"/>; the reply holds the best line after each of
/// <see cref="First"/>, in that order, each searched with <see cref="NodesPerFirst"/> nodes.
/// </summary>
public sealed record PimcWorldRequest(
    long Id,
    CombatObservation Observation,
    IReadOnlyList<CombatAction> First,
    string BuildSeed,
    ulong SearchSeed,
    int World,
    int Worlds,
    int NodesPerFirst) : WorkerRequest(Id);

/// <summary>
/// PIMC's search of the SL world of a combat decision in a run (docs: simulator §7): <see cref="Node"/> loaded and
/// replayed is the real combat at the decision. The reply holds the best line after each of <see cref="First"/>, in
/// that order, each searched with <see cref="NodesPerFirst"/> nodes.
/// </summary>
public sealed record SlWorldRequest(long Id, NodeClone Node, IReadOnlyList<CombatAction> First, int NodesPerFirst)
    : WorkerRequest(Id);

/// <param name="GameErrors">The game's Log.Error lines while serving the request.</param>
public sealed record WorkerReply(long Id, IReadOnlyList<PlannedLine>? Lines, string? Error, IReadOnlyList<string> GameErrors);

/// <summary>A line of play that search found, from its first action, and its value.</summary>
/// <param name="Nodes">The actions the search simulated after the line's first action, itself included.</param>
public sealed record PlannedLine(double Value, IReadOnlyList<CombatAction> Actions, int Nodes = 0);
