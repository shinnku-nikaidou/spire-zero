using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// A brain's answer as the player reads it: each action named as the screen shows it, in the game's language, from
/// the observation it answered. An id the game has no text for is shown as is.
/// </summary>
internal static partial class Describe
{
    public static string[] Recommendation(Recommendation answer, Observation obs)
    {
        // On the map, the panel rings the point the answer goes to and draws the route after it (MapMark).
        bool map = ActionText.Is(answer.Action, "map", out _);
        var lines = new List<string> { $"→ {Action(answer.Action, obs)}{(map ? ", the purple ring on the map" : "")}" };
        if (answer.Plan is { Count: > 0 } plan)
            lines.Add(map ? "then the purple line to the boss" : "then " + string.Join(", ", plan.Select(a => Action(a, obs))));
        if (answer.Candidates is { Count: > 1 } candidates)
            lines.Add(string.Join("   ", candidates.Take(3).Select(c => $"{Action(c.Action, obs)} {c.Value:0.0}±{c.Ci:0.0}")));
        return [.. lines];
    }

    private static string Action(string action, Observation obs)
    {
        // A combat potion action carries its target too ("potion:SLOT:TARGET"); the combat names it.
        if (obs.Screen is CombatScreen { Combat: var combat }) return Combat(action, combat);
        if (ActionText.Is(action, "potion", out var arg)) return "drink " + Potion(obs, arg);
        if (ActionText.Is(action, "discard_potion", out arg)) return "discard " + Potion(obs, arg);
        return obs.Screen switch
        {
            CardChoiceScreen { Choice: var choice } => Pick(action, choice),
            MapScreen => Map(action, obs.Run.Map),
            RewardsScreen rewards => Reward(action, rewards),
            CardRewardScreen reward => CardReward(action, reward),
            BundleScreen bundles when ActionText.Is(action, "bundle", out arg) =>
                $"bundle {ActionText.Index(arg) + 1}: " + string.Join(", ", bundles.Bundles[ActionText.Index(arg)].Select(Card)),
            EventScreen ev => Event(action, ev),
            ShopScreen shop => Shop(action, shop),
            RestScreen when ActionText.Is(action, "rest", out arg) => Text("rest_site_ui", $"OPTION_{arg}.name") ?? arg,
            TreasureScreen treasure when ActionText.Is(action, "relic", out arg) => Relic(treasure.Relics[ActionText.Index(arg)]),
            TreasureScreen when action == TreasureActions.Skip => "skip the relic",
            CrystalSphereScreen when ActionText.Is(action, "big", out arg) => $"big divination at {arg}",
            CrystalSphereScreen when ActionText.Is(action, "small", out arg) => $"small divination at {arg}",
            _ => action,
        };
    }

    private static string Combat(string text, CombatObservation combat)
    {
        if (!CombatAction.TryParse(text, out var action)) return text;
        string target = action.Target is uint t ? " → " + Creature(t, combat) : "";
        return action.Kind switch
        {
            CombatActionKind.PlayCard => (combat.Hand.FirstOrDefault(c => c.Uid == action.Card) is { } card ? Card(card) : $"card {action.Card}") + target,
            CombatActionKind.UsePotion => "drink " + (combat.Potions.ElementAtOrDefault(action.PotionSlot!.Value) is { } p ? Potion(p) : "?") + target,
            _ => "end turn",
        };
    }

    private static string Creature(uint uid, CombatObservation combat)
    {
        var enemies = combat.Enemies.Where(e => e.Hp > 0).ToList();
        if (enemies.FindIndex(e => e.Uid == uid) is var i and >= 0)
        {
            string name = enemies[i].Id is { } id ? Monster(id) : "enemy";
            return enemies.Count > 1 ? $"{name} #{i + 1}" : name;
        }
        return combat.Player.Uid == uid ? "self" : combat.Allies.FirstOrDefault(a => a.Uid == uid)?.Id is { } ally ? Monster(ally) : $"creature {uid}";
    }

