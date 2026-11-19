using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>The live combat written out for the engine to take in, and the run around it as it stands: what a world
/// made from it shows as its run (<see cref="ICombatWorld.Run"/>) before the combat changes more of it.</summary>
/// <param name="Bytes">The combat as the bridge's <c>EngineExport</c> wrote it.</param>
public sealed record CombatExport(byte[] Bytes, RunView Run);

/// <summary>
/// What a combat policy gets at each decision: the legal actions, and the observation on request. In a run, the run
/// observation around the combat too (<paramref name="inRun"/> makes it from the combat's); a combat started on its
/// own has none. A host that holds the live game can write the combat out for the engine (<paramref name="export"/>),
/// where the game stands between actions: not at a card prompt. A host that shows answers as they come and may move on
/// before one comes gives <paramref name="early"/>, <paramref name="hurry"/> and <paramref name="after"/>.
/// </summary>
public sealed class CombatDecision(IReadOnlyList<CombatAction> legal, Func<CombatObservation> observe, Func<CombatObservation, Observation>? inRun = null,
    Func<CombatExport>? export = null, Action<CombatDecisionResult>? early = null, CancellationToken hurry = default,
    Action<CombatDecisionResult>? after = null)
{
    private CombatObservation? _observation;
    private Observation? _runObservation;
    private CombatExport? _export;

    public IReadOnlyList<CombatAction> Legal { get; } = legal;

    /// <summary>Built on first use: a policy that ignores it shouldn't pay for it.</summary>
    public CombatObservation Observation => _observation ??= observe();

    /// <summary>Built on first use, like <see cref="Observation"/>.</summary>
    public Observation? RunObservation => _runObservation ??= inRun?.Invoke(Observation);

    /// <summary>The live combat for the engine, written on first use; null where the host has none to give.</summary>
    public CombatExport? Export => _export ??= export?.Invoke();

    /// <summary>Told the pick so far, by a brain that searches on before it answers (<see cref="SolverRoute"/>); null
    /// for a host that only takes answers.</summary>
    public Action<CombatDecisionResult>? Early { get; } = early;

    /// <summary>Cancelled when the host moved on from the decision: a brain waiting on its search answers at once.</summary>
    public CancellationToken Hurry { get; } = hurry;

    /// <summary>Told, after the answer, the answer again with the plan after its action each time the search the brain
    /// goes on with from there changes it (a better line, or one proved), on the searching thread, until the brain's next
    /// decision or the combat's end (<see cref="SolverRoute"/>); null for a host that only takes answers.</summary>
    public Action<CombatDecisionResult>? After { get; } = after;
}

/// <summary>
/// What a macro policy gets at each out-of-combat decision: the surface, its legal actions ("map:3,5", "reward:0",
/// "leave", ...), and the observation on request. In SL, Harness may add the
/// node's clone at the decision (<paramref name="clone"/>); in NoSL self-play, at an event, the node's clone for the
/// log's event lines alone (<paramref name="lines"/>).
/// </summary>
public sealed class MacroDecision(string surface, IReadOnlyList<MacroAction> legal, Func<Observation> observe, IMacroClone? clone = null,
    IMacroClone? lines = null)
{
    private Observation? _observation;

    public string Surface { get; } = surface;
    public IReadOnlyList<MacroAction> Legal { get; } = legal;

    /// <summary>SL: the node's clone at this decision, which the answer is valued on; null in NoSL.</summary>
    public IMacroClone? Clone { get; } = clone;

    /// <summary>
    /// NoSL self-play at an event: the node's clone on which each event option is played out as NoSL plays it, for
    /// V's training (<see cref="Macro.EventLines"/>). Nothing valued for the answer may read it: the answer is NoSL's,
    /// from the screen. Null otherwise.
    /// </summary>
    public IMacroClone? Lines { get; } = lines;

    /// <summary>The same decision with <paramref name="node"/>'s clone.</summary>
    public MacroDecision With(IMacroClone node) => new(Surface, Legal, () => Observation, node);

    /// <summary>The same decision with <paramref name="node"/>'s clone for the event lines alone.</summary>
    public MacroDecision WithLines(IMacroClone node) => new(Surface, Legal, () => Observation, lines: node);

    /// <summary>Built on first use, like <see cref="CombatDecision.Observation"/>.</summary>
    public Observation Observation => _observation ??= observe();
}

/// <summary>
/// A card prompt for a choice policy, and the observation it is asked in on
/// request. The observation is null where there is no run around the prompt (a combat started on its own). Outside
/// combat in SL, Harness may add the node's clone at the prompt (<paramref name="clone"/>).
/// </summary>
/// <param name="early">During a combat, as a combat decision's (<see cref="CombatDecision.Early"/>).</param>
/// <param name="hurry">During a combat, as a combat decision's (<see cref="CombatDecision.Hurry"/>).</param>
/// <param name="after">During a combat, as a combat decision's (<see cref="CombatDecision.After"/>).</param>
public sealed class ChoiceDecision(CardChoice choice, Func<Observation>? observe = null, IMacroClone? clone = null,
    Action<CombatDecisionResult>? early = null, CancellationToken hurry = default, Action<CombatDecisionResult>? after = null)
{
    private Observation? _observation;

    public CardChoice Choice { get; } = choice;

    /// <summary>SL, outside combat: the node's clone at this prompt; null otherwise.</summary>
    public IMacroClone? Clone { get; } = clone;

    public Action<CombatDecisionResult>? Early { get; } = early;

    public CancellationToken Hurry { get; } = hurry;

    public Action<CombatDecisionResult>? After { get; } = after;

    /// <summary>The same prompt with <paramref name="node"/>'s clone.</summary>
    public ChoiceDecision With(IMacroClone node) => new(Choice, () => Observation!, node, Early, Hurry, After);

    /// <summary>Built on first use, like <see cref="CombatDecision.Observation"/>.</summary>
    public Observation? Observation => _observation ??= observe?.Invoke();
}

