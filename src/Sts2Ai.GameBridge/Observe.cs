using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>Builds the protocol's observation from the live game objects. See <see cref="CombatObservation"/>.</summary>
public static class Observe
{
    public static CombatObservation Combat(CombatState st, Player player)
    {
        var pcs = player.PlayerCombatState ?? throw new InvalidOperationException("player has no combat state");
        var me = player.Creature;
        Creature[] targets = [me];
        return new CombatObservation
        {
            Round = st.RoundNumber,
            Player = new PlayerView
            {
                Uid = me.CombatId,
                Character = player.Character.Id.Entry,
                Hp = me.CurrentHp,
                MaxHp = me.MaxHp,
                Block = me.Block,
                Energy = pcs.Energy,
                MaxEnergy = pcs.MaxEnergy,
                Stars = pcs.Stars,
                Gold = player.Gold,
                Powers = Powers(me),
                OrbSlots = pcs.OrbQueue.Capacity,
                Orbs = pcs.OrbQueue.Orbs.Select(o => new OrbView(o.Id.Entry, o.PassiveVal, o.EvokeVal)).ToList(),
            },
            Hand = pcs.Hand.Cards.Select(c => Card(c, withUid: true)).ToList(),
            Draw = pcs.DrawPile.Cards.Select(c => Card(c)).OrderBy(ProtocolJson.CardKey, StringComparer.Ordinal).ToList(),
            Discard = pcs.DiscardPile.Cards.Select(c => Card(c)).ToList(),
            Exhaust = pcs.ExhaustPile.Cards.Select(c => Card(c)).ToList(),
            Potions = player.PotionSlots.Select(p => p?.Id.Entry).ToList(),
            Relics = player.Relics.Select(Relic).ToList(),
            Allies = st.Allies.Where(c => c != me).Select(c => Creature(c, targets)).ToList(),
            Enemies = st.Enemies.Select(c => Creature(c, targets)).ToList(),
        };
    }

    /// <summary>A card as the player sees it; with its uid only if asked (cards in hand).</summary>
    public static CardView Card(CardModel card, bool withUid = false)
    {
        var x = card.EnergyCost.CostsX;
        return new CardView
        {
            Id = card.Id.Entry,
            Uid = withUid ? NetCombatCardDb.Instance.GetCardId(card) : null,
            Up = card.CurrentUpgradeLevel > 0 ? card.CurrentUpgradeLevel : null,
            Cost = x ? null : card.EnergyCost.GetWithModifiers(CostModifiers.All),
            XCost = x ? true : null,
            Stars = card.CanonicalStarCost >= 0 ? card.GetStarCostWithModifiers() : null,
            Enchant = card.Enchantment is { } e ? new ModifierView(e.Id.Entry, e.Amount) : null,
            Affliction = card.Affliction is { } a ? new ModifierView(a.Id.Entry, a.Amount) : null,
            Keywords = card.Keywords.Count > 0 ? card.Keywords.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToList() : null,
            Vars = card.DynamicVars.Count > 0 ? Vars(card) : null,
        };
    }

    /// <summary>See <see cref="ProtocolJson.CardKey"/>.</summary>
    public static string CardKey(CardModel card) => ProtocolJson.CardKey(Card(card));

    private static Dictionary<string, decimal> Vars(CardModel card)
    {
        // Filled in sorted order; the serializer writes a dictionary in insertion order.
        var vars = new Dictionary<string, decimal>(card.DynamicVars.Count);
        foreach (var (name, v) in card.DynamicVars.OrderBy(kv => kv.Key, StringComparer.Ordinal)) vars[name] = v.BaseValue;
        return vars;
    }

    private static CreatureView Creature(Creature c, Creature[] targets) => new()
    {
        Uid = c.CombatId,
        Id = c.Monster?.Id.Entry,
        Hp = c.CurrentHp,
        MaxHp = c.MaxHp,
        Block = c.Block,
        Powers = Powers(c),
        Intents = c.Monster is { } monster && c.IsAlive && c.Side != CombatSide.Player
            ? monster.NextMove.Intents.Select(i => Intent(i, c, targets)).ToList()
            : null,
    };

    private static IntentView Intent(AbstractIntent intent, Creature owner, Creature[] targets) => intent is AttackIntent attack
        ? new IntentView { Type = intent.IntentType.ToString(), Damage = attack.GetSingleDamage(targets, owner), Hits = Math.Max(1, attack.Repeats) }
        : new IntentView { Type = intent.IntentType.ToString() };

    private static List<PowerView> Powers(Creature c) =>
        c.Powers.Where(p => p.IsVisible).Select(p => new PowerView(p.Id.Entry, p.DisplayAmount)).ToList();

    private static RelicView Relic(RelicModel r) => new()
    {
        Id = r.Id.Entry,
        Counter = r.ShowCounter ? r.DisplayAmount : null,
        Status = r.Status != RelicStatus.Normal ? r.Status.ToString() : null,
        Melted = r.IsMelted ? true : null,
    };
}
