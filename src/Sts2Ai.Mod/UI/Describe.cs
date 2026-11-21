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

/// <summary>A brain's answer as the panel shows it.</summary>
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
            ? candidates.Take(Listed).Select(c => new Shown.Option(Action(c.Action, obs, c.Plan), c.Value, c.Ci, c.Win, c.Hp,
                obs.Screen is MapScreen ? c.Plan?.Prepend(c.Action).Select(a => Point(a, obs.Run.Map)).ToList() : null)).ToList()
            : [];
        string next = Action(answer.Action, obs, candidates.FirstOrDefault(c => c.Action == answer.Action)?.Plan);
        // On the map, the route's rings and lines are drawn on the map itself (MapMark); the panel names its rooms.
        if (MacroAction.Parse(answer.Action).Verb == MacroAction.Map) return new(next, [.. plan.Select(a => Room(a, obs.Run.Map))], true, null, kind, options);
        if (obs.Screen is not CombatScreen) return new(next, [.. plan.Select(a => Action(a, obs))], false, null, kind, options);
        var (turn, later) = Turn(answer.Action, plan);
        return new(next, [.. turn.Skip(1).Select(a => Action(a, obs))], false,
            later == 0 ? null : L10n.T("{0} more turns planned", later), kind, options);
    }

    /// <summary>The map point type a map action goes to ("Elite", "RestSite", ...), or null.</summary>
    public static string? Point(string action, MapView map)
    {
        var move = MacroAction.Parse(action);
        if (move.Verb != MacroAction.Map) return null;
        var at = move.Coord;
        return map.Points.FirstOrDefault(p => p.Col == at.Col && p.Row == at.Row)?.Type;
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

    /// <param name="line">Outside combat, the answers the brain would give after the action (<see cref="Candidate.Plan"/>).</param>
    public static string Action(string text, Observation obs, IReadOnlyList<string>? line = null)
    {
        // A combat potion action carries its target too ("potion:SLOT:TARGET"); the combat names it.
        if (obs.Screen is CombatScreen { Combat: var combat }) return Combat(text, combat);
        var action = MacroAction.Parse(text);
        if (action.Verb == MacroAction.Potion) return L10n.T("drink {0}", Potion(obs, action.Index));
        if (action.Verb == MacroAction.DiscardPotion) return L10n.T("discard {0}", Potion(obs, action.Index));
        return obs.Screen switch
        {
            CardChoiceScreen { Choice: var choice } when action.Verb == MacroAction.PickVerb => Pick(action, choice),
            MapScreen when action.Verb == MacroAction.Map => L10n.T("go to {0}", Room(text, obs.Run.Map)),
            RewardsScreen rewards => Reward(action, rewards, line),
            CardRewardScreen reward => CardReward(action, reward),
            BundleScreen bundles when action.Verb == MacroAction.Bundle =>
                L10n.T("bundle {0}", action.Index + 1) + ": " + string.Join(", ", bundles.Bundles[action.Index].Select(Card)),
            EventScreen ev => Event(action, ev),
            ShopScreen shop => Shop(action, shop),
            RestScreen when action.Verb == MacroAction.Rest => Text("rest_site_ui", $"OPTION_{action.Arg}.name") ?? action.Arg,
            RestScreen when action == MacroAction.Leave => L10n.T("leave"),
            TreasureScreen treasure when action.Verb == MacroAction.Relic => Relic(treasure.Relics[action.Index]),
            TreasureScreen when action == MacroAction.RelicSkip => L10n.T("skip the relic"),
            CrystalSphereScreen when action.Verb == MacroAction.Big => L10n.T("big divination at {0}", action.Arg),
            CrystalSphereScreen when action.Verb == MacroAction.Small => L10n.T("small divination at {0}", action.Arg),
            _ => text,
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
            // A planned answer to a prompt that is not on screen yet: its cards are named when the prompt is.
            CombatActionKind.Pick => L10n.T(action.Picks.Count == 0 ? "pick none" : "pick cards"),
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

    private static string Pick(MacroAction action, CardChoice choice)
    {
        var picks = action.Picks;
        return picks.Count == 0 ? L10n.T("pick none") : L10n.T("pick {0}", string.Join(", ", picks.Select(i => Card(choice.Options[i]))));
    }

    /// <summary>The room a map action goes to, by the map legend's name for it.</summary>
    private static string Room(string action, MapView map) => MacroAction.Parse(action).Verb == MacroAction.Map ? Legend(Point(action, map)) : action;

    /// <summary>A reward as the rewards screen shows it. A card reward is named with the card the brain would take in it,
    /// the first answer of its <paramref name="line"/>; without one, with the cards it holds.</summary>
    private static string Reward(MacroAction action, RewardsScreen screen, IReadOnlyList<string>? line)
    {
        if (action == MacroAction.RewardsDone) return L10n.T(screen.Rewards.Count > 0 ? "skip the rest, proceed" : "proceed");
        if (action.Verb != MacroAction.Reward || screen.Rewards.FirstOrDefault(r => r.Index == action.Index) is not { } reward) return action.ToString();
        return reward.Kind switch
        {
            "gold" => L10n.T("take {0} gold", reward.Amount ?? 0),
            "potion" => L10n.T("take {0}", Potion(reward.Id!)),
            "relic" => L10n.T("take {0}", Relic(reward.Id!)),
            "card" when reward.Cards is { } cards && Taken(line, cards) is { } taken => L10n.T("open the card reward") + " → " + taken,
            "card" => L10n.T("open the card reward") + (reward.Cards is { } cards ? ": " + string.Join(", ", cards.Select(Card)) : ""),
            "special_card" => L10n.T("take {0}", reward.Card is { } card ? Card(card) : L10n.T("the card")),
            "remove_card" => L10n.T("remove a card"),
            var kind => kind,
        };
    }

    /// <summary>What a card reward of <paramref name="cards"/> is answered with first along <paramref name="line"/>: a card,
    /// or one of its alternatives; null for none.</summary>
    private static string? Taken(IReadOnlyList<string>? line, IReadOnlyList<CardView> cards)
    {
        if (line is not [var first, ..]) return null;
        var action = MacroAction.Parse(first);
        return action.Verb switch
        {
            MacroAction.Card when action.Index < cards.Count => Card(cards[action.Index]),
            MacroAction.CardAlt => Alternative(action),
            _ => null,
        };
    }

    private static string CardReward(MacroAction action, CardRewardScreen screen) => action.Verb switch
    {
        MacroAction.Card => L10n.T("take {0}", Card(screen.Cards[action.Index])),
        MacroAction.CardAlt => Alternative(action),
        _ => action.ToString(),
    };

    /// <summary>A card reward's alternative to a card (skipping, the Singing Bowl's max HP), as its button names it.</summary>
    private static string Alternative(MacroAction action) => Text("card_reward_ui", $"OPTION_{action.Arg.ToUpperInvariant()}.name") ?? action.Arg;

    private static string Event(MacroAction action, EventScreen ev)
    {
        if (action == MacroAction.Proceed) return L10n.T("proceed");
        if (action.Verb != MacroAction.Event || ev.Options.FirstOrDefault(o => o.Index == action.Index) is not { } option) return action.ToString();
        // Ancients keep their texts in their own table.
        return Text("events", option.Key + ".title") ?? Text("ancients", option.Key + ".title") ?? option.Key[(option.Key.LastIndexOf('.') + 1)..];
    }

    private static string Shop(MacroAction action, ShopScreen shop)
    {
        if (action == MacroAction.Leave) return L10n.T("leave the shop");
        if (action == MacroAction.Remove) return L10n.T("remove a card ({0} gold)", shop.RemovalCost ?? 0);
        if (action.Verb != MacroAction.Buy || shop.Entries.FirstOrDefault(e => e.Slot == action.Index) is not { } entry) return action.ToString();
        string what = entry.Kind switch
        {
            "card" => Card(entry.Card!),
            "relic" => Relic(entry.Id!),
            "potion" => Potion(entry.Id!),
            var kind => kind,
        };
        return L10n.T("buy {0} ({1} gold)", what, entry.Cost);
    }

    private static string Potion(Observation obs, int slot) => obs.Run.Potions.ElementAtOrDefault(slot) is { } id ? Potion(id) : $"potion {slot}";

    private static string Card(CardView card) => (Text("cards", card.Id + ".title") ?? card.Id) + (card.Up is > 0 ? "+" : "");
    public static string Relic(string id) => Text("relics", id + ".title") ?? id;
    public static string Potion(string id) => Text("potions", id + ".title") ?? id;
    /// <summary>A card of a fight table (<see cref="TurnRow.Cards"/>): its id, "+" after it when upgraded.</summary>
    public static string TableCard(string card) => card.EndsWith('+') ? (Text("cards", card[..^1] + ".title") ?? card[..^1]) + "+" : Text("cards", card + ".title") ?? card;

    /// <summary>An enemy of a fight table by its monster id.</summary>
    public static string TableEnemy(string? id) => id != null ? Monster(id) : "?";

    private static string Monster(string id) => Text("monsters", id + ".name") is { } name ? Variable().Replace(name, "") : id;

    /// <summary>The text as the game's loc tables hold it, without its markup; null when it has none.</summary>
    private static string? Text(string table, string key) =>
        LocString.Exists(table, key) ? Markup().Replace(new LocString(table, key).GetRawText(), "") : null;

    [GeneratedRegex(@"\[/?[a-z_]+[^\]]*\]")]
    private static partial Regex Markup();

    // A word of a monster's name that holds a variable, dropped: Test Subject's "#C{Count}" (the profile's kills).
    [GeneratedRegex(@"\s*\S*\{[^}]*\}\S*")]
    private static partial Regex Variable();
}
