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
/// where the game stands between actions: not at a card prompt.
/// </summary>
public sealed class CombatDecision(IReadOnlyList<CombatAction> legal, Func<CombatObservation> observe, Func<CombatObservation, Observation>? inRun = null,
    Func<CombatExport>? export = null)
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
}

/// <summary>
/// What a macro policy gets at each out-of-combat decision: the surface, its legal actions ("map:3,5", "reward:0",
/// "leave", ...), and the observation on request. In SL, Harness may add the
/// node's clone at the decision (<paramref name="clone"/>).
/// </summary>
public sealed class MacroDecision(string surface, IReadOnlyList<MacroAction> legal, Func<Observation> observe, IMacroClone? clone = null)
{
    private Observation? _observation;

    public string Surface { get; } = surface;
    public IReadOnlyList<MacroAction> Legal { get; } = legal;

    /// <summary>SL: the node's clone at this decision; null in NoSL.</summary>
    public IMacroClone? Clone { get; } = clone;

    /// <summary>The same decision with <paramref name="node"/>'s clone.</summary>
    public MacroDecision With(IMacroClone node) => new(Surface, Legal, () => Observation, node);

    /// <summary>Built on first use, like <see cref="CombatDecision.Observation"/>.</summary>
    public Observation Observation => _observation ??= observe();
}

/// <summary>
/// A card prompt for a choice policy, and the observation it is asked in on
/// request. The observation is null where there is no run around the prompt (a combat started on its own). Outside
/// combat in SL, Harness may add the node's clone at the prompt (<paramref name="clone"/>).
/// </summary>
public sealed class ChoiceDecision(CardChoice choice, Func<Observation>? observe = null, IMacroClone? clone = null)
{
    private Observation? _observation;

    public CardChoice Choice { get; } = choice;

    /// <summary>SL, outside combat: the node's clone at this prompt; null otherwise.</summary>
    public IMacroClone? Clone { get; } = clone;

    /// <summary>The same prompt with <paramref name="node"/>'s clone.</summary>
    public ChoiceDecision With(IMacroClone node) => new(Choice, () => Observation!, node);

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
public sealed record MacroAnswer(MacroAction Action, bool Explored = false, IReadOnlyList<Candidate>? Scored = null)
{
    public IReadOnlyList<Candidate> Scored { get; init; } = Scored ?? [];
}

/// <summary>A choice policy's answer to a card prompt.</summary>
/// <param name="Picks">Indexes into <see cref="CardChoice.Options"/>; must satisfy <see cref="CardChoice.Accepts"/>.</param>
/// <param name="Explored">As <see cref="MacroAnswer.Explored"/>.</param>
/// <param name="Scored">The answers it was chosen among, as "pick:I,J", each with its value, the chosen first and then
/// best first; none from a policy that values nothing, or for an answer that was drawn.</param>
public sealed record ChoiceAnswer(IReadOnlyList<int> Picks, bool Explored = false, IReadOnlyList<Candidate>? Scored = null)
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
}

/// <summary>Picks an out-of-combat action (map, rewards, events, shop, rest, treasure, card rewards, bundles).</summary>
public interface IMacroPolicy
{
    MacroAnswer Choose(MacroDecision decision);

    /// <summary>The actions the policy plans after <paramref name="action"/>, for the highlight and the log; none by default.</summary>
    IReadOnlyList<MacroAction> Plan(MacroDecision decision, MacroAction action) => [];
}
