using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;
using ChoiceContext = Sts2Ai.Protocol.ChoiceContext;

namespace Sts2Ai.Mod;

/// <summary>A decision to ask the brain about: the request's surface, observation and legal actions.</summary>
/// <param name="Source">What the player's answer is read against (<see cref="NodeRecord"/>): a card prompt's options in
/// the game's own order, a rewards screen's set.</param>
/// <param name="GameIndex">A card prompt's: for each canonical index, the option's index in <paramref name="Source"/>,
/// which is what the AI's answer picks (<see cref="Executor"/>).</param>
internal sealed record Asking(string Surface, Observation Obs, IReadOnlyList<string> Legal, object? Source = null, int[]? GameIndex = null);

/// <summary>
/// The decision the screen on top asks for, asked as the simulator asks it (RunSession): the same
/// surfaces, observations and legal actions, with the potion belt offered on the same screens. The game's UI keeps
/// what each screen offers in private fields, read here as the screen holds them.
/// </summary>
internal sealed class Screens
{
    // The card prompt on screen (its node, or the hand's selection), and the context it was asked with: null when the
    // game opened it without one, which leaves it to the player.
    private object? _prompt;
    private ChoiceContext? _context;
    private ChoiceContext? _bound;

    /// <summary>
    /// The decision on screen once nothing is moving, or null; then <paramref name="note"/> says what the player has
    /// to do first, when the game is waiting on something other than a decision.
    /// </summary>
    public Asking? Read(RunState run, Player me, out string note)
    {
        note = "";
        var top = ActiveScreenContext.Instance.GetCurrentScreen();
        // Prompts wait inside an action or a reward; every other screen waits with the action queue empty.
        bool settled = RunManager.Instance.ActionQueueSet.IsEmpty;
        switch (top)
        {
            case NCombatRoom when NPlayerHand.Instance is { IsInCardSelection: true } hand:
                return Prompt(run, me, Field<object>(hand, "_selectionCompletionSource"), Field<CardSelectorPrefs>(hand, "_prefs"),
                    [.. PileType.Hand.GetPile(me).Cards.Where(Field<Func<CardModel, bool>?>(hand, "_currentSelectionFilter") ?? (_ => true))]);
            case NCombatRoom:
                return Combat(run, me);
            case NCombatPileCardSelectScreen pile:
                return Prompt(run, me, pile, Field<CardSelectorPrefs>(pile, "_prefs"),
                    [.. Field<CardPile>(pile, "_pile").Cards.Where(Field<Func<CardModel, bool>?>(pile, "_filter") ?? (_ => true))]);
            case NCardGridSelectionScreen grid:
                return Prompt(run, me, grid, Field<CardSelectorPrefs>(grid, "_prefs"), Field<IReadOnlyList<CardModel>>(grid, "_cards"));
            case NChooseACardSelectionScreen choose:
                // The selector is asked for 0..1 cards whether or not the screen can be skipped (CardSelectCmd).
                return Prompt(run, me, choose, 0, 1, Field<IReadOnlyList<CardModel>>(choose, "_cards"));
            case NCardRewardSelectionScreen reward:
            {
                var cards = Field<IReadOnlyList<CardCreationResult>>(reward, "_options");
                // A used reroll comes back as a new list without it, so the screen's list is what is left.
                var alternatives = Field<IReadOnlyList<CardRewardAlternative>>(reward, "_extraOptions");
                return Ask(run, me, Surfaces.CardReward, RewardActions.CardRewardScreen(cards, alternatives),
                    RewardActions.CardRewardLegal(cards, alternatives, new HashSet<CardRewardAlternative>()), potions: false);
            }
            case NChooseABundleSelectionScreen bundle:
            {
                var bundles = Field<IReadOnlyList<IReadOnlyList<CardModel>>>(bundle, "_bundles");
                return Ask(run, me, Surfaces.Bundle, BundleActions.Screen(bundles), BundleActions.Legal(bundles.Count), potions: false);
            }
            case NRewardsScreen rewards when settled && Field<RewardsSet?>(rewards, "_rewardsSet") is { } set:
                return Ask(run, me, Surfaces.Rewards, RewardActions.Screen(set), RewardActions.Legal(set), source: set);
            case NCrystalSphereScreen sphere when settled && Field<CrystalSphereMinigame>(sphere, "_entity") is { IsFinished: false } game:
                return Ask(run, me, Surfaces.CrystalSphere, CrystalSphereActions.Screen(game), CrystalSphereActions.Legal(game));
            case NMapScreen { IsTravelEnabled: true, IsTraveling: false } when settled:
                return Ask(run, me, Surfaces.Map, MapActions.Screen(run), MapActions.Legal(run));
            case NEventRoom or NFakeMerchant or NMerchantInventory when settled && run.CurrentRoom is EventRoom:
            {
                var ev = RunManager.Instance.EventSynchronizer.GetLocalEvent();
                return Ask(run, me, Surfaces.Event, EventActions.Screen(ev, me), EventActions.Legal(ev));
            }
            case NMerchantRoom or NMerchantInventory when settled && run.CurrentRoom is MerchantRoom room:
            {
                var inventory = room.GetLocalInventory();
                return Ask(run, me, Surfaces.Shop, ShopActions.Screen(inventory), ShopActions.Legal(inventory));
            }
            case NRestSiteRoom rest when settled:
            {
                var options = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions();
                return Ask(run, me, Surfaces.Rest, RestActions.Screen(options, me), RestActions.Legal(options, rest.ProceedButton.IsEnabled));
            }
            case NTreasureRoom treasure when settled:
                if (Field<bool>(treasure, "_isRelicCollectionOpen") && RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is { Count: > 0 } relics)
                    return Ask(run, me, Surfaces.Treasure, TreasureActions.Screen(relics), TreasureActions.Legal(relics));
                if (!Field<bool>(treasure, "_hasChestBeenOpened")) note = "open the chest";
                return null;
            default:
                return null;
        }
    }

