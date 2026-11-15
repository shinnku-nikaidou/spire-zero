using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// The current node's SL record: its checkpoint, taken when the game saves on arrival at a map point as
/// the simulator takes it, and every decision taken in the node since, as the simulator's trace names them ("surface
/// action"). The player's decisions are read where the game commits them, the entry points the
/// simulator calls itself, so a step is known exactly, before the game plays it.
/// The record is kept in both environments, so switching to SL works in the room the player is in; only SL sends it.
///
/// A step the simulator has no decision for (a potion thrown away in combat, a card reward closed unanswered) breaks the
/// record for the rest of the node, as does a run continued from a save made inside the room: the brain cannot clone
/// that node.
/// Everything here runs on the game's main thread.
/// </summary>
internal static class NodeRecord
{
    private static readonly AccessTools.FieldRef<VoteForMapCoordAction, MapVote?> Destination = AccessTools.FieldRefAccess<VoteForMapCoordAction, MapVote?>("_destination");
    private static readonly AccessTools.FieldRef<DiscardPotionGameAction, uint> DiscardedSlot = AccessTools.FieldRefAccess<DiscardPotionGameAction, uint>("_potionSlotIndex");

    private static readonly AccessTools.FieldRef<EventSynchronizer, List<Task>> EventOptionTasks = AccessTools.FieldRefAccess<EventSynchronizer, List<Task>>("_pendingOptionTasks");

    private static readonly List<string> _steps = [];

    /// <summary>The game's tasks for the steps the player took, by the surface they were taken on.</summary>
    private static readonly List<(string Surface, Task Task)> _running = [];
    private static RunState? _run;

    /// <summary>The decision last on screen, which the player's next answer is read against.</summary>
    private static Asking? _screen;

    /// <summary>The node's number in the run, from 0 at the first node the game entered while the mod ran; -1 before.</summary>
    public static int Node { get; private set; } = -1;

    public static string? Checkpoint { get; private set; }

    public static IReadOnlyList<string> Steps => _steps;

    /// <summary>The prompts the node has asked so far (card prompts, card rewards, bundles), as the simulator's
    /// selector counts them from the node's entry: the next prompt is this number, which seeds its ties' order.</summary>
    public static int Prompts => _steps.Count(s => s.StartsWith(Surfaces.CardChoice + " ", StringComparison.Ordinal)
                                                  || s.StartsWith(Surfaces.CardReward + " ", StringComparison.Ordinal)
                                                  || s.StartsWith(Surfaces.Bundle + " ", StringComparison.Ordinal));

    /// <summary>Why the node cannot be cloned, or null.</summary>
    public static string? Broken { get; private set; }

