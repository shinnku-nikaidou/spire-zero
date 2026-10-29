using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>The legal actions of a combat in protocol terms, and starting one in the game.</summary>
public static class CombatActions
{
    /// <summary>True when the player can act: their turn, play phase, nothing resolving, input not locked.</summary>
    public static bool WaitingForInput(Player player)
    {
        var st = CombatManager.Instance.DebugOnlyGetState();
        var pcs = player.PlayerCombatState;
        return st != null && pcs != null && CombatManager.Instance.IsInProgress
            && st.CurrentSide == CombatSide.Player && pcs.Phase == PlayerTurnPhase.Play
            && !RunManager.Instance.ActionExecutor.IsRunning && !CombatManager.Instance.PlayerActionsDisabled;
    }

    /// <summary>Every legal action, cards in hand order first, then potions by slot, then ending the turn.</summary>
    public static List<CombatAction> Legal(CombatState st, Player player)
    {
        var actions = new List<CombatAction>();
        var pcs = PlayerCombat(player);
        foreach (var card in pcs.Hand.Cards)
        {
            // CanPlayTargeting(t) is IsValidTarget(t) && CanPlay(), and CanPlay runs four hook dispatches, so
            // check it once per card rather than once per target.
            if (!card.CanPlay()) continue;
            uint uid = NetCombatCardDb.Instance.GetCardId(card);
            switch (card.TargetType)
            {
                case TargetType.AnyEnemy:
                    foreach (var e in st.Enemies)
                        if (card.IsValidTarget(e)) actions.Add(CombatAction.Play(uid, Uid(e)));
                    break;
                case TargetType.AnyAlly:
                    foreach (var a in st.Allies)
                        if (card.IsValidTarget(a)) actions.Add(CombatAction.Play(uid, Uid(a)));
                    break;
                default:
                    if (card.IsValidTarget(null)) actions.Add(CombatAction.Play(uid));
                    break;
            }
        }

        if (player.CanUseOrRemovePotions)
        {
            for (int s = 0; s < player.PotionSlots.Count; s++)
            {
                var potion = player.PotionSlots[s];
                if (potion == null || potion.IsQueued || !potion.PassesCustomUsabilityCheck) continue;
                if (potion.Usage is not (PotionUsage.CombatOnly or PotionUsage.AnyTime)) continue;
                switch (potion.TargetType)
                {
                    case TargetType.AnyEnemy:
                        foreach (var e in st.Enemies)
                            if (e.IsAlive && potion.IsValidTarget(e)) actions.Add(CombatAction.Potion(s, Uid(e)));
                        break;
                    case TargetType.AnyAlly:
                        foreach (var a in st.Allies)
                            if (potion.IsValidTarget(a)) actions.Add(CombatAction.Potion(s, Uid(a)));
                        break;
                    default:
                        if (potion.IsValidTarget(null) || potion.IsValidTarget(player.Creature)) actions.Add(CombatAction.Potion(s));
                        break;
                }
            }
        }

        actions.Add(CombatAction.EndTurn);
        return actions;
    }

    /// <summary>
    /// Starts <paramref name="action"/> through the same entry points as the UI (dragging a card, clicking a potion,
    /// the end-turn button). It runs once the game's action queue gets to it; the caller waits for that.
    /// </summary>
    public static void Start(CombatState st, Player player, CombatAction action)
    {
        var pcs = PlayerCombat(player);
        switch (action.Kind)
        {
            case CombatActionKind.PlayCard:
            {
                var card = HandCard(pcs, action.Card ?? throw new ArgumentException($"{action} names no card"));
                // TryManualPlay validates targeting, then enqueues PlayCardAction.
                if (!card.TryManualPlay(Target(st, action)))
                    throw new InvalidOperationException($"illegal play {action} ({card.Id.Entry})");
                break;
            }
            case CombatActionKind.UsePotion:
            {
                int slot = action.PotionSlot ?? throw new ArgumentException($"{action} names no slot");
                var potion = slot >= 0 && slot < player.PotionSlots.Count ? player.PotionSlots[slot] : null;
                (potion ?? throw new InvalidOperationException($"no potion in slot {slot}")).EnqueueManualUse(Target(st, action));
                break;
            }
            case CombatActionKind.EndTurn:
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, pcs.TurnNumber));
                break;
        }
    }

    /// <summary>
    /// A play of <paramref name="card"/> as <see cref="Legal"/> names it: with its target only when the card picks one.
    /// The UI can pass a target for other cards too (the player for a self card).
    /// </summary>
    public static CombatAction PlayOf(CardModel card, uint? target) =>
        CombatAction.Play(NetCombatCardDb.Instance.GetCardId(card), PicksTarget(card.TargetType) ? target : null);

    /// <summary>A use of the potion in <paramref name="slot"/> as <see cref="Legal"/> names it (see <see cref="PlayOf"/>).</summary>
    public static CombatAction PotionOf(Player player, int slot, uint? target) =>
        CombatAction.Potion(slot, player.PotionSlots[slot] is { } potion && PicksTarget(potion.TargetType) ? target : null);

    private static bool PicksTarget(TargetType type) => type is TargetType.AnyEnemy or TargetType.AnyAlly;

    /// <summary>The card in hand with this uid.</summary>
    public static CardModel HandCard(PlayerCombatState pcs, uint uid) =>
        NetCombatCardDb.Instance.TryGetCard(uid, out var card) && card != null && pcs.Hand.Cards.Contains(card)
            ? card
            : throw new InvalidOperationException($"no card {uid} in hand");

    /// <summary>The creature with this uid.</summary>
    public static Creature Creature(CombatState st, uint uid) =>
        st.Enemies.FirstOrDefault(c => c.CombatId == uid) ?? st.Allies.FirstOrDefault(c => c.CombatId == uid)
        ?? throw new InvalidOperationException($"no creature {uid}");

    private static Creature? Target(CombatState st, CombatAction action) => action.Target is uint t ? Creature(st, t) : null;

    private static uint Uid(Creature c) => c.CombatId ?? throw new InvalidOperationException($"{c} has no combat id");

    private static PlayerCombatState PlayerCombat(Player player) =>
        player.PlayerCombatState ?? throw new InvalidOperationException("player has no combat state");
}
