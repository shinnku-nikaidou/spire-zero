using System.Collections.Concurrent;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>
/// Builds the protocol's observations from the live game objects: the run (<see cref="RunView"/>) and the combat
/// (<see cref="CombatObservation"/>) here, each other screen next to its legal actions (<c>MapActions.Screen</c>, ...).
/// </summary>
public static class Observe
{
    public static Observation Of(RunState run, Player player, ScreenView screen) => new() { Run = Run(run, player), Screen = screen };

    /// <param name="previews">Whether each card carries its upgrade preview (<see cref="CardView.Upgraded"/>), which
    /// costs a copy of the card: what a decision shows, not what V reads (a combat leaf's run).</param>
    public static RunView Run(RunState run, Player player, bool previews = true) => new()
    {
        Character = player.Character.Id.Entry,
        Ascension = run.AscensionLevel,
        Act = run.CurrentActIndex,
        ActId = run.Act.Id.Entry,
        Floor = run.TotalFloor,
        Hp = player.Creature.CurrentHp,
        MaxHp = player.Creature.MaxHp,
        Gold = player.Gold,
        Deck = [.. player.Deck.Cards.Select(c => previews ? DeckCard(c) : Card(c)).OrderBy(ProtocolJson.CardKey, StringComparer.Ordinal)],
        Relics = [.. player.Relics.Select(Relic)],
        Potions = [.. player.PotionSlots.Select(p => p?.Id.Entry)],
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
            Points = [.. points.Distinct().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).Select(p => new MapPointView
            {
                Col = p.coord.col,
                Row = p.coord.row,
                Type = p.PointType.ToString(),
                Next = [.. p.Children.Select(c => c.coord).OrderBy(c => c.row).ThenBy(c => c.col).Select(Coord)],
                Quest = p.Quests.Count > 0 ? true : null,
            })],
            Visited = [.. run.VisitedMapCoords.Select(Coord)],
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
        var arrows = pcs.Arrows;
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
                Orbs = [.. pcs.OrbQueue.Orbs.Select(o => new OrbView(o.Id.Entry, o.PassiveVal, o.EvokeVal))],
                Arrows = arrows.Count > 0 ? arrows.Count(a => a.Status == ArrowStatus.Retrieved) : null,
                ArrowSlots = arrows.Count > 0 ? arrows.Count(a => a.Status != ArrowStatus.Destroyed) : null,
                Melodies = Melodies(st, player),
            },
            Hand = [.. pcs.Hand.Cards.Select(c => Card(c, withUid: true))],
            Draw = [.. pcs.DrawPile.Cards.Select(c => Card(c)).OrderBy(ProtocolJson.CardKey, StringComparer.Ordinal)],
            Discard = [.. pcs.DiscardPile.Cards.Select(c => Card(c))],
            Exhaust = [.. pcs.ExhaustPile.Cards.Select(c => Card(c))],
            Potions = [.. player.PotionSlots.Select(p => p?.Id.Entry)],
            Relics = [.. player.Relics.Select(Relic)],
            Allies = [.. st.Allies.Where(c => c != me).Select(c => Creature(c, targets))],
            Enemies = [.. st.Enemies.Select(c => Creature(c, targets) with { Arrows = Stuck(c, me) })],
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
            Enchant = card.Enchantment is { } e ? new ModifierView(e.Id.Entry, e.Amount, e is Momentum && (int)e.EnchantDamageAdditive(0m, ValueProp.Move) is > 0 and var extra ? extra : null) : null,
            Affliction = card.Affliction is { } a ? new ModifierView(a.Id.Entry, a.Amount) : null,
            Tinker = card is MadScience mad ? new TinkerView(mad.TinkerTimeType.ToString(), mad.TinkerTimeRider.ToString()) : null,
            Keywords = card.Keywords.Count > 0 ? card.Keywords.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToList() : null,
            ThisTurn = ThisTurn(card),
            Vars = Vars(card) is { Count: > 0 } vars ? vars : null,
            Lasting = x ? null : Lasting(card),
            StarsLasting = card.CanonicalStarCost >= 0 && !card.HasStarCostX ? StarsLasting(card) : null,
            Replay = card.GetEnchantedReplayCount() is > 0 and var replay ? replay : null,
            Spent = card.Enchantment?.Status == EnchantmentStatus.Disabled ? true : null,
            Calc = card.CombatState != null ? card.DynamicVars.Values.OfType<CalculatedVar>().FirstOrDefault()?.Calculate(null) : null,
            Grown = Grown(card),
        };
    }

    private static readonly CardTag[] MelodyTags = [CardTag.Wah, CardTag.Cho, CardTag.Ki];
    private static readonly ConcurrentDictionary<(ModelId Id, int Up), Dictionary<string, decimal>> FreshVars = new();

    /// <summary>The melodies the player's card plays of this turn made, as the game counts them (<c>MelodyCmd.PlayedThisTurn</c>).
    /// The history is in order, so only this turn's end of it is read.</summary>
    private static List<string>? Melodies(CombatState st, Player player)
    {
        if (CombatManager.Instance.History.Entries is not IReadOnlyList<CombatHistoryEntry> entries) throw new InvalidOperationException("the combat history is no list");
        int played = 0;
        for (int i = entries.Count - 1; i >= 0 && entries[i].HappenedThisTurn(st); i--)
        {
            if (entries[i] is not CardPlayFinishedEntry play || play.CardPlay.Player != player) continue;
            for (int t = 0; t < MelodyTags.Length; t++)
                if (play.CardPlay.Card.Tags.Contains(MelodyTags[t])) played |= 1 << t;
        }
        return played == 0 ? null : [.. MelodyTags.Where((_, t) => (played >> t & 1) == 1).Select(t => t.ToString())];
    }

    /// <summary>How many of the player's arrows are stuck in <paramref name="c"/>.</summary>
    private static int? Stuck(Creature c, Creature me)
    {
        int stuck = 0;
        foreach (var power in c.Powers)
            if (power is ImpaledPower impaled && impaled.Applier == me) stuck += impaled.Arrows.Count;
        return stuck > 0 ? stuck : null;
    }

    /// <summary>How far the card's numbers other than its damage and block, which are read as they are, have moved from
    /// those of a fresh copy at its upgrade level. A calculated number is left out as in <see cref="Vars"/>.</summary>
    private static decimal? Grown(CardModel card)
    {
        var fresh = FreshVars.GetOrAdd((card.Id, card.CurrentUpgradeLevel), static key =>
        {
            var copy = CardModel.FromSerializable(new SerializableCard { Id = key.Id, CurrentUpgradeLevel = key.Up, FloorAddedToDeck = 1 });
            return copy.DynamicVars.Where(kv => kv.Value is not CalculatedVar).ToDictionary(kv => kv.Key, kv => kv.Value.BaseValue);
        });
        decimal moved = 0;
        foreach (var (name, v) in card.DynamicVars)
            if (v is not CalculatedVar && name is not ("Damage" or "OstyDamage" or "Block") && fresh.TryGetValue(name, out decimal was)) moved += v.BaseValue - was;
        return moved == 0 ? null : moved;
    }

    private static readonly FieldInfo LocalModifiers = typeof(CardEnergyCost).GetField("_localModifiers", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StarCosts = typeof(CardModel).GetField("_temporaryStarCosts", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo CanonicalPower = typeof(PowerModel).GetProperty("CanonicalInstance", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Dictionary<Type, FieldInfo[]> OnceFlags = [];

    /// <summary>The energy cost without the card's changes that end when it is played or when the turn ends. The game keeps them apart only inside the cost.</summary>
    private static int? Lasting(CardModel card)
    {
        var cost = card.EnergyCost;
        if (!cost.HasLocalModifiers) return null;
        int lasting = cost.GetWithModifiers(CostModifiers.None);
        if (lasting < 0) return null;
        foreach (var modifier in (List<LocalCostModifier>)LocalModifiers.GetValue(cost)!)
            if (modifier.Expiration == LocalCostModifierExpiration.EndOfCombat) lasting = modifier.Modify(lasting);
        int gone = lasting - cost.GetWithModifiers(CostModifiers.Local);
        return gone == 0 ? null : Math.Max(0, cost.GetWithModifiers(CostModifiers.All) + gone);
    }

    /// <summary>The same as <see cref="Lasting"/> for the star cost.</summary>
    private static int? StarsLasting(CardModel card)
    {
        var temporary = (List<TemporaryCardCost>)StarCosts.GetValue(card)!;
        if (temporary.Count == 0) return null;
        int lasting = temporary.LastOrDefault(c => !c.ClearsWhenTurnEnds && !c.ClearsWhenCardIsPlayed)?.Cost ?? card.BaseStarCost;
        int gone = lasting - card.CurrentStarCost;
        return gone == 0 ? null : Math.Max(0, card.GetStarCostWithModifiers() + gone);
    }

    /// <summary>How far the power's numbers have moved from its canonical ones. One moves at a time, Slow's two summed.</summary>
    private static decimal? Detail(PowerModel power)
    {
        if (power.DynamicVars.Count == 0 || CanonicalPower.GetValue(power) is not PowerModel canonical) return null;
        decimal? moved = null;
        foreach (var (name, v) in power.DynamicVars)
            if (v is not CalculatedVar && canonical.DynamicVars.TryGetValue(name, out var c) && c.BaseValue != v.BaseValue) moved = (moved ?? 0) + v.BaseValue - c.BaseValue;
        return moved;
    }

    /// <summary>A relic's own flag of an effect spent for this combat or this turn: a private field the game names so, on the
    /// relics that have one.</summary>
    private static bool? Spent(RelicModel relic)
    {
        lock (OnceFlags)
        {
            if (!OnceFlags.TryGetValue(relic.GetType(), out var flags))
                OnceFlags[relic.GetType()] = flags = [.. relic.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Where(f => f.FieldType == typeof(bool) && (f.Name.EndsWith("ThisCombat", StringComparison.Ordinal) || f.Name.EndsWith("ThisTurn", StringComparison.Ordinal)))
                ];
            return flags.Any(f => (bool)f.GetValue(relic)!) ? true : null;
        }
    }

    /// <summary>The keywords an effect gave the card for this turn only, beside its own.</summary>
    private static List<string>? ThisTurn(CardModel card)
    {
        List<string> kept = [];
        if (card.ShouldRetainThisTurn && !card.Keywords.Contains(CardKeyword.Retain)) kept.Add(nameof(CardKeyword.Retain));
        if (card.IsSlyThisTurn && !card.Keywords.Contains(CardKeyword.Sly)) kept.Add(nameof(CardKeyword.Sly));
        return kept.Count > 0 ? kept : null;
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

    /// <summary>The key of the card as a combat built from an observation has it: without what a build does not set.</summary>
    public static string BuiltKey(CardModel card) => ProtocolJson.CardKey(Card(card) with
    {
        Lasting = null, StarsLasting = null, Replay = null, Spent = null, Calc = null, ThisTurn = null,
        Enchant = card.Enchantment is { } e ? new ModifierView(e.Id.Entry, e.Amount) : null,
    });

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
        Intents = c.Monster is { } monster && c.Side != CombatSide.Player ? Intents(monster.NextMove, c, targets) : null,
    };

    /// <summary>The intents <paramref name="move"/> would show above <paramref name="owner"/>, aimed at <paramref name="targets"/>.</summary>
    public static List<IntentView> Intents(MoveState move, Creature owner, Creature[] targets) =>
        [.. move.Intents.Select(i => Intent(i, owner, targets))];

    private static IntentView Intent(AbstractIntent intent, Creature owner, Creature[] targets) => intent is AttackIntent attack
        ? new IntentView { Type = intent.IntentType.ToString(), Damage = attack.GetSingleDamage(targets, owner), Hits = Math.Max(1, attack.Repeats) }
        : new IntentView { Type = intent.IntentType.ToString() };

    private static List<PowerView> Powers(Creature c) =>
        [.. c.Powers.Where(p => p.IsVisible).Select(p => new PowerView(p.Id.Entry, p.DisplayAmount, p.DisplayAmount != p.Amount ? p.Amount : null,
            HeldCards.Get(p) is { } card ? Card(card) : null, Detail(p), p is SwipePower { StolenCard: { } stolen } ? Card(stolen) : null))];

    public static RelicView Relic(RelicModel r) => new()
    {
        Id = r.Id.Entry,
        Counter = r.ShowCounter ? r.DisplayAmount : null,
        Status = r.Status != RelicStatus.Normal ? r.Status.ToString() : null,
        Melted = r.IsMelted ? true : null,
        UsedUp = r.IsUsedUp ? true : null,
        Spent = Spent(r),
        Cards = r is PaelsTooth { SerializableCards.Count: > 0 } tooth ? tooth.SerializableCards.Select(c => c.Id!.Entry).ToList() : null,
    };
}
