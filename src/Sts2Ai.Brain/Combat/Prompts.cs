using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>
/// The answer to a card prompt in combat that no search weighs (docs: combat-brain/shared §1): the first options in the
/// prompt's canonical order, as few as it takes. It holds no view of what a card is worth: search tries the other answers
/// of each action's first prompt (<see cref="PromptSeen.Others"/>), and the leaf values where they lead.
/// </summary>
public sealed class FirstPicks : IChoicePolicy
{
    public static FirstPicks Instance { get; } = new();

    public IReadOnlyList<int> Choose(ChoiceDecision decision) => [.. Enumerable.Range(0, Math.Min(decision.Choice.Min, decision.Choice.Options.Count))];
}

/// <summary>
/// A card prompt's answers as branches of search (docs: combat-brain/shared §1): after an action is played in a search world,
/// the other answers of the first prompt it met are actions to try next, each the same action carrying its answer.
/// </summary>
public static class PromptAnswers
{
    /// <summary>
    /// The other answers to the first prompt that <paramref name="action"/>, just played in <paramref name="world"/>, met, at
    /// most <paramref name="most"/>; none if it met none or already carries an answer.
    /// </summary>
    /// <param name="cut">Whether answers were left out.</param>
    public static List<CombatAction> Others(ICombatWorld world, CombatAction action, int most, out bool cut)
    {
        cut = false;
        if (action.Pick != null || world.LastPrompt is not { } prompt) return [];
        most = Math.Max(0, most);
        var answers = prompt.Others().Take(most + 1).Select(p => action with { Pick = p }).ToList();
        if (answers.Count <= most) return answers;
        cut = true;
        return answers[..most];
    }
}

/// <summary>
/// A search world's prompts (docs: combat-brain/shared §1): <paramref name="policy"/> answers them, as in the run, unless the
/// action being played carries its own answer to its first (<see cref="CombatAction.Pick"/>). It notes that first prompt
/// and the answer it got, so search can try the others.
/// </summary>
public sealed class SearchPrompts(IChoicePolicy policy) : IChoicePolicy
{
    private IReadOnlyList<int>? _pick;
    private bool _asked;

    /// <summary>The first prompt of the action being played, or of the last one played.</summary>
    public PromptSeen? Seen { get; private set; }

    /// <summary>A new action is about to be played, with its answer to its first prompt, if it carries one.</summary>
    public void Arm(CombatAction action) => (_pick, _asked, Seen) = (action.Picks, false, null);

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        if (_asked) return policy.Choose(decision);
        _asked = true;
        var answer = _pick is { } pick && decision.Choice.Accepts(pick) ? pick : policy.Choose(decision);
        Seen = new(decision.Choice, answer);
        return answer;
    }
}
