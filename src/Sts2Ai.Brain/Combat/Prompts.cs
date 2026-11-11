using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>
/// The answer to a card prompt where nothing weighs the answers (docs: combat-brain/shared §1): the first options in the
/// prompt's canonical order, as few as it takes. It holds no view of what a card is worth. Search does not use it: there
/// a prompt is a state, and its answers are branches (<see cref="PromptAnswers"/>). It answers for a placeholder policy,
/// for a combat being built from an observation, and for PIMC at a prompt its plan did not reach, since no world can be
/// sampled in the middle of an action.
/// </summary>
public sealed class FirstPicks : IChoicePolicy
{
    public static FirstPicks Instance { get; } = new();

    public ChoiceAnswer Choose(ChoiceDecision decision) => new([.. Of(decision.Choice).Picks]);

    /// <summary>The answer as a combat action.</summary>
    public static CombatAction Of(CardChoice prompt) => CombatAction.Pick(Enumerable.Range(0, Math.Min(prompt.Min, prompt.Options.Count)));
}

/// <summary>
/// A card prompt's answers as the actions of the state that waits on it (docs: combat-brain/shared §1): search tries them
/// as it tries the plays of any other state, and the leaf values where they lead.
/// </summary>
public static class PromptAnswers
{
    /// <summary>The prompt's answers in its own order (<see cref="CardChoice.Answers"/>), at most <paramref name="most"/>
    /// and at least one: a prompt has to be answered.</summary>
    /// <param name="cut">Whether answers were left out.</param>
    public static List<CombatAction> Of(CardChoice prompt, int most, out bool cut)
    {
        most = Math.Max(1, most);
        var answers = prompt.Answers().Take(most + 1).Select(CombatAction.Pick).ToList();
        cut = answers.Count > most;
        return cut ? answers[..most] : answers;
    }
}