    public static void Patch(Harmony harmony)
    {
        Prefix(harmony, typeof(SaveManager), nameof(SaveManager.SaveRun), nameof(Saving));
        Prefix(harmony, typeof(RunManager), nameof(RunManager.LoadIntoLatestMapCoord), nameof(Continuing));
        Prefix(harmony, typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue), nameof(Queued));
        Prefix(harmony, typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice), nameof(Chosen));
        Prefix(harmony, typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption), nameof(EventOption));
        Prefix(harmony, typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption), nameof(RestOption));
        Prefix(harmony, typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward), nameof(RewardTaken));
        Prefix(harmony, typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SkipLocalRewardsSet), nameof(RewardsDone));
        Prefix(harmony, typeof(RunManager), nameof(RunManager.ProceedFromTerminalRewardsScreen), nameof(RewardsDone));
        Prefix(harmony, typeof(ActChangeSynchronizer), nameof(ActChangeSynchronizer.SetLocalPlayerReady), nameof(RewardsDone));
        Prefix(harmony, typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.PickRelicLocally), nameof(RelicPicked));
        Prefix(harmony, typeof(MerchantSynchronizer), nameof(MerchantSynchronizer.PurchaseLocalEntry), nameof(Bought));
        Prefix(harmony, typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked), nameof(Divined));
        Postfix(harmony, typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption), nameof(EventRunning));
        Postfix(harmony, typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption), nameof(RestRunning));
        Postfix(harmony, typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward), nameof(RewardRunning));
        Postfix(harmony, typeof(MerchantSynchronizer), nameof(MerchantSynchronizer.PurchaseLocalEntry), nameof(PurchaseRunning));
        Postfix(harmony, typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked), nameof(DivinationRunning));
    }

    private static void Prefix(Harmony harmony, Type type, string method, string prefix) =>
        harmony.Patch(AccessTools.DeclaredMethod(type, method), prefix: new HarmonyMethod(typeof(NodeRecord), prefix));

    private static void Postfix(Harmony harmony, Type type, string method, string postfix) =>
        harmony.Patch(AccessTools.DeclaredMethod(type, method), postfix: new HarmonyMethod(typeof(NodeRecord), postfix));

    /// <summary>
    /// Whether a step the player took on <paramref name="surface"/> still plays out (an event option, a rest option, a
    /// reward, a purchase, a divination): until it ends, the screen still shows the decision it answered. The simulator
    /// waits for the same tasks before its next decision.
    /// </summary>
    public static bool Running(string surface)
    {
        _running.RemoveAll(r => r.Task.IsCompleted);
        return _running.Any(r => r.Surface == surface);
    }

    private static void EventRunning(EventSynchronizer __instance)
    {
        foreach (var task in EventOptionTasks(__instance)) _running.Add((Surfaces.Event, task));
    }

    private static void RestRunning(Task __result) => _running.Add((Surfaces.Rest, __result));

    private static void RewardRunning(Task __result) => _running.Add((Surfaces.Rewards, __result));

    private static void PurchaseRunning(Task __result) => _running.Add((Surfaces.Shop, __result));

    private static void DivinationRunning(Task __result) => _running.Add((Surfaces.CrystalSphere, __result));

    /// <summary>
    /// The decision on screen, each frame one is read. Leaving an event, a shop or a rest site is a decision of its own
    /// in the simulator, where the game only opens the map: it is taken when the map asks for the travel.
    /// </summary>
    public static void Seen(Asking asking)
    {
        if (asking.Surface == Surfaces.Map) Leave();
        _screen = asking;
    }

    private static void Leave()
    {
        if (_screen is not { } room) return;
        MacroAction? leave = room.Surface switch
        {
            Surfaces.Event => MacroAction.Proceed,
            Surfaces.Shop or Surfaces.Rest => MacroAction.Leave,
            _ => null,
        };
        if (leave is { } action && room.Legal.Contains(action.ToString())) Add(room.Surface, action);
    }

    // ---- node entry ----

    /// <summary>The save on arrival at a map point (the one without a finished room), as the simulator takes it.</summary>
    private static void Saving(AbstractRoom? preFinishedRoom)
    {
        if (preFinishedRoom == null) Guard(() => Enter(arrived: true));
    }

    /// <summary>
    /// A continued run enters its saved point again without saving. Saved on arrival, it enters it as the arrival did;
    /// saved later in the room (after its fight, after an ancient), the room's start is gone.
    /// </summary>
    private static void Continuing(AbstractRoom? preFinishedRoom) => Guard(() => Enter(arrived: preFinishedRoom == null));

    private static void Enter(bool arrived)
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { Players.Count: 1 } run) return;
        if (!ReferenceEquals(run, _run)) (_run, Node) = (run, -1);
        Node++;
        _steps.Clear();
        _screen = null;
        (Checkpoint, Broken) = (null, arrived ? "no checkpoint was taken" : "the run was continued from a save made after this room began");
        if (arrived) (Checkpoint, Broken) = (GameBridge.Checkpoint.Take(LocalContext.GetMe(run)!), null);
    }

    // ---- the player's decisions ----

    /// <summary>Card plays, potions, the turn's end and the map's travel all go through the action queue.</summary>
    private static void Queued(GameAction action) => Guard(() =>
    {
        if (_run is not { } run || LocalContext.GetMe(run) is not { } me) return;
        switch (action)
        {
            case PlayCardAction play:
                // The action finds its card only once it runs; the card's combat id is set when it is queued.
                Add(Surfaces.Combat, CombatActions.PlayOf(play.NetCombatCard.ToCardModel(), play.TargetId).ToString());
                break;
            case UsePotionAction use when use.WasEnqueuedInCombat:
                Add(Surfaces.Combat, CombatActions.PotionOf(me, (int)use.PotionIndex, use.TargetId).ToString());
                break;
            case UsePotionAction use:
                Belt(MacroAction.Of(MacroAction.Potion, (int)use.PotionIndex));
                break;
            case DiscardPotionGameAction discard when discard.WasEnqueuedInCombat:
                Break("a potion thrown away in combat");
                break;
            case DiscardPotionGameAction discard:
                Belt(MacroAction.Of(MacroAction.DiscardPotion, (int)DiscardedSlot(discard)));
                break;
            case EndPlayerTurnAction:
                Add(Surfaces.Combat, CombatAction.EndTurn.ToString());
                break;
            case UndoEndPlayerTurnAction:
                Break("a turn end taken back");
                break;
            case VoteForMapCoordAction vote when Destination(vote) is { } to:
                if (_screen?.Surface != Surfaces.Map) Leave();
                var travel = MapActions.Of(to.coord);
                if (MapActions.Legal(run).Contains(travel)) Add(Surfaces.Map, travel);
                else Break($"a travel to {travel}, which the map does not offer");
                break;
        }
    });

    /// <summary>The potion belt outside combat, offered on the screen it is used on.</summary>
    private static void Belt(MacroAction action)
    {
        if (_screen is { } screen && screen.Surface != Surfaces.Combat && screen.Legal.Contains(action.ToString())) Add(screen.Surface, action);
        else Break($"{action} on a screen that does not offer it");
    }

    /// <summary>Card prompts, card rewards and bundles answer through the choice synchronizer.</summary>
    private static void Chosen(PlayerChoiceResult result) => Guard(() =>
    {
        if (Checkpoint == null || Broken != null) return;
        switch (_screen)
        {
            case { Surface: Surfaces.CardChoice, Source: IReadOnlyList<CardModel> options }:
            {
                // The game's own indexes into the prompt's options; a skipped single pick is -1.
                var picks = result.ChoiceType == PlayerChoiceType.Index
                    ? result.AsIndexes().Where(i => i >= 0).ToList()
                    : [.. result.AsCards(result.ChoiceType).Select(c => options.ToList().IndexOf(c))];
                if (picks.Any(i => i < 0 || i >= options.Count)) Break("a card picked outside the prompt's options");
                else Add(Surfaces.CardChoice, string.Join(",", picks));
                break;
            }
            case { Surface: Surfaces.CardReward } screen:
                // The screen's cards, then its alternatives (skip, reroll), as both the game and the legal actions list them.
                if (result.AsIndexOrNull() is int pick && pick >= 0 && pick < screen.Legal.Count) Add(Surfaces.CardReward, screen.Legal[pick]);
                else Break("a card reward closed unanswered");
                break;
            case { Surface: Surfaces.Bundle }:
                Add(Surfaces.Bundle, MacroAction.Of(MacroAction.Bundle, result.AsIndex()));
                break;
            default:
                Break($"a {result.ChoiceType} choice on {_screen?.Surface ?? "no screen"}");
                break;
        }
    });

    private static void EventOption(int index) => Add(Surfaces.Event, MacroAction.Of(MacroAction.Event, index));

    private static void RestOption(RestSiteSynchronizer __instance, int index) =>
        Guard(() => Add(Surfaces.Rest, RestActions.Of(__instance.GetLocalOptions()[index])));

    private static void RewardTaken(Reward reward)
    {
        if (_screen is { Surface: Surfaces.Rewards, Source: RewardsSet set } && set.Rewards.IndexOf(reward) is var i and >= 0) Add(Surfaces.Rewards, MacroAction.Of(MacroAction.Reward, i));
        else Break("a reward taken from no rewards screen");
    }

    /// <summary>The rewards screen's proceed button; the same calls leave a chest or a fight without rewards, which are no decisions.</summary>
    private static void RewardsDone()
    {
        if (_screen?.Surface != Surfaces.Rewards) return;
        Add(Surfaces.Rewards, MacroAction.RewardsDone);
        // Some screens call two of these.
        _screen = null;
    }

    private static void RelicPicked(int? index) => Add(Surfaces.Treasure, index is int i ? MacroAction.Of(MacroAction.Relic, i) : MacroAction.RelicSkip);

    /// <summary>A purchase the shop cannot make (too little gold, sold out) changes nothing. Only the player's own purchase
    /// comes this way: a relic buying for free goes past it.</summary>
    private static void Bought(MerchantEntry entry) => Guard(() =>
    {
        if (_run?.CurrentRoom is not MerchantRoom) return;
        var inventory = RunManager.Instance.MerchantSynchronizer.GetLocalInventory();
        var action = ShopActions.Of(inventory, entry);
        if (ShopActions.Legal(inventory).Contains(action)) Add(Surfaces.Shop, action);
    });

    /// <summary>A click that uncovers nothing changes nothing.</summary>
    private static void Divined(CrystalSphereMinigame __instance, CrystalSphereCell clickedCell) => Guard(() =>
    {
        var action = CrystalSphereActions.Of(__instance, clickedCell);
        if (CrystalSphereActions.Legal(__instance).Contains(action)) Add(Surfaces.CrystalSphere, action);
    });

    private static void Add(string surface, MacroAction action) => Add(surface, action.ToString());

    private static void Add(string surface, string action)
    {
        if (Checkpoint == null || Broken != null) return;
        _steps.Add($"{surface} {action}");
    }

    /// <summary>A hook runs inside the game's own call: whatever goes wrong in it must not reach the game.</summary>
    private static void Guard(Action hook)
    {
        try
        {
            hook();
        }
        catch (Exception e)
        {
            Log.Error($"{Host.Tag} SL record: {e}");
            Break($"{e.GetType().Name}: {e.Message}");
        }
    }

    private static void Break(string reason)
    {
        if (Checkpoint == null || Broken != null) return;
        Broken = reason;
        Log.Warn($"{Host.Tag} SL record of node {Node} broken after {_steps.Count} steps: {reason}");
    }
}
