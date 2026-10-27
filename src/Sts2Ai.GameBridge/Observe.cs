using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>
/// Builds the protocol's observations from the live game objects: the run (<see cref="RunView"/>) and the combat
/// (<see cref="CombatObservation"/>) here, each other screen next to its legal actions (<c>MapActions.Screen</c>, ...).
/// </summary>
public static class Observe
{
    public static Observation Of(RunState run, Player player, ScreenView screen) => new() { Run = Run(run, player), Screen = screen };

    public static RunView Run(RunState run, Player player) => new()
    {
        Character = player.Character.Id.Entry,
        Ascension = run.AscensionLevel,
        Act = run.CurrentActIndex,
        ActId = run.Act.Id.Entry,
        Floor = run.TotalFloor,
        Hp = player.Creature.CurrentHp,
        MaxHp = player.Creature.MaxHp,
        Gold = player.Gold,
        Deck = player.Deck.Cards.Select(DeckCard).OrderBy(ProtocolJson.CardKey, StringComparer.Ordinal).ToList(),
        Relics = player.Relics.Select(Relic).ToList(),
        Potions = player.PotionSlots.Select(p => p?.Id.Entry).ToList(),
        Map = Map(run),
    };

    /// <summary>
    /// The map as its screen draws it. The boss, second boss and start points are not in the map's grid, and a
    /// point's children are a hash set, so everything is put in (row, col) order.
    /// </summary>
    private static MapView Map(RunState run)
    {
        var map = run.Map;
        var points = map.GetAllMapPoints().Append(map.StartingMapPoint).Append(map.BossMapPoint);
        if (map.SecondBossMapPoint is { } second) points = points.Append(second);
        return new MapView
        {
            Points = points.Distinct().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).Select(p => new MapPointView
            {
                Col = p.coord.col,
                Row = p.coord.row,
                Type = p.PointType.ToString(),
                Next = p.Children.Select(c => c.coord).OrderBy(c => c.row).ThenBy(c => c.col).Select(Coord).ToList(),
                Quest = p.Quests.Count > 0 ? true : null,
            }).ToList(),
            Visited = run.VisitedMapCoords.Select(Coord).ToList(),
            Boss = run.Act.BossEncounter.Id.Entry,
            SecondBoss = map.SecondBossMapPoint != null ? run.Act.SecondBossEncounter?.Id.Entry : null,
            Ancient = run.Act.Ancient?.Id.Entry,
        };
    }

    public static Coord Coord(MapCoord c) => new(c.col, c.row);

    public static CombatObservation Combat(CombatState st, Player player)
    {
        var pcs = player.PlayerCombatState ?? throw new InvalidOperationException("player has no combat state");
        var me = player.Creature;
        Creature[] targets = [me];
        return new CombatObservation
        {
            Encounter = st.Encounter?.Id.Entry,
            Room = st.Encounter?.RoomType.ToString(),
            Act = st.RunState.CurrentActIndex,
            Ascension = st.RunState.AscensionLevel,
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
            Tinker = card is MadScience mad ? new TinkerView(mad.TinkerTimeType.ToString(), mad.TinkerTimeRider.ToString()) : null,
            Keywords = card.Keywords.Count > 0 ? card.Keywords.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToList() : null,
            Vars = Vars(card) is { Count: > 0 } vars ? vars : null,
        };
    }

    /// <summary>A card of the deck: <see cref="Card"/> with the upgrade preview the deck view shows, if it can be upgraded.</summary>
    public static CardView DeckCard(CardModel card) => Card(card) with { Upgraded = card.IsUpgradable ? Card(UpgradePreview(card)) : null };

    /// <summary>
    /// The card once more upgraded, made as the game's upgrade preview makes it, but on a clone outside the run's
    /// cards: a mutable clone has none of the original's event subscribers, and the upgrade runs no hooks.
    /// </summary>
    private static CardModel UpgradePreview(CardModel card)
    {
        var preview = (CardModel)card.MutableClone();
        preview.UpgradeInternal();
        preview.FinalizeUpgradeInternal();
        return preview;
    }

    /// <summary>See <see cref="ProtocolJson.CardKey"/>.</summary>
    public static string CardKey(CardModel card) => ProtocolJson.CardKey(Card(card));

    /// <summary>
    /// The card's numbers, but for calculated ones: the card shows those as their base number plus their extra number
    /// times a count from the state, and both parts are listed. A calculated number's own value only mirrors its base
    /// number, and a downgrade leaves it stale.
    /// </summary>
    private static Dictionary<string, decimal> Vars(CardModel card)
    {
        // Filled in sorted order; the serializer writes a dictionary in insertion order.
        var vars = new Dictionary<string, decimal>(card.DynamicVars.Count);
        foreach (var (name, v) in card.DynamicVars.Where(kv => kv.Value is not CalculatedVar).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            vars[name] = v.BaseValue;
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
        Intents = c.Monster is { } monster && c.IsAlive && c.Side != CombatSide.Player ? Intents(monster.NextMove, c, targets) : null,
    };

    /// <summary>The intents <paramref name="move"/> would show above <paramref name="owner"/>, aimed at <paramref name="targets"/>.</summary>
    public static List<IntentView> Intents(MoveState move, Creature owner, Creature[] targets) =>
        move.Intents.Select(i => Intent(i, owner, targets)).ToList();

    private static IntentView Intent(AbstractIntent intent, Creature owner, Creature[] targets) => intent is AttackIntent attack
        ? new IntentView { Type = intent.IntentType.ToString(), Damage = attack.GetSingleDamage(targets, owner), Hits = Math.Max(1, attack.Repeats) }
        : new IntentView { Type = intent.IntentType.ToString() };

    private static List<PowerView> Powers(Creature c) =>
        c.Powers.Where(p => p.IsVisible).Select(p => new PowerView(p.Id.Entry, p.DisplayAmount, p.DisplayAmount != p.Amount ? p.Amount : null,
            HeldCards.Get(p) is { } card ? Card(card) : null)).ToList();

    public static RelicView Relic(RelicModel r) => new()
    {
        Id = r.Id.Entry,
        Counter = r.ShowCounter ? r.DisplayAmount : null,
        Status = r.Status != RelicStatus.Normal ? r.Status.ToString() : null,
        Melted = r.IsMelted ? true : null,
        UsedUp = r.IsUsedUp ? true : null,
        Cards = r is PaelsTooth { SerializableCards.Count: > 0 } tooth ? tooth.SerializableCards.Select(c => c.Id!.Entry).ToList() : null,
    };
}
