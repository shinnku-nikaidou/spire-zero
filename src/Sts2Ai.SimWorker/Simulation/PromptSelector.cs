using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>
/// The game's card-choice prompts (discard X, exhaust X, choose 1 of 3, ...) as decisions of the driver's (docs:
/// simulator/runs §5). A prompt does not get its answer inside the game's call: it waits (<see cref="Waiting"/>), with
/// the pump held so that the game stands where it asked, until the driver answers it (<see cref="Answer"/>), as a player
/// answers one on its screen. The driver sees the options in canonical order (<see cref="CardChoices.Canonical"/>) with
/// the prompt's context (<see cref="ChoiceContexts"/>). An answer is recorded as indexes into the game's own list, so a
/// combat can be replayed exactly (<see cref="Replay"/>).
/// Card rewards and bundles are run-level decisions the game asks for inside its call, answered by the handlers: a
/// combat on its own has none, and one asked there throws.
/// </summary>
internal sealed class PromptSelector(
    Pump pump,
    Action<CardChoice, int[]> record,
    Func<IReadOnlyList<CardCreationResult>, IReadOnlyList<CardRewardAlternative>, CardRewardSelection>? cardReward = null,
    Func<IReadOnlyList<IReadOnlyList<CardModel>>, int>? bundle = null) : ICardSelector
{
    private Asked? _waiting;

    /// <summary>A prompt the game waits on: its options as the game listed them, where its answer goes, and whether
    /// the combat's own turns asked it (an enemy's move, what the start or the end of a turn brings) rather than an
    /// action of the player's.</summary>
    private sealed record Asked(CardChoice Choice, int[] GameIndex, List<CardModel> Cards, TaskCompletionSource<IEnumerable<CardModel>> Answer, bool ByTurns);

    /// <summary>Prompts asked so far; each one's number seeds its tie-break.</summary>
    public int Prompts { get; private set; }

    /// <summary>What an export of the live combat says of the program around the game: the count of the selector that
    /// answers the player's prompts, a session's, which is the game's local selector while the session lives.</summary>
    public static EngineExport.Around Around() =>
        RunManager.Instance.CardSelectorStack.LocalSelector is PromptSelector selector ? new(selector.Prompts)
            : throw new InvalidOperationException("the local card selector is not a session's");

    /// <summary>
    /// Makes <paramref name="selector"/> the one that answers <paramref name="player"/>'s prompts, until the scope given
    /// back is disposed. It is on the player's stack, where card rewards and bundles look for it, and it is the game's
    /// local selector, which answers where the player's screen does. In combat the stack does not show it
    /// (<c>HeadlessPatches.AskedOnTheScreen</c>): the game then asks as it asks a player, and what asked pauses first.
    /// </summary>
    public static IDisposable Install(Player player, PromptSelector selector)
    {
        var stack = RunManager.Instance.CardSelectorStack;
        var pushed = stack.PushSelector(player, selector);
        var local = stack.UseLocalSelector(selector);
        return new Scopes(local, pushed);
    }

    private sealed class Scopes(params IDisposable[] scopes) : IDisposable
    {
        public void Dispose()
        {
            foreach (var scope in scopes) scope.Dispose();
        }
    }

    /// <summary>The prompt the game waits on, if any.</summary>
    public CardChoice? Waiting => _waiting?.Choice;

    /// <summary>Numbers the next prompts from 0 again, as a session that starts here would.</summary>
    public void RestartPrompts() => Prompts = 0;

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        if (_waiting != null) throw new InvalidOperationException("a card prompt while another waits for its answer");
        int prompt = Prompts++;
        var cards = options.ToList();
        // Asked as the player's screen is (the local selector), the prompt comes after the entry's first await, where
        // the entry's context is the latest one, as the mod reads it.
        var context = ChoiceContexts.Current ?? ChoiceContexts.Last ?? throw new InvalidOperationException("card prompt outside the CardSelectCmd entries");
        var (min, max) = ChoiceContexts.Bounds(context, minSelect, maxSelect, cards.Count);
        // The tie-break only has to be unrelated to the hidden state, and the same on replay.
        var (choice, gameIndex) = CardChoices.Canonical(cards, min, max, context, new Random(prompt));
        // The player's cards and potions ask from inside their action, which the executor is executing then. What a
        // turn's end brings (a card a power plays there) asks as the end-turn action finishes, the executor still on
        // it: that action has finished, and the turns wait for the answer. A hook of the turns that asks (Tools of the
        // Trade at a turn's start) pauses, and asks from the action the game makes of its rest: the turns' too.
        var executor = RunManager.Instance.ActionExecutor;
        bool byAction = executor.IsRunning && executor.CurrentlyRunningAction is { State: not GameActionState.Finished } and not GenericHookGameAction;
        bool byTurns = CombatManager.Instance.DebugOnlyGetState() != null && !byAction;
        _waiting = new(choice, gameIndex, cards, new(), byTurns);
        pump.Held = true;
        return _waiting.Answer.Task;
    }

    /// <summary>
    /// Answers the waiting prompt with <paramref name="picks"/>, places in its canonical order. The game goes on from the
    /// prompt inside this call, as far as it gets without the pump.
    /// </summary>
    public void Answer(IReadOnlyList<int> picks)
    {
        var asked = _waiting ?? throw new InvalidOperationException("no card prompt waits for an answer");
        if (!asked.Choice.Accepts(picks))
            throw new InvalidOperationException($"bad card choice [{string.Join(",", picks)}] for {asked.Cards.Count} options, select {asked.Choice.Min}..{asked.Choice.Max}");
        Give(asked, [.. picks.Select(i => asked.GameIndex[i])]);
    }

    /// <summary>Answers the waiting prompt with a recorded answer: indexes into the game's own list.</summary>
    public void Replay(int[] picks)
    {
        var asked = _waiting ?? throw new InvalidOperationException($"replay diverged: no card prompt waits for the recorded answer [{string.Join(",", picks)}]");
        var (min, max, cards) = (asked.Choice.Min, asked.Choice.Max, asked.Cards);
        if (picks.Length < min || picks.Length > max || picks.Distinct().Count() != picks.Length || picks.Any(i => i < 0 || i >= cards.Count))
            throw new InvalidOperationException($"replayed card choice [{string.Join(",", picks)}] does not fit {cards.Count} options, select {min}..{max}");
        Give(asked, picks);
    }

    private void Give(Asked asked, int[] picks)
    {
        record(asked.Choice, picks);
        _waiting = null;
        pump.Held = false;
        asked.Answer.SetResult([.. picks.Select(i => asked.Cards[i])]);
    }

    /// <summary>
    /// Ends a waiting prompt with no answer, once its run is cleaned up. One that an action of the player's asked stays
    /// unanswered with that action, which nothing waits for. One that the combat's own turns asked has its wait
    /// cancelled, as the game's prompt screen cancels it when it leaves the tree with its run: the next combat's turns
    /// start only when the turns of the combat before it have ended, and they end by this.
    /// </summary>
    public void Drop()
    {
        var asked = _waiting;
        _waiting = null;
        pump.Held = false;
        if (asked is { ByTurns: true }) asked.Answer.TrySetCanceled();
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
