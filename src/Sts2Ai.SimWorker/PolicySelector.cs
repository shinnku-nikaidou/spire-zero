using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// Answers the game's card-choice prompts (discard X, exhaust X, choose 1 of 3, ...) by asking an
/// <see cref="IChoicePolicy"/>, which sees the options in canonical order (<see cref="CardChoices.Canonical"/>) with
/// the prompt's context (<see cref="ChoiceContexts"/>). Each answer is recorded as indexes into the game's own list,
/// so a combat can be replayed exactly; when replaying, the recorded answers are given back in order before the live
/// policy is asked again.
/// Card rewards and bundles are run-level decisions, answered by the handlers if given: without them a card reward is
/// skipped and a bundle is left to TestMode (the first one). <paramref name="observe"/>, if given, makes a prompt's
/// observation for the policy.
/// </summary>
internal sealed class PolicySelector(
    IChoicePolicy policy,
    Action<CardChoice, int[]> record,
    Func<CardChoice, Observation>? observe = null,
    Func<IReadOnlyList<CardCreationResult>, IReadOnlyList<CardRewardAlternative>, CardRewardSelection>? cardReward = null,
    Func<IReadOnlyList<IReadOnlyList<CardModel>>, int>? bundle = null) : ICardSelector
{
    private readonly Queue<int[]> _replay = new();

    public int Prompts { get; private set; }
    public int PendingReplay => _replay.Count;

    public void QueueReplay(IEnumerable<int[]> picks)
    {
        foreach (var p in picks) _replay.Enqueue(p);
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        int prompt = Prompts++;
        var cards = options.ToList();
        var context = ChoiceContexts.Current ?? throw new InvalidOperationException("card prompt outside the CardSelectCmd entries");
        var (min, max) = ChoiceContexts.Bounds(context, minSelect, maxSelect, cards.Count);
        // The tie-break only has to be unrelated to the hidden state, and the same on replay.
        var (choice, gameIndex) = CardChoices.Canonical(cards, min, max, context, new Random(prompt));
        int[] picks;
        if (_replay.Count > 0) picks = _replay.Dequeue();
        else
        {
            var answer = policy.Choose(new ChoiceDecision(choice, observe == null ? null : () => observe(choice)));
            if (!choice.Accepts(answer))
                throw new InvalidOperationException($"bad card choice [{string.Join(",", answer)}] for {cards.Count} options, select {min}..{max}");
            picks = answer.Select(i => gameIndex[i]).ToArray();
        }
        if (picks.Length < min || picks.Length > max || picks.Distinct().Count() != picks.Length || picks.Any(i => i < 0 || i >= cards.Count))
            throw new InvalidOperationException($"replayed card choice [{string.Join(",", picks)}] does not fit {cards.Count} options, select {min}..{max}");
        record(choice, picks);
        return Task.FromResult<IEnumerable<CardModel>>(picks.Select(i => cards[i]).ToList());
    }

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        Prompts++;
        return cardReward?.Invoke(options, alternatives) ?? new CardRewardSelection();
    }

    /// <summary>Whether <see cref="ChooseBundle"/> asks a handler (see HeadlessPatches).</summary>
    public bool ChoosesBundles => bundle != null;

    /// <summary>The index of the bundle to take.</summary>
    public int ChooseBundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        Prompts++;
        return bundle!(bundles);
    }
}
