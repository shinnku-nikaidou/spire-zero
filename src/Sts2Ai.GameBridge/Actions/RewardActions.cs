using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>
/// The rewards screen: "reward:i" takes reward i (index into the set, which is the screen's order), "rewards_done"
/// presses proceed. And the card reward screen opened by a card reward: "card:i", "card_alt:ID".
/// </summary>
public static class RewardActions
{
    /// <summary>A combat room's rewards screen ends the room: proceeding opens the map (<c>NRewardsScreen</c>).</summary>
    public static bool IsTerminal(RewardsSet set) => set.Room is CombatRoom;

    /// <summary>The buttons of the rewards not taken yet: what each shows, and the cards a card reward would show if opened.</summary>
    public static RewardsScreen Screen(RewardsSet set) => new()
    {
        Rewards = [.. set.Rewards.Select((reward, i) => (reward, i)).Where(r => !r.reward.SuccessfullySelected).Select(r => View(r.reward, r.i))],
    };

    private static RewardView View(Reward reward, int index) => reward switch
    {
        GoldReward gold => new() { Index = index, Kind = "gold", Amount = gold.Amount },
        PotionReward potion => new() { Index = index, Kind = "potion", Id = potion.Potion?.Id.Entry },
        RelicReward relic => new() { Index = index, Kind = "relic", Id = relic.Relic?.Id.Entry },
        CardReward card => new() { Index = index, Kind = "card", Cards = [.. card.Cards.Select(c => Observe.Card(c))] },
        // The card is private; the button's hover tip shows it.
        SpecialCardReward special => new() { Index = index, Kind = "special_card", Card = special.HoverTips.OfType<CardHoverTip>().Select(t => Observe.Card(t.Card)).FirstOrDefault() },
        CardRemovalReward => new() { Index = index, Kind = "remove_card" },
        _ => throw new NotSupportedException($"reward {reward.GetType().Name} has no view"),
    };

    /// <summary>
    /// Rewards not taken yet, then proceed unless the set forbids skipping. A potion reward with a full belt stays on
    /// the screen but cannot be taken until a slot is free, so it is left out until then.
    /// </summary>
    public static List<MacroAction> Legal(RewardsSet set)
    {
        var actions = new List<MacroAction>();
        for (int i = 0; i < set.Rewards.Count; i++)
        {
            var reward = set.Rewards[i];
            if (reward is LinkedRewardSet) throw new NotSupportedException("linked rewards are not handled (unused in this game version)");
            if (reward.SuccessfullySelected) continue;
            if (reward is PotionReward && !set.Player.HasOpenPotionSlots) continue;
            actions.Add(MacroAction.Of(MacroAction.Reward, i));
        }
        if (!set.DisallowSkipping || set.AllRewardsSuccessfullySelected) actions.Add(MacroAction.RewardsDone);
        return actions;
    }

    /// <summary>Takes a reward, as its button does. The task ends when the reward is resolved (true if taken).</summary>
    public static Task<bool> Start(RewardsSet set, MacroAction action)
    {
        if (action.Verb != MacroAction.Reward || !Legal(set).Contains(action)) throw Illegal.Action(action, Surfaces.Rewards);
        return RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(set.Rewards[action.Index]);
    }

    /// <summary>
    /// The proceed button. A non-terminal screen skips what is left and closes. A terminal one leaves it to room exit
    /// to skip the rest; after a boss it readies the act change, except between the two bosses of a double-boss act.
    /// </summary>
    public static Task Proceed(RunState run, RewardsSet set)
    {
        if (set.DisallowSkipping && !set.AllRewardsSuccessfullySelected) throw Illegal.Action(MacroAction.RewardsDone, Surfaces.Rewards);
        if (!IsTerminal(set))
        {
            RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
            return Task.CompletedTask;
        }
        if (run.CurrentRoom is { RoomType: RoomType.Boss } or { IsVictoryRoom: true }
            && !(run.Map.SecondBossMapPoint != null && run.CurrentMapCoord == run.Map.BossMapPoint.coord))
        {
            RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
            return Task.CompletedTask;
        }
        return RunManager.Instance.ProceedFromTerminalRewardsScreen();
    }

    public static CardRewardScreen CardRewardScreen(IReadOnlyList<CardCreationResult> cards, IReadOnlyList<CardRewardAlternative> alternatives) => new()
    {
        Cards = [.. cards.Select(c => Observe.Card(c.Card))],
        Alternatives = [.. alternatives.Select(a => a.OptionId)],
    };

    /// <summary>
    /// The card reward screen: each offered card, then each alternative (skip, reroll, ones added by relics) by id.
    /// <paramref name="spent"/> holds alternatives that stay listed but no longer do anything (a used reroll).
    /// </summary>
    public static List<MacroAction> CardRewardLegal(IReadOnlyList<CardCreationResult> cards, IReadOnlyList<CardRewardAlternative> alternatives,
        IReadOnlySet<CardRewardAlternative> spent)
    {
        var actions = Enumerable.Range(0, cards.Count).Select(i => MacroAction.Of(MacroAction.Card, i)).ToList();
        actions.AddRange(alternatives.Where(a => !spent.Contains(a)).Select(a => new MacroAction(MacroAction.CardAlt, a.OptionId)));
        return actions;
    }

    /// <summary>The selector's answer for <paramref name="action"/>.</summary>
    public static CardRewardSelection CardRewardAnswer(IReadOnlyList<CardCreationResult> cards, IReadOnlyList<CardRewardAlternative> alternatives, MacroAction action) => action.Verb switch
    {
        MacroAction.Card => new CardRewardSelection { card = cards[action.Index].Card },
        MacroAction.CardAlt => new CardRewardSelection { alternative = alternatives.First(a => a.OptionId == action.Arg) },
        _ => throw Illegal.Action(action, Surfaces.CardReward),
    };

    /// <summary>An alternative that leaves the screen open (reroll): picking it again would be a no-op.</summary>
    public static bool KeepsScreenOpen(CardRewardAlternative alternative) => alternative.AfterSelected == PostAlternateCardRewardAction.DoNothing;
}
