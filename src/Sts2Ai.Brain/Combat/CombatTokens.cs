using System.Text.Json;
using Sts2Ai.Brain.Macro;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>A card of a pile: its id, upgrade level (capped at <see cref="RunNet.MaxUp"/>), pile (<see cref="CombatTokens.Piles"/>),
/// modifiers, cost / 3 (0 for X or none), its enchantment's and affliction's amounts / 10, its damage / 20 (Osty's for
/// an Osty attack, 0 for none), and its numbers: block / 20, the calculated number / 20, replays / 2, star cost / 5 and
/// the damage its enchantment gathered / 20 (Momentum's).</summary>
public sealed record CombatCardToken(
    string Id, int Up, int Pile, IReadOnlyList<string> Modifiers, double Cost, IReadOnlyList<double> Amounts, double Damage, IReadOnlyList<double> Numbers);

/// <summary>A power: its id, its amount / 10, the stacks its tooltip states apart from its icon / 10 (else 0), the id of
/// the card it keeps (Nightmare's) or stole (Swipe's), and the number moved from its own / 20 (else 0).</summary>
public sealed record PowerToken(string Id, double Amount, double Stacks, string? Card, double Detail);

/// <summary>An intent: its type, its damage per hit / 10 and its total damage / 20 (0 for no attack).</summary>
public sealed record IntentToken(string Type, double Damage, double Total);

/// <summary>An enemy, one lying dead until it revives included: its id, its HP share, HP / 100, max HP / 100, block / 20
/// and how many rounds it has shown / 10, its powers, and its slots of intents: those now, then those of its last rounds
/// before this one, most recent first.</summary>
public sealed record EnemyTokens(string Id, IReadOnlyList<double> Scalars, IReadOnlyList<PowerToken> Powers, IReadOnlyList<IReadOnlyList<IntentToken>> Slots);

/// <summary>A living ally: its id (a player's as "&lt;player&gt;"), its HP share, HP / 100, max HP / 100 and block / 20, and its powers.</summary>
public sealed record AllyTokens(string Id, IReadOnlyList<double> Scalars, IReadOnlyList<PowerToken> Powers);

