using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// What a combat policy gets at each decision: the legal actions, and the observation on request. In a run, the run
/// observation around the combat too (<paramref name="inRun"/> makes it from the combat's); a combat started on its
/// own has none.
/// </summary>
public sealed class CombatDecision(IReadOnlyList<CombatAction> legal, Func<CombatObservation> observe, Func<CombatObservation, Observation>? inRun = null)
{
    private CombatObservation? _observation;
    private Observation? _runObservation;

    public IReadOnlyList<CombatAction> Legal { get; } = legal;

    /// <summary>Built on first use: a policy that ignores it shouldn't pay for it.</summary>
    public CombatObservation Observation => _observation ??= observe();

    /// <summary>Built on first use, like <see cref="Observation"/>.</summary>
    public Observation? RunObservation => _runObservation ??= inRun?.Invoke(Observation);
}

/// <summary>
/// What a macro policy gets at each out-of-combat decision: the surface, its legal actions in text form ("map:3,5",
/// "reward:0", "leave", ...; grammar in docs: protocol §3), and the observation on request. In SL, Harness may add the
/// node's clone at the decision (<paramref name="clone"/>).
/// </summary>
public sealed class MacroDecision(string surface, IReadOnlyList<string> legal, Func<Observation> observe, IMacroClone? clone = null)
{
    private Observation? _observation;

    public string Surface { get; } = surface;
    public IReadOnlyList<string> Legal { get; } = legal;

    /// <summary>SL: the node's clone at this decision; null in NoSL.</summary>
    public IMacroClone? Clone { get; } = clone;

    /// <summary>The same decision with <paramref name="node"/>'s clone.</summary>
    public MacroDecision With(IMacroClone node) => new(Surface, Legal, () => Observation, node);

    /// <summary>Built on first use, like <see cref="CombatDecision.Observation"/>.</summary>
    public Observation Observation => _observation ??= observe();
}

/// <summary>
/// A card prompt for a choice policy, and the observation it is asked in (docs: protocol §3, surface card_choice) on
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
/// SL (docs: macro-brain §2): the current node's clone at a decision outside combat. It plays paths of answers from the
/// decision in the real game and shows what the game asks after each: the true afterstates, random outcomes included.
/// </summary>
public interface IMacroClone
{
    /// <summary>
    /// For each of <paramref name="paths"/>, in order, what the game asks next after its answers from the decision on:
    /// macro actions in text form, and for a card prompt "pick:" and its picks in the prompt's canonical order.
    /// </summary>
    IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<string>> paths);
}

/// <summary>Picks the next action of a combat.</summary>
public interface ICombatPolicy
{
    CombatAction Choose(CombatDecision decision);
}

/// <summary>Answers the card-choice prompts that come up while a card or power resolves.</summary>
public interface IChoicePolicy
{
    /// <summary>Indexes into <see cref="CardChoice.Options"/>; must satisfy <see cref="CardChoice.Accepts"/>.</summary>
    IReadOnlyList<int> Choose(ChoiceDecision decision);

    /// <summary>Whether the last answer was drawn by self-play exploration and valued below the policy's best
    /// (<see cref="Softmax.Explored"/>); never by default.</summary>
    bool Explored => false;
}

/// <summary>Picks an out-of-combat action (map, rewards, events, shop, rest, treasure, card rewards, bundles).</summary>
public interface IMacroPolicy
{
    /// <summary>One of <see cref="MacroDecision.Legal"/>.</summary>
    string Choose(MacroDecision decision);

    /// <summary>The actions the policy plans after <paramref name="action"/>, for the highlight and the log; none by default.</summary>
    IReadOnlyList<string> Plan(MacroDecision decision, string action) => [];

    /// <summary>As <see cref="IChoicePolicy.Explored"/>.</summary>
    bool Explored => false;
}
