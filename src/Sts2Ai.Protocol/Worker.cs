using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

/// <summary>
/// The messages between the decision side and a simulator worker (docs: simulator/pool §3), one JSON line each: the worker
/// says hello once it has booted, then answers each request in order. A request carries everything the worker needs,
/// so any worker can serve it and a crashed worker's request can be sent again.
/// </summary>
public sealed record WorkerHello(string Game);

/// <summary>A request, with its type; its reply carries the same id.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PimcWorldRequest), "pimc_world")]
[JsonDerivedType(typeof(SlTurnRequest), "sl_turn")]
[JsonDerivedType(typeof(SlMacroRequest), "sl_macro")]
public abstract record WorkerRequest(long Id);

/// <summary>
/// PIMC's search of one NoSL world of a combat decision (docs: pimc/budget §5): the world is built from
/// <see cref="Observation"/> with <see cref="BuildSeed"/>, then determinized as world <see cref="World"/> of
/// <see cref="Worlds"/> drawn from <see cref="SearchSeed"/>; the reply holds the best line after each of
/// <see cref="First"/>, in that order, each searched with <see cref="NodesPerFirst"/> nodes. <see cref="History"/> is the
/// combat's record of enemy moves so far, for the leaves.
/// </summary>
public sealed record PimcWorldRequest(
    long Id,
    CombatObservation Observation,
    IReadOnlyList<CombatAction> First,
    string BuildSeed,
    ulong SearchSeed,
    int World,
    int Worlds,
    int NodesPerFirst,
    IReadOnlyList<EnemyTurns> History) : WorkerRequest(Id);

/// <summary>
/// The SL planner's search of one turn from one state of a combat decision in a run (docs: sl-planner/budget §4):
/// <see cref="Node"/> loaded and replayed is the real combat at the decision, and <see cref="Path"/> played from there
/// reaches the state. The reply's <see cref="WorkerReply.Turn"/> holds the best ends of that turn: up to
/// <see cref="Keep"/> states at the next turn's start, and the best end of the combat, each above <see cref="Floor"/>
/// at best (the best whole line the planner holds), searched with <see cref="Nodes"/> nodes. <see cref="History"/> is
/// the record of enemy moves up to the state, for the leaves.
/// </summary>
public sealed record SlTurnRequest(long Id, NodeClone Node, IReadOnlyList<CombatAction> Path, int Nodes, int Keep, double? Floor,
    IReadOnlyList<EnemyTurns> History) : WorkerRequest(Id);

/// <summary>
/// L0's probes of a decision outside combat in a run (docs: macro-brain/search §6): <see cref="Node"/> loaded and replayed
/// stands at the decision; each of <see cref="Paths"/> is played from there, in its own load of the clone, and the
/// reply's <see cref="WorkerReply.Afters"/> holds what the game asks next after each, in order. A path's answers are
/// macro actions in text form and, for a card prompt, "pick:" and its picks in the prompt's canonical order.
/// </summary>
public sealed record SlMacroRequest(long Id, NodeClone Node, IReadOnlyList<IReadOnlyList<string>> Paths) : WorkerRequest(Id);

/// <param name="GameErrors">The game's Log.Error lines while serving the request.</param>
/// <param name="Turn">The answer to an <see cref="SlTurnRequest"/>.</param>
/// <param name="Afters">The answer to an <see cref="SlMacroRequest"/>.</param>
public sealed record WorkerReply(long Id, IReadOnlyList<PlannedLine>? Lines, string? Error, IReadOnlyList<string> GameErrors, TurnSearch? Turn = null,
    IReadOnlyList<Afterstate>? Afters = null);

/// <summary>
/// What a path of answers from a decision in a node's clone leads to (<see cref="SlMacroRequest"/>): the run then, and
/// the next decision the game asks. <see cref="Surface"/> is null once the run is over. A card prompt's options are in
/// its screen (<see cref="CardChoiceScreen"/>), so its <see cref="Legal"/> is empty; a combat's screen is left out, as
/// a combat ends a probe.
/// </summary>
public sealed record Afterstate(RunView Run, string? Surface, ScreenView? Screen, IReadOnlyList<string> Legal);

/// <summary>A line of play that search found, from its first action, and its value.</summary>
/// <param name="Nodes">The actions the search simulated after the line's first action, itself included.</param>
/// <param name="Hashes">For each of <paramref name="Actions"/>, the hash of the observation it is played at
/// (<see cref="ProtocolJson.Hash"/>): what the line expects to see before each step.</param>
/// <param name="Repeats">Whether the line repeats a cycle the search found (docs: pimc/search §6).</param>
public sealed record PlannedLine(double Value, IReadOnlyList<CombatAction> Actions, int Nodes = 0, IReadOnlyList<ulong>? Hashes = null, bool Repeats = false);

/// <summary>What the SL planner's search of one turn found (docs: sl-planner/turn-search §4).</summary>
/// <param name="Ends">The best end of the combat, if the turn can reach one, then the states at the next turn's start, best first.</param>
/// <param name="Nodes">The actions the search simulated.</param>
/// <param name="Cut">Whether anything was left out: the nodes ran out, or more states were found than kept.</param>
/// <param name="Bound">The bound of the state the search started from (<see cref="TurnEnd.Bound"/>).</param>
public sealed record TurnSearch(IReadOnlyList<TurnEnd> Ends, int Nodes, bool Cut, double Bound);

/// <summary>
/// A state where a turn of the SL planner's line ends: at the next turn's first decision, after the enemies' turn, or
/// where the combat ended. As a whole line, the same from the planner's decision.
/// </summary>
/// <param name="Actions">The actions that reach it, the last one ending the turn.</param>
/// <param name="Hashes">For each of <paramref name="Actions"/>, the hash of the observation it is played at.</param>
/// <param name="Value">The leaf's value of the state.</param>
/// <param name="Bound">The best the state can still reach: a win with the HP and the potions it has; once the combat is
/// over, its value.</param>
/// <param name="Key">The state's observation and its full hidden state, hashed: states with the same key are the same
/// state. 0 once the combat is over.</param>
/// <param name="History">The record of enemy moves up to the state.</param>
/// <param name="Win">The chance to win the combat from the state, as the leaf predicts it: 1 or 0 once the combat is over.</param>
/// <param name="Hp">The HP held at the end of a win from the state, as the leaf expects it: the HP held once the combat is won.</param>
public sealed record TurnEnd(IReadOnlyList<CombatAction> Actions, IReadOnlyList<ulong> Hashes, CombatStatus Status, double Value, double Bound,
    ulong Key, IReadOnlyList<EnemyTurns> History, double Win = 0, double Hp = 0);
