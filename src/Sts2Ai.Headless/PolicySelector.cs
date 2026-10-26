using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2Ai.Headless;

/// <summary>Decides the card-choice prompts that come up while a card or power resolves.</summary>
internal interface IChoicePolicy
{
    /// <summary>Indexes into <paramref name="options"/> of the cards to select; between min and max of them.</summary>
    IReadOnlyList<int> ChooseCards(IReadOnlyList<CardModel> options, int min, int max);
}

/// <summary>Uniformly random legal selection. Placeholder until the brain answers these too.</summary>
internal sealed class RandomChoices(Random rng) : IChoicePolicy
{
    public IReadOnlyList<int> ChooseCards(IReadOnlyList<CardModel> options, int min, int max)
    {
        int count = min == max ? min : rng.Next(min, max + 1);
        return Enumerable.Range(0, options.Count).OrderBy(_ => rng.Next()).Take(count).ToList();
    }
}

/// <summary>
/// Answers the game's card-choice prompts (discard X, exhaust X, choose 1 of 3, ...). Each answer is recorded so
/// a combat can be replayed exactly; when replaying, the recorded answers are given back in order before the live
/// policy is asked again.
/// </summary>
internal sealed class PolicySelector(IChoicePolicy policy, Action<int[]> record) : ICardSelector
{
    /// <summary>Set while a choose-a-card screen that cannot be skipped is asking (see HeadlessPatches).</summary>
    [ThreadStatic] public static bool MustPickOne;

    private readonly Queue<int[]> _replay = new();

    public int Prompts { get; private set; }
    public int PendingReplay => _replay.Count;

    public void QueueReplay(IEnumerable<int[]> picks)
    {
        foreach (var p in picks) _replay.Enqueue(p);
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        Prompts++;
        var cards = options.ToList();
        int max = Math.Min(maxSelect, cards.Count);
        int min = Math.Min(MustPickOne ? Math.Max(minSelect, 1) : minSelect, max);
        var picks = _replay.Count > 0 ? _replay.Dequeue() : policy.ChooseCards(cards, min, max).ToArray();
        if (picks.Length < min || picks.Length > max || picks.Distinct().Count() != picks.Length || picks.Any(i => i < 0 || i >= cards.Count))
            throw new InvalidOperationException($"bad card choice [{string.Join(",", picks)}] for {cards.Count} options, select {min}..{max}");
        record(picks);
        return Task.FromResult<IEnumerable<CardModel>>(picks.Select(i => cards[i]).ToList());
    }

    /// <summary>Post-combat card rewards are a run-level decision; inside a combat sim we always skip.</summary>
    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        Prompts++;
        return new CardRewardSelection();
    }
}
