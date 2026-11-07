using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>What the values of an answer's options are.</summary>
internal enum ShownKind
{
    /// <summary>A search's score (PIMC's), shown with its confidence interval.</summary>
    Score,

    /// <summary>The SL planner's: the chance to win the combat and the HP held at its end.</summary>
    Fight,

    /// <summary>V's: the chance to win the run.</summary>
    Run,
}

/// <summary>A brain's answer as the panel shows it (docs: output §2).</summary>
/// <param name="Next">The action to take.</param>
/// <param name="Then">The plan after it: the rest of the turn in combat, the rooms the route goes through on the map.</param>
/// <param name="Route">Whether <paramref name="Then"/> is a map route, shown as rooms rather than numbered steps.</param>
/// <param name="Later">What the plan holds past <paramref name="Then"/>, such as later turns; null for nothing.</param>
/// <param name="Kind">What the options' values are.</param>
/// <param name="Options">The actions the brain scored, the chosen first, then best first; empty when it scored one.</param>
internal sealed record Shown(string Next, IReadOnlyList<string> Then, bool Route, string? Later, ShownKind Kind, IReadOnlyList<Shown.Option> Options)
{
    /// <param name="Rooms">On the map, the map point types of the option's point and the route after it.</param>
    public sealed record Option(string Name, double Value, double Ci, double? Win, double? Hp, IReadOnlyList<string?>? Rooms);
}

/// <summary>
/// A brain's answer as the player reads it: each action named as the screen shows it, in the game's language, from
/// the observation it answered. An id the game has no text for is shown as is.
/// </summary>
internal static partial class Describe
{
    /// <summary>How many of the scored options the panel lists.</summary>
    private const int Listed = 5;

    public static Shown Recommendation(Recommendation answer, Observation obs)
    {
        var plan = answer.Plan ?? [];
        var candidates = answer.Candidates ?? [];
        var kind = obs.Screen is CombatScreen or CardChoiceScreen { Combat: not null }
            ? candidates.Any(c => c.Win != null) ? ShownKind.Fight : ShownKind.Score
            : ShownKind.Run;
        var options = candidates.Count > 1 || kind != ShownKind.Score
            ? candidates.Take(Listed).Select(c => new Shown.Option(Action(c.Action, obs), c.Value, c.Ci, c.Win, c.Hp,
                c.Plan?.Prepend(c.Action).Select(a => Point(a, obs.Run.Map)).ToList())).ToList()
            : [];
        string next = Action(answer.Action, obs);
        // On the map, the route's rings and lines are drawn on the map itself (MapMark); the panel names its rooms.
        if (ActionText.Is(answer.Action, "map", out _)) return new(next, [.. plan.Select(a => Room(a, obs.Run.Map))], true, null, kind, options);
        if (obs.Screen is not CombatScreen) return new(next, [.. plan.Select(a => Action(a, obs))], false, null, kind, options);
        var (turn, later) = Turn(answer.Action, plan);
        return new(next, [.. turn.Skip(1).Select(a => Action(a, obs))], false,
            later == 0 ? null : L10n.T("{0} more turns planned", later), kind, options);
    }

    /// <summary>The map point type a map action goes to ("Elite", "RestSite", ...), or null.</summary>
    public static string? Point(string action, MapView map)
    {
        if (!ActionText.Is(action, "map", out var arg)) return null;
        var at = arg.Split(',').Select(int.Parse).ToArray();
        return map.Points.FirstOrDefault(p => p.Col == at[0] && p.Row == at[1])?.Type;
    }

    /// <summary>The map legend's name for a map point type, in the game's language.</summary>
    public static string Legend(string? type)
    {
        string? legend = type switch
        {
            "Monster" => "ENEMY",
            "Elite" => "ELITE",
            "Unknown" => "UNKNOWN",
            "RestSite" => "REST",
            "Shop" => "MERCHANT",
            "Treasure" => "TREASURE",
            "Boss" => "BOSS",
            "Ancient" => "ANCIENT",
            _ => null,
        };
        return (legend != null ? Text("map", $"LEGEND_{legend}.title") : null) ?? type?.ToLowerInvariant() ?? "?";
    }

    /// <summary>A character's name in the game's language.</summary>
    public static string Character(string id) => Text("characters", id + ".title") ?? id;

    /// <summary>
    /// A combat plan from the action on to this turn's end, and how many turns it plans after that. The SL planner plans
    /// the whole combat, but later turns play cards not drawn yet, which the screen cannot name: they are only counted.
    /// </summary>
    public static (IReadOnlyList<string> Turn, int Later) Turn(string first, IReadOnlyList<string> plan)
    {
        static bool Ends(string a) => CombatAction.TryParse(a, out var c) && c.Kind == CombatActionKind.EndTurn;
        var all = plan.Prepend(first).ToList();
        int end = all.FindIndex(Ends);
        int later = end < 0 ? 0 : all.Skip(end + 1).Count(Ends) + (all.Count > end + 1 && !Ends(all[^1]) ? 1 : 0);
        return (end < 0 ? all : [.. all.Take(end + 1)], later);
    }

