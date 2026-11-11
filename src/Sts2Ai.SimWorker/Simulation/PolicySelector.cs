using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>
/// The game's card-choice prompts (discard X, exhaust X, choose 1 of 3, ...) as the driver's to answer (docs:
/// simulator/runs §5). A prompt does not get its answer inside the game's call: it waits (<see cref="Waiting"/>), with
/// the pump held so that the game stands where it asked, until the driver answers it (<see cref="Answer"/>), as a player
/// answers one on its screen. The answer comes from an <see cref="IChoicePolicy"/>, which sees the options in canonical
/// order (<see cref="CardChoices.Canonical"/>) with the prompt's context (<see cref="ChoiceContexts"/>). It is recorded
/// as indexes into the game's own list, so a combat can be replayed exactly; when replaying, the recorded answers are
/// given back in order before the live policy is asked again.
/// Card rewards and bundles are run-level decisions the game asks for inside its call, answered by the handlers: a
/// combat on its own has none, and one asked there throws. <paramref name="observe"/>, if given, makes a prompt's
/// observation for the policy.
/// </summary>
internal sealed class PolicySelector(
    Pump pump,
    IChoicePolicy policy,
    Action<CardChoice, int[]> record,
    Func<CardChoice, Observation>? observe = null,
    Func<IReadOnlyList<CardCreationResult>, IReadOnlyList<CardRewardAlternative>, CardRewardSelection>? cardReward = null,
    Func<IReadOnlyList<IReadOnlyList<CardModel>>, int>? bundle = null) : ICardSelector
{
    private readonly Queue<int[]> _replay = new();
    private Asked? _waiting;

    /// <summary>A prompt the game waits on: its options as the game listed them, and where its answer goes.</summary>
    private sealed record Asked(CardChoice Choice, int[] GameIndex, List<CardModel> Cards, int Min, int Max,
        TaskCompletionSource<IEnumerable<CardModel>> Answer);

    /// <summary>Prompts asked so far; each one's number seeds its tie-break.</summary>
    public int Prompts { get; private set; }
    public int PendingReplay => _replay.Count;

    /// <summary>Whether the game waits for a prompt's answer.</summary>
    public bool Waiting => _waiting != null;

    /// <summary>Numbers the next prompts from 0 again, as a session that starts here would.</summary>
    public void RestartPrompts() => Prompts = 0;

    public void QueueReplay(IEnumerable<int[]> picks)
    {
        foreach (var p in picks) _replay.Enqueue(p);
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        if (_waiting != null) throw new InvalidOperationException("a card prompt while another waits for its answer");
        int prompt = Prompts++;
        var cards = options.ToList();
        var context = ChoiceContexts.Current ?? throw new InvalidOperationException("card prompt outside the CardSelectCmd entries");
        var (min, max) = ChoiceContexts.Bounds(context, minSelect, maxSelect, cards.Count);
        // The tie-break only has to be unrelated to the hidden state, and the same on replay.
        var (choice, gameIndex) = CardChoices.Canonical(cards, min, max, context, new Random(prompt));
        _waiting = new(choice, gameIndex, cards, min, max, new());
        pump.Held = true;
        return _waiting.Answer.Task;
    }

    /// <summary>
    /// Answers the waiting prompt: with the next recorded answer when replaying, else the policy's. The game goes on from
    /// the prompt inside this call, as far as it gets without the pump.
    /// </summary>
    public void Answer()
    {
        var asked = _waiting ?? throw new InvalidOperationException("no card prompt waits for an answer");
        var (choice, cards, min, max) = (asked.Choice, asked.Cards, asked.Min, asked.Max);
        int[] picks;
        if (_replay.Count > 0) picks = _replay.Dequeue();
        else
        {
            var answer = policy.Choose(new ChoiceDecision(choice, observe == null ? null : () => observe(choice))).Picks;
            if (!choice.Accepts(answer))
                throw new InvalidOperationException($"bad card choice [{string.Join(",", answer)}] for {cards.Count} options, select {min}..{max}");
            picks = [.. answer.Select(i => asked.GameIndex[i])];
        }
        if (picks.Length < min || picks.Length > max || picks.Distinct().Count() != picks.Length || picks.Any(i => i < 0 || i >= cards.Count))
            throw new InvalidOperationException($"replayed card choice [{string.Join(",", picks)}] does not fit {cards.Count} options, select {min}..{max}");
        record(choice, picks);
        Drop();
        asked.Answer.SetResult([.. picks.Select(i => cards[i])]);
    }

    /// <summary>Forgets a waiting prompt, which then never gets its answer: its session ends.</summary>
    public void Drop()
    {
        _waiting = null;
        pump.Held = false;
    }

    /// <summary>Pumps until <paramref name="done"/> holds, answering the prompts met on the way.</summary>
    public void Run(Func<bool> done, Func<string> describe)
    {
        while (true)
        {
            pump.RunUntil(() => Waiting || done(), describe);
            if (!Waiting) return;
            Answer();
        }
    }

    /// <summary>Runs what is queued (<see cref="Pump.Drain"/>), answering the prompts met on the way.</summary>
    public void Flush()
    {
        while (true)
        {
            pump.Drain();
            if (!Waiting) return;
            Answer();
        }
    }

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        Prompts++;
        return (cardReward ?? throw new InvalidOperationException("a card reward where nothing answers one: a combat on its own"))(options, alternatives);
    }

    /// <summary>The index of the bundle to take (see HeadlessPatches).</summary>
    public int ChooseBundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        Prompts++;
        return (bundle ?? throw new InvalidOperationException("a bundle where nothing answers one: a combat on its own"))(bundles);
    }
}
