using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>What carrying out an action did.</summary>
internal enum Acted
{
    /// <summary>The game is doing it.</summary>
    Done,

    /// <summary>A step before it was taken (a dialogue clicked on, the shop opened), or its button is not ready: ask again.</summary>
    Again,
}

/// <summary>
/// Carries out the brain's answer in the real game, the way the player's click does, so the game's
/// screens follow: where GameBridge's start is the call the click makes (combat, the map, purchases, a chest's relic,
/// the crystal sphere, the belt), that; where the click does more (rewards, rest options, events, every way out of a
/// room), the button's own handler, pressed only when it is enabled and on screen; and a card prompt or a card reward
/// answered as its screen answers itself, through the task it awaits. The actions go through the game entry points the
/// SL record watches, so they are recorded as the player's would be (<see cref="NodeRecord"/>). Only legal actions come
/// here: the brain's answer to the decision on screen.
/// </summary>
internal static class Executor
{
    public static Acted Run(Asking asking, string text, RunState run, Player me)
    {
        if (asking.Surface == Surfaces.Combat)
        {
            CombatActions.Start(CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat"), me, CombatAction.Parse(text));
            return Acted.Done;
        }
        var action = MacroAction.Parse(text);
        // In combat a potion is a combat action, with its target; elsewhere it is the belt's.
        if (PotionActions.Handles(action))
        {
            PotionActions.Start(me, action);
            return Acted.Done;
        }
        var top = ActiveScreenContext.Instance.GetCurrentScreen();
        switch (asking.Surface)
        {
            case Surfaces.CardChoice:
                return Pick(top, asking, action);
            case Surfaces.CardReward when top is NCardRewardSelectionScreen screen:
            {
                var options = Field<IReadOnlyList<CardCreationResult>>(screen, "_options");
                var alternatives = Field<IReadOnlyList<CardRewardAlternative>>(screen, "_extraOptions");
                int index = action.Verb switch
                {
                    MacroAction.Card => action.Index,
                    MacroAction.CardAlt => options.Count + alternatives.ToList().FindIndex(a => a.OptionId == action.Arg),
                    _ => throw Unknown(asking, action),
                };
                Field<TaskCompletionSource<int?>?>(screen, "_completionSource")?.TrySetResult(index);
                return Acted.Done;
            }
            case Surfaces.Bundle when top is NChooseABundleSelectionScreen screen && action.Verb == MacroAction.Bundle:
            {
                var bundles = Field<IReadOnlyList<IReadOnlyList<CardModel>>>(screen, "_bundles");
                Field<TaskCompletionSource<IEnumerable<IReadOnlyList<CardModel>>>>(screen, "_completionSource").TrySetResult([bundles[action.Index]]);
                return Acted.Done;
            }
            case Surfaces.Rewards when top is NRewardsScreen screen:
            {
                if (action == MacroAction.RewardsDone) return Click(Field<NProceedButton>(screen, "_proceedButton"));
                if (action.Verb != MacroAction.Reward || asking.Source is not RewardsSet set) throw Unknown(asking, action);
                var reward = set.Rewards[action.Index];
                var button = Field<List<Godot.Control>>(screen, "_rewardButtons").OfType<NRewardButton>().FirstOrDefault(b => b.Reward == reward);
                return button != null ? Click(button) : Acted.Again;
            }
            case Surfaces.Map when top is NMapScreen screen && action.Verb == MacroAction.Map:
            {
                var at = action.Coord;
                if (!Field<Dictionary<MapCoord, NMapPoint>>(screen, "_mapPointDictionary").TryGetValue(new MapCoord(at.Col, at.Row), out var point) || !point.IsEnabled)
                    return Acted.Again;
                screen.OnMapPointSelectedLocally(point);
                return Acted.Done;
            }
            case Surfaces.Event:
                return Event(action);
            case Surfaces.Shop when NMerchantRoom.Instance is { } room && run.CurrentRoom is MerchantRoom:
                if (action == MacroAction.Leave)
                    return room.Inventory.IsOpen ? Again(Click(Field<NBackButton>(room.Inventory, "_backButton"))) : Click(room.ProceedButton);
                // The purchase does not need the shop open; opened, the player sees it. Checked here: the start is a task.
                var inventory = RunManager.Instance.MerchantSynchronizer.GetLocalInventory();
                if (!ShopActions.Legal(inventory).Contains(action)) throw Unknown(asking, action);
                if (!room.Inventory.IsOpen) room.OpenInventory();
                _ = ShopActions.Start(inventory, action);
                return Acted.Done;
            case Surfaces.Rest when NRestSiteRoom.Instance is { } room:
            {
                if (action == MacroAction.Leave) return Click(room.ProceedButton);
                var option = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions().FirstOrDefault(o => RestActions.Of(o) == action)
                    ?? throw Unknown(asking, action);
                return room.GetButtonForOption(option) is { } button ? Click(button) : Acted.Again;
            }
            case Surfaces.Treasure when top is NTreasureRoom room:
                if (action == MacroAction.RelicSkip) return Click(room.ProceedButton);
                TreasureActions.Start(RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics ?? throw Unknown(asking, action), action);
                return Acted.Done;
            case Surfaces.CrystalSphere when top is NCrystalSphereScreen sphere:
                _ = CrystalSphereActions.Start(Field<CrystalSphereMinigame>(sphere, "_entity"), action);
                return Acted.Done;
            default:
                return Acted.Again;
        }
    }

    /// <summary>
    /// What auto-play presses when the game waits on the player without a decision: the chest, the proceed button after
    /// a chest's relic, a fight with no rewards or the crystal sphere, an ancient's dialogue. Null when there is nothing
    /// to press here.
    /// </summary>
    public static string? Nudge()
    {
        switch (ActiveScreenContext.Instance.GetCurrentScreen())
        {
            case NTreasureRoom room:
                // The chest's button is disabled once it is clicked (the game has had no _hasChestBeenOpened since v0.113.0).
                if (Field<NClickableControl>(room, "_chestButton") is { IsEnabled: true } chest) return Click(chest) == Acted.Done ? "chest" : null;
                return Click(room.ProceedButton) == Acted.Done ? "proceed" : null;
            case NCombatRoom room when !CombatManager.Instance.IsInProgress:
                return Click(room.ProceedButton) == Acted.Done ? "proceed" : null;
            case NEventRoom when Dialogue() is { } hitbox:
                return Click(hitbox) == Acted.Done ? "dialogue" : null;
            // The last divination leaves only the way out, after the sphere's rewards.
            case NCrystalSphereScreen sphere when Field<CrystalSphereMinigame>(sphere, "_entity").IsFinished:
                return Click(Field<NProceedButton>(sphere, "_proceedButton")) == Acted.Done ? "proceed" : null;
            default:
                return null;
        }
    }

    /// <summary>A card prompt's picks, canonical indexes, answered as its screen answers itself: the cards to the task it awaits.</summary>
    private static Acted Pick(object? top, Asking asking, MacroAction action)
    {
        if (action.Verb != MacroAction.PickVerb || asking.Source is not IReadOnlyList<CardModel> options || asking.GameIndex is not { } index)
            throw Unknown(asking, action);
        var cards = action.Picks.Select(i => options[index[i]]).ToList();
        switch (top)
        {
            case NCombatRoom when NPlayerHand.Instance is { IsInCardSelection: true } hand:
                // The hand's confirm button does this; then the hand leaves its selection mode by itself.
                Field<TaskCompletionSource<IEnumerable<CardModel>>>(hand, "_selectionCompletionSource").TrySetResult(cards);
                return Acted.Done;
            case NCardGridSelectionScreen grid:
                // Each grid screen's confirm: the picks, then the screen leaves the overlays.
                if (Field<TaskCompletionSource<IEnumerable<CardModel>>>(grid, "_completionSource").TrySetResult(cards)) NOverlayStack.Instance?.Remove(grid);
                return Acted.Done;
            case NChooseACardSelectionScreen choose:
                // The screen removes itself once its task has the pick (or nothing, for skip).
                Field<TaskCompletionSource<IEnumerable<CardModel>>>(choose, "_completionSource").TrySetResult(cards);
                return Acted.Done;
            default:
                return Acted.Again;
        }
    }

    /// <summary>
    /// An event's option through the room's handler, which also clears the old buttons; "proceed" through the proceed
    /// option when the event shows one, else as the room's proceed, which opens the map. An ancient's dialogue is
    /// clicked through first.
    /// </summary>
    private static Acted Event(MacroAction action)
    {
        var ev = RunManager.Instance.EventSynchronizer.GetLocalEvent();
        if (NEventRoom.Instance is not { } room) return Acted.Again;
        if (Dialogue() is { } hitbox) return Again(Click(hitbox));
        var options = ev.CurrentOptions;
        if (action == MacroAction.Proceed)
        {
            int at = options.ToList().FindIndex(o => o.IsProceed && o.TextKey == "PROCEED");
            if (at >= 0) room.OptionButtonClicked(options[at], at);
            else _ = NEventRoom.Proceed();
            return Acted.Done;
        }
        if (action.Verb != MacroAction.Event) return Acted.Again;
        int i = action.Index;
        room.OptionButtonClicked(options[i], i);
        return Acted.Done;
    }

    /// <summary>An ancient's dialogue hitbox while it waits for a click, or null.</summary>
    private static NClickableControl? Dialogue() =>
        NEventRoom.Instance?.Layout is NAncientEventLayout layout && Field<NClickableControl?>(layout, "_dialogueHitbox") is { IsEnabled: true } hitbox
            && hitbox.IsVisibleInTree() ? hitbox : null;

    /// <summary>Presses a button as a click does, if it is enabled and on screen; otherwise it is not ready yet.</summary>
    private static Acted Click(NClickableControl button)
    {
        if (!button.IsEnabled || !button.IsVisibleInTree()) return Acted.Again;
        button.ForceClick();
        return Acted.Done;
    }

    /// <summary>A step taken before the action: the action itself is still to come.</summary>
    private static Acted Again(Acted _) => Acted.Again;

    private static InvalidOperationException Unknown(Asking asking, MacroAction action) => new($"cannot carry out {action} on {asking.Surface}");

    /// <summary>A private field of the game's node, by name. A field the game no longer has is an error that names it:
    /// as a null dereference it ended the game with SIGSEGV (v0.113.1's treasure room, 2026-11-15).</summary>
    private static T Field<T>(object node, string name) =>
        (T)(AccessTools.Field(node.GetType(), name) ?? throw new MissingFieldException(node.GetType().FullName, name)).GetValue(node)!;
}