    private static string Action(string action, Observation obs)
    {
        // A combat potion action carries its target too ("potion:SLOT:TARGET"); the combat names it.
        if (obs.Screen is CombatScreen { Combat: var combat }) return Combat(action, combat);
        if (ActionText.Is(action, "potion", out var arg)) return L10n.T("drink {0}", Potion(obs, arg));
        if (ActionText.Is(action, "discard_potion", out arg)) return L10n.T("discard {0}", Potion(obs, arg));
        return obs.Screen switch
        {
            CardChoiceScreen { Choice: var choice } => Pick(action, choice),
            MapScreen => Map(action, obs.Run.Map),
            RewardsScreen rewards => Reward(action, rewards),
            CardRewardScreen reward => CardReward(action, reward),
            BundleScreen bundles when ActionText.Is(action, "bundle", out arg) =>
                L10n.T("bundle {0}", ActionText.Index(arg) + 1) + ": " + string.Join(", ", bundles.Bundles[ActionText.Index(arg)].Select(Card)),
            EventScreen ev => Event(action, ev),
            ShopScreen shop => Shop(action, shop),
            RestScreen when ActionText.Is(action, "rest", out arg) => Text("rest_site_ui", $"OPTION_{arg}.name") ?? arg,
            RestScreen when action == RestActions.Leave => L10n.T("leave"),
            TreasureScreen treasure when ActionText.Is(action, "relic", out arg) => Relic(treasure.Relics[ActionText.Index(arg)]),
            TreasureScreen when action == TreasureActions.Skip => L10n.T("skip the relic"),
            CrystalSphereScreen when ActionText.Is(action, "big", out arg) => L10n.T("big divination at {0}", arg),
            CrystalSphereScreen when ActionText.Is(action, "small", out arg) => L10n.T("small divination at {0}", arg),
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
            CombatActionKind.UsePotion => L10n.T("drink {0}", combat.Potions.ElementAtOrDefault(action.PotionSlot!.Value) is { } p ? Potion(p) : "?") + target,
            _ => L10n.T("end turn"),
        };
    }

    private static string Creature(uint uid, CombatObservation combat)
    {
        var enemies = combat.Enemies.Where(e => e.Hp > 0).ToList();
        if (enemies.FindIndex(e => e.Uid == uid) is var i and >= 0)
        {
            string name = enemies[i].Id is { } id ? Monster(id) : L10n.T("enemy");
            return enemies.Count > 1 ? $"{name} #{i + 1}" : name;
        }
        return combat.Player.Uid == uid ? L10n.T("self") : combat.Allies.FirstOrDefault(a => a.Uid == uid)?.Id is { } ally ? Monster(ally) : $"creature {uid}";
    }

    private static string Pick(string action, CardChoice choice)
    {
        if (!ActionText.Is(action, "pick", out var arg)) return action;
        var picks = arg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ActionText.Index).ToList();
        return picks.Count == 0 ? L10n.T("pick none") : L10n.T("pick {0}", string.Join(", ", picks.Select(i => Card(choice.Options[i]))));
    }

    private static string Map(string action, MapView map) => ActionText.Is(action, "map", out _) ? L10n.T("go to {0}", Room(action, map)) : action;

    /// <summary>The room a map action goes to, by the map legend's name for it.</summary>
    private static string Room(string action, MapView map) => ActionText.Is(action, "map", out _) ? Legend(Point(action, map)) : action;

    private static string Reward(string action, RewardsScreen screen)
    {
        if (action == RewardActions.Done) return L10n.T(screen.Rewards.Count > 0 ? "skip the rest, proceed" : "proceed");
        if (!ActionText.Is(action, "reward", out var arg) || screen.Rewards.FirstOrDefault(r => r.Index == ActionText.Index(arg)) is not { } reward) return action;
        return reward.Kind switch
        {
            "gold" => L10n.T("take {0} gold", reward.Amount ?? 0),
            "potion" => L10n.T("take {0}", Potion(reward.Id!)),
            "relic" => L10n.T("take {0}", Relic(reward.Id!)),
            "card" => L10n.T("open the card reward") + (reward.Cards is { } cards ? ": " + string.Join(", ", cards.Select(Card)) : ""),
            "special_card" => L10n.T("take {0}", reward.Card is { } card ? Card(card) : L10n.T("the card")),
            "remove_card" => L10n.T("remove a card"),
            var kind => kind,
        };
    }

    private static string CardReward(string action, CardRewardScreen screen)
    {
        if (ActionText.Is(action, "card", out var arg)) return L10n.T("take {0}", Card(screen.Cards[ActionText.Index(arg)]));
        if (ActionText.Is(action, "card_alt", out arg)) return Text("card_reward_ui", $"OPTION_{arg.ToUpperInvariant()}.name") ?? arg;
        return action;
    }

    private static string Event(string action, EventScreen ev)
    {
        if (action == EventActions.Proceed) return L10n.T("proceed");
        if (!ActionText.Is(action, "event", out var arg) || ev.Options.FirstOrDefault(o => o.Index == ActionText.Index(arg)) is not { } option) return action;
        // Ancients keep their texts in their own table.
        return Text("events", option.Key + ".title") ?? Text("ancients", option.Key + ".title") ?? option.Key[(option.Key.LastIndexOf('.') + 1)..];
    }

    private static string Shop(string action, ShopScreen shop)
    {
        if (action == ShopActions.Leave) return L10n.T("leave the shop");
        if (action == ShopActions.Remove) return L10n.T("remove a card ({0} gold)", shop.RemovalCost ?? 0);
        if (!ActionText.Is(action, "buy", out var arg) || shop.Entries.FirstOrDefault(e => e.Slot == ActionText.Index(arg)) is not { } entry) return action;
        string what = entry.Kind switch
        {
            "card" => Card(entry.Card!),
            "relic" => Relic(entry.Id!),
            "potion" => Potion(entry.Id!),
            var kind => kind,
        };
        return L10n.T("buy {0} ({1} gold)", what, entry.Cost);
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
