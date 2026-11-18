using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

/// <summary>
/// The messages between the decision side and a simulator worker, one JSON line each: the worker
/// says hello once it has booted, then answers each request in order. A request carries everything the worker needs,
/// so any worker can serve it and a crashed worker's request can be sent again.
/// </summary>
public sealed record WorkerHello(string Game);

/// <summary>A request, with its type; its reply carries the same id.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SlMacroRequest), "sl_macro")]
[JsonDerivedType(typeof(SlPromptRequest), "sl_prompt")]
public abstract record WorkerRequest(long Id);

/// <summary>
/// L0's probes of a decision outside combat in a run: <see cref="Node"/> loaded and replayed
/// stands at the decision; each of <see cref="Paths"/> is played from there, in its own load of the clone, and the
/// reply's <see cref="WorkerReply.Afters"/> holds what the game asks next after each, in order. A path's answers are
/// macro actions and, for a card prompt, its picks in the prompt's canonical order (<see cref="MacroAction.Pick"/>).
/// </summary>
public sealed record SlMacroRequest(long Id, NodeClone Node, IReadOnlyList<IReadOnlyList<MacroAction>> Paths) : WorkerRequest(Id);

/// <summary>
/// The SL planner's way across a card prompt of a combat in a run, where the engine cannot start: <see cref="Node"/>
/// loaded and replayed stands at the prompt the real combat waits on, and <see cref="Path"/>, answers to it and to the
/// prompts they raise, is played from there. The reply's <see cref="WorkerReply.Crossed"/> is what stands there then.
/// </summary>
public sealed record SlPromptRequest(long Id, NodeClone Node, IReadOnlyList<CombatAction> Path) : WorkerRequest(Id);

/// <summary>
/// Where a path of answers from a card prompt leads in a node's clone (<see cref="SlPromptRequest"/>): the combat's
/// status, what it shows (another prompt, if one waits), and the run around it.
/// </summary>
/// <param name="HiddenKey">The world's hidden key there (<see cref="ICombatWorld.HiddenKey"/>), while the combat goes on.</param>
/// <param name="Export">Where the player can play again (the combat goes on and no prompt waits): the combat written
/// out for the engine, the bridge's <c>EngineExport</c> bytes in base64. Null otherwise.</param>
public sealed record PromptStep(CombatStatus Status, CombatObservation Observation, RunView Run, ulong HiddenKey = 0, string? Export = null);

/// <param name="GameErrors">The game's Log.Error lines while serving the request.</param>
/// <param name="Afters">The answer to an <see cref="SlMacroRequest"/>.</param>
/// <param name="Crossed">The answer to an <see cref="SlPromptRequest"/>.</param>
public sealed record WorkerReply(long Id, string? Error, IReadOnlyList<string> GameErrors,
    IReadOnlyList<Afterstate>? Afters = null, PromptStep? Crossed = null);

/// <summary>
/// What a path of answers from a decision in a node's clone leads to (<see cref="SlMacroRequest"/>): the run then, and
/// the next decision the game asks. <see cref="Surface"/> is null once the run is over, and <see cref="Won"/> then says how
/// it ended: the game kills the players when it records a win too, so the run's HP cannot tell. A card prompt's options
/// are in its screen (<see cref="CardChoiceScreen"/>), so its <see cref="Legal"/> is empty; a combat's screen
/// (<see cref="CombatScreen"/>) holds its first observation, and its actions are left out, as a combat ends a probe.
/// </summary>
public sealed record Afterstate(RunView Run, string? Surface, ScreenView? Screen, IReadOnlyList<MacroAction> Legal, bool? Won = null);

/// <summary>A line of play that search found, from its first action, and its value.</summary>
/// <param name="Nodes">The actions the search simulated after the line's first action, itself included.</param>
/// <param name="Hashes">For each of <paramref name="Actions"/>, the hash of the observation it is played at
/// (<see cref="ProtocolJson.Hash"/>): what the line expects to see before each step.</param>
/// <param name="Repeats">Whether the line repeats a cycle the search found.</param>
public sealed record PlannedLine(double Value, IReadOnlyList<CombatAction> Actions, int Nodes = 0, IReadOnlyList<ulong>? Hashes = null, bool Repeats = false);

/// <summary>
/// A state where a turn of the SL planner's line ends: at the next turn's first decision, after the enemies' turn, or
/// where the combat ended. As a whole line, the same from the planner's decision.
/// </summary>
/// <param name="Actions">The actions that reach it. Where the turn ended, its end is among them, followed by the answers
/// to any card prompt that the end or the next turn's start raised.</param>
/// <param name="Hashes">For each of <paramref name="Actions"/>, the hash of the observation it is played at.</param>
/// <param name="Value">The leaf's value of the state.</param>
/// <param name="Key">The state's observation and its full hidden state, hashed: states with the same key are the same
/// state. 0 once the combat is over.</param>
/// <param name="History">The record of enemy moves up to the state.</param>
/// <param name="Win">The chance to win the combat from the state, as the leaf predicts it: 1 or 0 once the combat is over.</param>
/// <param name="Hp">The HP held at the end of a win from the state, as the leaf expects it: the HP held once the combat is won.</param>
/// <param name="Life">The player's HP plus max HP at the state: at a win's end, the bound the planner's pruning keeps
/// lines with the same potions up to (the engine's search/bound.rs).</param>
/// <param name="Reach">The most HP plus max HP the combat can end with from the state, as the pruning bounds it
/// (search/bound.rs, <c>reach</c>).</param>
/// <param name="Potions">The potions held at the state, as the pruning tells states apart (search/bound.rs, <c>potions</c>).</param>
/// <param name="Shown">The hash of the observation at the state, card uids and all (<see cref="ProtocolJson.Hash"/>): two
/// lines to one key whose copies of a card went otherwise hold them under other uids, which the actions after it name.</param>
public sealed record TurnEnd(IReadOnlyList<CombatAction> Actions, IReadOnlyList<ulong> Hashes, CombatStatus Status, double Value,
    ulong Key, IReadOnlyList<EnemyTurns> History, double Win = 0, double Hp = 0, int Life = 0, int Reach = 0, string Potions = "",
    ulong Shown = 0);
