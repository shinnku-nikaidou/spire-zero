using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace Sts2Ai.Headless;

/// <summary>
/// What the player can see of a combat, as JSON. This is the only view of the game the brain gets, so every field
/// is whitelisted from what the game's UI shows. Hidden information never appears: the draw pile's order (it is
/// listed sorted, as a multiset), seeds and RNG state, which move a monster has queued (only its intents), and
/// anything about future turns.
/// </summary>
internal static class Observation
{
    public static JsonObject Of(CombatSession session)
    {
        var st = session.State;
        var player = session.Player;
        var pcs = player.PlayerCombatState!;
        var me = player.Creature;
        Creature[] targets = [me];
        return new JsonObject
        {
            ["round"] = st.RoundNumber,
            ["player"] = new JsonObject
            {
                ["character"] = player.Character.Id.Entry,
                ["hp"] = me.CurrentHp,
                ["max_hp"] = me.MaxHp,
                ["block"] = me.Block,
                ["energy"] = pcs.Energy,
                ["max_energy"] = pcs.MaxEnergy,
                ["stars"] = pcs.Stars,
                ["gold"] = player.Gold,
                ["powers"] = Powers(me),
                ["orb_slots"] = pcs.OrbQueue.Capacity,
                ["orbs"] = Array(pcs.OrbQueue.Orbs, o => new JsonObject { ["id"] = o.Id.Entry, ["passive"] = o.PassiveVal, ["evoke"] = o.EvokeVal }),
            },
            ["hand"] = Array(pcs.Hand.Cards, Card),
            ["draw"] = Array(pcs.DrawPile.Cards.OrderBy(CardKey, StringComparer.Ordinal), Card),
            ["discard"] = Array(pcs.DiscardPile.Cards, Card),
            ["exhaust"] = Array(pcs.ExhaustPile.Cards, Card),
            ["potions"] = Array(player.PotionSlots, p => p == null ? null : JsonValue.Create(p.Id.Entry)),
            ["relics"] = Array(player.Relics, Relic),
            ["allies"] = Array(st.Allies.Where(c => c != me), c => Creature(c, targets)),
            ["enemies"] = Array(st.Enemies, c => Creature(c, targets)),
        };
    }

    /// <summary>A card as the player sees it. Two cards with the same key are indistinguishable to the player.</summary>
    public static JsonObject Card(CardModel card)
    {
        var o = new JsonObject { ["id"] = card.Id.Entry };
        if (card.CurrentUpgradeLevel > 0) o["up"] = card.CurrentUpgradeLevel;
        o["cost"] = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetWithModifiers(CostModifiers.All);
        if (card.CanonicalStarCost >= 0) o["stars"] = card.GetStarCostWithModifiers();
        if (card.Enchantment is { } enchantment) o["enchant"] = $"{enchantment.Id.Entry}:{enchantment.Amount}";
        if (card.Affliction is { } affliction) o["affliction"] = $"{affliction.Id.Entry}:{affliction.Amount}";
        if (card.Keywords.Count > 0) o["keywords"] = Array(card.Keywords.Select(k => k.ToString()).Order(), k => JsonValue.Create(k));
        if (card.DynamicVars.Count > 0)
        {
            var vars = new JsonObject();
            foreach (var (name, v) in card.DynamicVars.OrderBy(kv => kv.Key, StringComparer.Ordinal)) vars[name] = v.BaseValue;
            o["vars"] = vars;
        }
        return o;
    }

    public static string CardKey(CardModel card) => Card(card).ToJsonString();

    private static JsonObject Creature(Creature c, Creature[] targets)
    {
        var o = new JsonObject
        {
            ["id"] = c.Monster?.Id.Entry,
            ["hp"] = c.CurrentHp,
            ["max_hp"] = c.MaxHp,
            ["block"] = c.Block,
            ["powers"] = Powers(c),
        };
        if (c.Monster is { } monster && c.IsAlive && c.Side != CombatSide.Player)
            o["intents"] = Array(monster.NextMove.Intents, intent => Intent(intent, c, targets));
        return o;
    }

    /// <summary>The intent icon and its number, as shown above the monster.</summary>
    private static JsonObject Intent(AbstractIntent intent, Creature owner, Creature[] targets)
    {
        var o = new JsonObject { ["type"] = intent.IntentType.ToString() };
        if (intent is AttackIntent attack)
        {
            o["damage"] = attack.GetSingleDamage(targets, owner);
            o["hits"] = Math.Max(1, attack.Repeats);
        }
        return o;
    }

    private static JsonArray Powers(Creature c) =>
        Array(c.Powers.Where(p => p.IsVisible), p => new JsonObject { ["id"] = p.Id.Entry, ["amount"] = p.DisplayAmount });

    private static JsonObject Relic(RelicModel r)
    {
        var o = new JsonObject { ["id"] = r.Id.Entry };
        if (r.ShowCounter) o["counter"] = r.DisplayAmount;
        if (r.Status != RelicStatus.Normal) o["status"] = r.Status.ToString();
        if (r.IsMelted) o["melted"] = true;
        return o;
    }

    private static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonNode?> map) => new(items.Select(map).ToArray());
}