/// <summary>
/// A combat state as the combat net reads it (docs: combat-brain/network §2): ids, and numbers scaled and capped. This is
/// the one place a state is turned into the net's reading. <see cref="CombatNet"/> looks the ids up in its net file's
/// tables and feeds the graph; the trainer reads the same tokens as JSON (<see cref="Write"/>) and does the same.
/// </summary>
/// <param name="Player">The player's HP share, HP / 100, max HP / 100, block / 20, energy / 3, max energy / 3 and stars / 5, and the round / 10.</param>
/// <param name="Relics">Each relic's id, with ":used" once used up.</param>
/// <param name="RelicValues">Each relic's numbers (<see cref="RelicValuesOf"/>).</param>
/// <param name="RelicStatus">Each relic's status (<see cref="RelicStatusOf"/>).</param>
/// <param name="Context">The character, the encounter, the room and the act.</param>
/// <param name="OrbValues">Each orb's passive / 10 and evoke / 10.</param>
/// <param name="Extra">The ascension / 10, the orb slots / 10, the gold / 100 and the potion slots / 10.</param>
public sealed record CombatTokens(
    IReadOnlyList<CombatCardToken> Cards, IReadOnlyList<PowerToken> Powers, IReadOnlyList<double> Player, IReadOnlyList<EnemyTokens> Enemies,
    IReadOnlyList<string> Relics, IReadOnlyList<IReadOnlyList<double>> RelicValues, IReadOnlyList<string> RelicStatus,
    IReadOnlyList<string> Potions, IReadOnlyList<string> Context, IReadOnlyList<string> Orbs, IReadOnlyList<IReadOnlyList<double>> OrbValues,
    IReadOnlyList<AllyTokens> Allies, IReadOnlyList<double> Extra)
{
    /// <summary>Every scaled number is capped at ±this: a few states show huge ones (an erupting WATERFALL_GIANT's HP).</summary>
    public const double Cap = 10;

    /// <summary>How many earlier rounds of each enemy's intents the tokens made for a trainer hold; a net file says its own.</summary>
    public const int Lags = 3;

    /// <summary>The piles, in the order a card's <see cref="CombatCardToken.Pile"/> counts them.</summary>
    public static readonly IReadOnlyList<string> Piles = ["hand", "draw", "discard", "exhaust"];

    /// <summary>How many numbers a card has beyond its cost and damage.</summary>
    public const int CardNumbers = 5;

    /// <summary>The tokens of a state.</summary>
    /// <param name="history">The combat's record of enemy moves; the rounds before this one are read.</param>
    /// <param name="lags">How many earlier rounds of each enemy's intents the net reads.</param>
    public static CombatTokens Of(CombatObservation obs, IReadOnlyList<EnemyTurns> history, int lags)
    {
        var pl = obs.Player;
        var past = history.ToDictionary(h => h.Uid, h => h.Turns.Where(t => t.Round < obs.Round).ToList());
        var enemies = obs.Enemies.Select(e =>
        {
            var turns = e.Uid is { } uid && past.TryGetValue(uid, out var t) ? t : [];
            var slots = new List<IReadOnlyList<IntentToken>> { Intents(e.Intents ?? []) };
            for (int lag = 1; lag <= lags && lag <= turns.Count; lag++) slots.Add(Intents(turns[^lag].Intents));
            return new EnemyTokens(e.Id ?? "<none>", [.. Creature(e.Hp, e.MaxHp, e.Block), Capped(turns.Count / 10.0)], PowersOf(e.Powers), slots);
        }).ToList();
        return new(
            [.. Piles.SelectMany((_, p) => Pile(obs, p).Select(c => Card(c, p)))],
            PowersOf(pl.Powers),
            [.. Creature(pl.Hp, pl.MaxHp, pl.Block), Capped(pl.Energy / 3.0), Capped(pl.MaxEnergy / 3.0), Capped(pl.Stars / 5.0), Capped(obs.Round / 10.0)],
            enemies,
            [.. obs.Relics.Select(RelicId)],
            [.. obs.Relics.Select(RelicValuesOf)],
            [.. obs.Relics.Select(RelicStatusOf)],
            [.. obs.Potions.OfType<string>()],
            ["character:" + pl.Character, "encounter:" + (obs.Encounter ?? "<none>"), "room:" + (obs.Room ?? "<none>"), $"act:{obs.Act}"],
            [.. pl.Orbs.Select(o => o.Id)],
            [.. pl.Orbs.Select(o => (IReadOnlyList<double>)[Capped((double)o.Passive / 10.0), Capped((double)o.Evoke / 10.0)])],
            [.. obs.Allies.Where(a => a.Hp > 0).Select(a => new AllyTokens(a.Id ?? "<player>", Creature(a.Hp, a.MaxHp, a.Block), PowersOf(a.Powers)))],
            [obs.Ascension / 10.0, pl.OrbSlots / 10.0, Capped(pl.Gold / 100.0), obs.Potions.Count / 10.0]);
    }

    /// <summary>A relic's id as both nets read it: with ":used" once used up.</summary>
    public static string RelicId(RelicView relic) => relic.UsedUp == true ? relic.Id + ":used" : relic.Id;

    /// <summary>A relic's numbers as both nets read them: counter / 10 (0 when none shows), whether one shows, whether it is melted.</summary>
    public static IReadOnlyList<double> RelicValuesOf(RelicView relic) =>
        [relic.Counter is { } counter ? Capped(counter / 10.0) : 0, relic.Counter != null ? 1 : 0, relic.Melted == true ? 1 : 0];

    /// <summary>A relic's status as both nets read it: the game's when not the normal one, else Spent for a once-a-combat
    /// effect used, else Normal.</summary>
    public static string RelicStatusOf(RelicView relic) => relic.Status ?? (relic.Spent == true ? "Spent" : "Normal");

    /// <summary>A card's modifier tokens: its enchantment and affliction first (in this order, as their amounts), then
    /// its keywords, its own and those it has for this turn only, the card type and rider Tinker Time made of it, and
    /// "spent" for its enchantment's effect used.</summary>
    private static List<string> Modifiers(CardView card)
    {
        var tokens = new List<string>();
        if (card.Enchant != null) tokens.Add("enchant:" + card.Enchant.Id);
        if (card.Affliction != null) tokens.Add("affliction:" + card.Affliction.Id);
        tokens.AddRange((card.Keywords ?? []).Concat(card.ThisTurn ?? []).Select(k => "keyword:" + k));
        if (card.Tinker != null) tokens.AddRange(["tinker:" + card.Tinker.Type, "rider:" + card.Tinker.Rider]);
        if (card.Spent == true) tokens.Add("spent");
        return tokens;
    }

    private static CombatCardToken Card(CardView card, int pile)
    {
        var vars = card.Vars ?? new Dictionary<string, decimal>();
        decimal damage = vars.TryGetValue("Damage", out var d) ? d : vars.GetValueOrDefault("OstyDamage");
        return new(card.Id, Math.Min(card.Up ?? 0, RunNet.MaxUp), pile, Modifiers(card),
            card.Cost is { } cost && cost > 0 ? cost / 3.0 : 0,
            [.. new[] { card.Enchant, card.Affliction }.OfType<ModifierView>().Select(m => Capped(m.Amount / 10.0))],
            Capped((double)damage / 20.0),
            [
                Capped((double)vars.GetValueOrDefault("Block") / 20.0), Capped((double)(card.Calc ?? 0) / 20.0), Capped((card.Replay ?? 0) / 2.0),
                Capped((card.Stars ?? 0) / 5.0), Capped((card.Enchant?.Extra ?? 0) / 20.0),
            ]);
    }

    private static List<PowerToken> PowersOf(IReadOnlyList<PowerView> powers) =>
    [
        .. powers.Select(p => new PowerToken(p.Id, Capped(p.Amount / 10.0), p.Stacks is { } stacks ? Capped(stacks / 10.0) : 0,
            (p.Card ?? p.Stolen)?.Id, Capped((double)(p.Detail ?? 0) / 20.0))),
    ];

    private static List<IntentToken> Intents(IReadOnlyList<IntentView> intents) =>
    [
        .. intents.Select(i =>
        {
            int damage = i.Damage ?? 0;
            return new IntentToken(i.Type, Capped(damage / 10.0), Capped(damage * (i.Hits ?? 1) / 20.0));
        }),
    ];

    /// <summary>A creature's HP share, HP / 100, max HP / 100 and block / 20.</summary>
    private static List<double> Creature(int hp, int maxHp, int block) =>
        [Capped(hp / (double)Math.Max(1, maxHp)), Capped(hp / 100.0), Capped(maxHp / 100.0), Capped(block / 20.0)];

    private static IReadOnlyList<CardView> Pile(CombatObservation obs, int pile) => pile switch
    {
        0 => obs.Hand,
        1 => obs.Draw,
        2 => obs.Discard,
        _ => obs.Exhaust,
    };

    private static double Capped(double x) => Math.Clamp(x, -Cap, Cap);

    /// <summary>
    /// The tokens as the trainer reads them (<c>learn/combat/net.py</c>'s Batch): an object by the names above in snake
    /// case; a card, a power and an intent each an array of its values in order (a power's card null when it has none).
    /// </summary>
    public void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteStartArray("cards");
        foreach (var c in Cards)
        {
            json.WriteStartArray();
            json.WriteStringValue(c.Id);
            json.WriteNumberValue(c.Up);
            json.WriteNumberValue(c.Pile);
            TokenJson.Strings(json, c.Modifiers);
            json.WriteNumberValue(c.Cost);
            TokenJson.Numbers(json, c.Amounts);
            json.WriteNumberValue(c.Damage);
            TokenJson.Numbers(json, c.Numbers);
            json.WriteEndArray();
        }
        json.WriteEndArray();
        json.WritePropertyName("powers");
        WritePowers(json, Powers);
        json.WritePropertyName("player");
        TokenJson.Numbers(json, Player);
        json.WriteStartArray("enemies");
        foreach (var e in Enemies)
        {
            json.WriteStartObject();
            json.WriteString("id", e.Id);
            json.WritePropertyName("scalars");
            TokenJson.Numbers(json, e.Scalars);
            json.WritePropertyName("powers");
            WritePowers(json, e.Powers);
            json.WriteStartArray("slots");
            foreach (var slot in e.Slots)
            {
                json.WriteStartArray();
                foreach (var i in slot)
                {
                    json.WriteStartArray();
                    json.WriteStringValue(i.Type);
                    json.WriteNumberValue(i.Damage);
                    json.WriteNumberValue(i.Total);
                    json.WriteEndArray();
                }
                json.WriteEndArray();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WritePropertyName("relics");
        TokenJson.Strings(json, Relics);
        json.WritePropertyName("relic_values");
        TokenJson.Rows(json, RelicValues);
        json.WritePropertyName("relic_status");
        TokenJson.Strings(json, RelicStatus);
        json.WritePropertyName("potions");
        TokenJson.Strings(json, Potions);
        json.WritePropertyName("context");
        TokenJson.Strings(json, Context);
        json.WritePropertyName("orbs");
        TokenJson.Strings(json, Orbs);
        json.WritePropertyName("orb_values");
        TokenJson.Rows(json, OrbValues);
        json.WriteStartArray("allies");
        foreach (var a in Allies)
        {
            json.WriteStartObject();
            json.WriteString("id", a.Id);
            json.WritePropertyName("scalars");
            TokenJson.Numbers(json, a.Scalars);
            json.WritePropertyName("powers");
            WritePowers(json, a.Powers);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WritePropertyName("extra");
        TokenJson.Numbers(json, Extra);
        json.WriteEndObject();
    }

    private static void WritePowers(Utf8JsonWriter json, IReadOnlyList<PowerToken> powers)
    {
        json.WriteStartArray();
        foreach (var p in powers)
        {
            json.WriteStartArray();
            json.WriteStringValue(p.Id);
            json.WriteNumberValue(p.Amount);
            json.WriteNumberValue(p.Stacks);
            if (p.Card != null) json.WriteStringValue(p.Card);
            else json.WriteNullValue();
            json.WriteNumberValue(p.Detail);
            json.WriteEndArray();
        }
        json.WriteEndArray();
    }
}