    private static string Pick(string action, CardChoice choice)
    {
        if (!ActionText.Is(action, "pick", out var arg)) return action;
        var picks = arg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ActionText.Index).ToList();
        return picks.Count == 0 ? "pick none" : "pick " + string.Join(", ", picks.Select(i => Card(choice.Options[i])));
    }

    /// <summary>The room the path leads to, by the map legend's name for it.</summary>
    private static string Map(string action, MapView map)
    {
        if (!ActionText.Is(action, "map", out var arg)) return action;
        var at = arg.Split(',').Select(int.Parse).ToArray();
        string? type = map.Points.FirstOrDefault(p => p.Col == at[0] && p.Row == at[1])?.Type;
        string? legend = type switch
        {
            "Monster" => "ENEMY",
            "Elite" => "ELITE",
            "Unknown" => "UNKNOWN",
            "RestSite" => "REST",
            "Shop" => "MERCHANT",
            "Treasure" => "TREASURE",
            _ => null,
        };
        string room = (legend != null ? Text("map", $"LEGEND_{legend}.title") : null) ?? type?.ToLowerInvariant() ?? "?";
        return "go to " + room;
    }

    private static string Reward(string action, RewardsScreen screen)
    {
        if (action == RewardActions.Done) return screen.Rewards.Count > 0 ? "skip the rest, proceed" : "proceed";
        if (!ActionText.Is(action, "reward", out var arg) || screen.Rewards.FirstOrDefault(r => r.Index == ActionText.Index(arg)) is not { } reward) return action;
        return reward.Kind switch
        {
            "gold" => $"take {reward.Amount} gold",
            "potion" => "take " + Potion(reward.Id!),
            "relic" => "take " + Relic(reward.Id!),
            "card" => "open the card reward" + (reward.Cards is { } cards ? ": " + string.Join(", ", cards.Select(Card)) : ""),
            "special_card" => "take " + (reward.Card is { } card ? Card(card) : "the card"),
            "remove_card" => "remove a card",
            var kind => kind,
        };
    }

    private static string CardReward(string action, CardRewardScreen screen)
    {
        if (ActionText.Is(action, "card", out var arg)) return "take " + Card(screen.Cards[ActionText.Index(arg)]);
        if (ActionText.Is(action, "card_alt", out arg)) return Text("card_reward_ui", $"OPTION_{arg.ToUpperInvariant()}.name") ?? arg;
        return action;
    }

    private static string Event(string action, EventScreen ev)
    {
        if (action == EventActions.Proceed) return "proceed";
        if (!ActionText.Is(action, "event", out var arg) || ev.Options.FirstOrDefault(o => o.Index == ActionText.Index(arg)) is not { } option) return action;
        // Ancients keep their texts in their own table.
        return Text("events", option.Key + ".title") ?? Text("ancients", option.Key + ".title") ?? option.Key[(option.Key.LastIndexOf('.') + 1)..];
    }

    private static string Shop(string action, ShopScreen shop)
    {
        if (action == ShopActions.Leave) return "leave the shop";
        if (action == ShopActions.Remove) return $"remove a card ({shop.RemovalCost} gold)";
        if (!ActionText.Is(action, "buy", out var arg) || shop.Entries.FirstOrDefault(e => e.Slot == ActionText.Index(arg)) is not { } entry) return action;
        string what = entry.Kind switch
        {
            "card" => Card(entry.Card!),
            "relic" => Relic(entry.Id!),
            "potion" => Potion(entry.Id!),
            var kind => kind,
        };
        return $"buy {what} ({entry.Cost} gold)";
    }

    private static string Potion(Observation obs, string slot) =>
        obs.Run.Potions.ElementAtOrDefault(ActionText.Index(slot)) is { } id ? Potion(id) : $"potion {slot}";

    private static string Card(CardView card) => (Text("cards", card.Id + ".title") ?? card.Id) + (card.Up is > 0 ? "+" : "");
    private static string Relic(string id) => Text("relics", id + ".title") ?? id;
    private static string Potion(string id) => Text("potions", id + ".title") ?? id;
    private static string Monster(string id) => Text("monsters", id + ".name") ?? id;

    /// <summary>The text as the game's loc tables hold it, without its markup; null when it has none.</summary>
    private static string? Text(string table, string key) =>
        LocString.Exists(table, key) ? Markup().Replace(new LocString(table, key).GetRawText(), "") : null;

    [GeneratedRegex(@"\[/?[a-z_]+[^\]]*\]")]
    private static partial Regex Markup();
}