/// <summary>
/// SL: the current node's clone at a decision outside combat. It plays paths of answers from the
/// decision in the real game and shows what the game asks after each: the true afterstates, random outcomes included.
/// </summary>
public interface IMacroClone
{
    /// <summary>
    /// For each of <paramref name="paths"/>, in order, what the game asks next after its answers from the decision on:
    /// macro actions, and for a card prompt its picks (<see cref="MacroAction.Pick"/>, canonical indexes, in order).
    /// </summary>
    IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<MacroAction>> paths);
}

/// <summary>A macro policy's answer to a decision.</summary>
/// <param name="Action">One of <see cref="MacroDecision.Legal"/>.</param>
/// <param name="Explored">Whether it was drawn by self-play exploration (<see cref="Uniform"/>, <see cref="Softmax"/>).</param>
/// <param name="Scored">The legal actions it was chosen among, each with its value, the chosen first and then best
/// first; none from a policy that values nothing, or for an answer that was drawn.</param>
/// <param name="State">SL: the state the answer was given at, as the node's clone keys it, for a host that says which
/// answers it played (<see cref="IMacroPolicy.Played"/>); null for a policy that keeps no states.</param>
/// <param name="Event">NoSL self-play, at an event, for a log: where each option whose result its button does not show
/// ends, played out on the node's clone with NoSL's decisions after it (<see cref="Macro.EventLines"/>); null otherwise.</param>
public sealed record MacroAnswer(MacroAction Action, bool Explored = false, IReadOnlyList<Candidate>? Scored = null, ulong? State = null,
    IReadOnlyList<EventLine>? Event = null)
{
    public IReadOnlyList<Candidate> Scored { get; init; } = Scored ?? [];
}

/// <summary>
/// An event's option as V learns it in NoSL (plans/2026-11-17-0306-nosl-shared-nets.md, 3.2;
/// plans/2026-11-18-1340-nosl-v0.md, 3.3), as NoSL's self-play logs it: its button; where it ends, played out on the
/// node's clone with NoSL's decisions after it; and whether it is the option the run took.
/// </summary>
/// <param name="Option">The option as its button shows it.</param>
/// <param name="End">The run where the option's line ends: the room left for the map (<paramref name="Ending"/> "left"),
/// or the run over ("died"); null where it ends otherwise.</param>
/// <param name="Ending">How the line ends: "left", "died", "won", "fight" (a combat starts: the combat net values that,
/// not V), "again" (back at a state already met or decided), "long" (past <see cref="Macro.EventLines.MaxSteps"/>
/// answers); null for an option not played out.</param>
/// <param name="Taken">True for the option the run took (V learns it from what followed in the run); null otherwise.</param>
public sealed record EventLine(EventOptionView Option, RunView? End = null, string? Ending = null, bool? Taken = null);

/// <summary>A choice policy's answer to a card prompt.</summary>
/// <param name="Picks">Indexes into <see cref="CardChoice.Options"/>; must satisfy <see cref="CardChoice.Accepts"/>.</param>
/// <param name="Explored">As <see cref="MacroAnswer.Explored"/>.</param>
/// <param name="Scored">The answers it was chosen among, as "pick:I,J", each with its value, the chosen first and then
/// best first; none from a policy that values nothing, or for an answer that was drawn.</param>
/// <param name="State">As <see cref="MacroAnswer.State"/> (<see cref="IChoicePolicy.Played"/>).</param>
public sealed record ChoiceAnswer(IReadOnlyList<int> Picks, bool Explored = false, IReadOnlyList<Candidate>? Scored = null, ulong? State = null)
{
    public IReadOnlyList<Candidate> Scored { get; init; } = Scored ?? [];
}

/// <summary>Picks the next action of a combat.</summary>
public interface ICombatPolicy
{
    CombatAction Choose(CombatDecision decision);
}

/// <summary>Answers the card-choice prompts that come up while a card or power resolves, or outside combat.</summary>
public interface IChoicePolicy
{
    ChoiceAnswer Choose(ChoiceDecision decision);

    /// <summary>As <see cref="IMacroPolicy.Played"/>, for a card prompt's answer.</summary>
    void Played(ulong state) { }
}

/// <summary>Picks an out-of-combat action (map, rewards, events, shop, rest, treasure, card rewards, bundles).</summary>
public interface IMacroPolicy
{
    MacroAnswer Choose(MacroDecision decision);

    /// <summary>The actions the policy plans after <paramref name="action"/>, for the highlight and the log; none by default.</summary>
    IReadOnlyList<MacroAction> Plan(MacroDecision decision, MacroAction action) => [];

    /// <summary>The host played the answer given at <paramref name="state"/> (<see cref="MacroAnswer.State"/>). Only a host
    /// that says which answers it played calls it: the mod, whose player may act instead of an answer.</summary>
    void Played(ulong state) { }
}