    /// <summary>The combat, when the player can act and nothing is still moving.</summary>
    private static Asking? Combat(RunState run, Player me)
    {
        var st = CombatManager.Instance.DebugOnlyGetState();
        if (st == null || !CombatActions.WaitingForInput(me) || !RunManager.Instance.ActionQueueSet.IsEmpty
            || NPlayerHand.Instance is not { CurrentMode: NPlayerHand.Mode.Play, InCardPlay: false })
            return null;
        var legal = CombatActions.Legal(st, me).Select(a => a.ToString()).ToList();
        return new Asking(Surfaces.Combat, Observe.Of(run, me, new CombatScreen { Combat = Observe.Combat(st, me) }), legal);
    }

    /// <summary>
    /// A card prompt, its options and limits as the selector would get them (CardSelectCmd). The answer picks
    /// canonical indexes, so the brain's choice never depends on the screen's order; ties are broken the same way on
    /// every frame, so the observation holds still.
    /// </summary>
    private Asking? Prompt(RunState run, Player me, object prompt, CardSelectorPrefs prefs, IReadOnlyList<CardModel> cards) =>
        Prompt(run, me, prompt, prefs.MinSelect, prefs.MaxSelect, cards);

    private Asking? Prompt(RunState run, Player me, object prompt, int minSelect, int maxSelect, IReadOnlyList<CardModel> cards)
    {
        if (ContextOf(prompt) is not { } context || cards.Count == 0) return null;
        var (min, max) = ChoiceContexts.Bounds(context, minSelect, maxSelect, cards.Count);
        var (choice, index) = CardChoices.Canonical(cards, min, max, context, new Random(0));
        var st = CombatManager.Instance.IsInProgress ? CombatManager.Instance.DebugOnlyGetState() : null;
        var screen = new CardChoiceScreen { Choice = choice, Combat = st != null ? Observe.Combat(st, me) : null };
        return new Asking(Surfaces.CardChoice, Observe.Of(run, me, screen), [], cards, index);
    }

    /// <summary>
    /// The context of the prompt on screen. The game opens the screen after its entry has returned, so it is the
    /// latest one recorded; one that an earlier prompt already used means this prompt came without one.
    /// </summary>
    private ChoiceContext? ContextOf(object prompt)
    {
        if (ReferenceEquals(prompt, _prompt)) return _context;
        var last = ChoiceContexts.Last;
        _prompt = prompt;
        _context = last != null && !ReferenceEquals(last, _bound) ? last : null;
        if (_context != null) _bound = _context;
        else Log.Warn($"{Host.Tag} a card prompt ({prompt.GetType().Name}) without its context, left to the player");
        return _context;
    }

    private static Asking? Ask(RunState run, Player me, string surface, ScreenView screen, List<MacroAction> legal, bool potions = true, object? source = null)
    {
        // A screen with nothing to pick is waiting on something else (an animation, a proceed button).
        if (legal.Count == 0) return null;
        if (potions) legal.AddRange(PotionActions.Legal(me));
        return new Asking(surface, Observe.Of(run, me, screen), [.. legal.Select(a => a.ToString())], source);
    }

    private static T Field<T>(object node, string name) => (T)AccessTools.Field(node.GetType(), name).GetValue(node)!;
}
