using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>A run and the value the trainer computed for it, which loading checks, in its environment (null: SL); and
/// each potion it holds, in the belt's order, with the HP the net says it will save (<see cref="RunNet.Saves(RunView, PlayEnvironment)"/>),
/// none for a net of before the fight score.</summary>
public sealed record NetTest(RunView Run, double Value, string? Env = null, IReadOnlyList<double>? Saves = null);

/// <summary>V as <c>learn/v/export.py</c> writes it.</summary>
/// <param name="Floors">How many floors the head covers, from floor 0.</param>
/// <param name="Vocab">Each token table's ids; an id a table does not list reads as its entry 0.</param>
/// <param name="Onnx">The net as an ONNX graph, base64: inputs as <see cref="RunNet"/> feeds them, outputs the hazards
/// and, since the fight score (plans/2026-11-21-1604-fight-score.md), each asked potion's f.</param>
/// <param name="State">The run's numbers the net reads, by name, with the mean and std that standardize them.</param>
public sealed record NetFile(
    string Name, int Floors, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab, string Onnx,
    IReadOnlyList<NetTest> Tests, NetState State);

/// <summary>The run's numbers the net reads (of <see cref="RunNet.StateNames"/>), and how they are standardized.</summary>
public sealed record NetState(IReadOnlyList<string> Names, IReadOnlyList<double> Mean, IReadOnlyList<double> Std);

/// <summary>A card as the net reads it: its id, upgrade level (capped at <see cref="RunNet.MaxUp"/>), modifiers (its
/// enchantment and affliction first, as <paramref name="Amounts"/>), its enchantment's and affliction's amounts / 10, and
/// its damage / 20, its block / 20 and how far its other numbers have grown / 10 (Show Off's Aim).</summary>
public sealed record CardToken(string Id, int Up, IReadOnlyList<string> Modifiers, IReadOnlyList<double> Amounts, double Damage, double Block, double Grown);

/// <summary>
/// A run as V reads it, but for its numbers (<see cref="RunNet.State"/>): ids, and numbers
/// scaled and capped. <see cref="RunNet.Tokens"/> is the one place a run is turned into them; <see cref="RunNet"/> looks
/// the ids up in its net file's tables and feeds the graph, and the trainer reads the same tokens as JSON
/// (<see cref="Write"/>) and does the same.
/// </summary>
/// <param name="Relics">Each relic's id, with ":used" once used up.</param>
/// <param name="RelicValues">Each relic's numbers, as the combat net reads them (<see cref="CombatTokens.RelicValuesOf"/>).</param>
/// <param name="RelicStatus">Each relic's status (<see cref="CombatTokens.RelicStatusOf"/>).</param>
/// <param name="Held">The cards the relics keep (Pael's Tooth's), by id.</param>
/// <param name="Context">The character, the act, the boss(es), the act's ancient, the game mode and dominion, and the
/// environment (<see cref="CombatTokens.EnvTokens"/>).</param>
/// <param name="Map">The map points still reachable (<see cref="RunNet.MapTokens"/>).</param>
/// <param name="Pending">The event option picked and not yet played out (<see cref="RunView.Pending"/>); null for none.</param>
public sealed record RunTokens(
    IReadOnlyList<CardToken> Cards, IReadOnlyList<string> Relics, IReadOnlyList<IReadOnlyList<double>> RelicValues, IReadOnlyList<string> RelicStatus,
    IReadOnlyList<string> Held, IReadOnlyList<string> Potions, IReadOnlyList<string> Context, IReadOnlyList<string> Map, PendingTokens? Pending = null)
{
    /// <summary>The tokens as the trainer reads them (<c>learn/v/net.py</c>'s Batch): an object by the names above in snake
    /// case, "pending" only when there is one; a card as <see cref="Write(Utf8JsonWriter, CardToken)"/> writes it.</summary>
    public void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteStartArray("cards");
        foreach (var c in Cards) Write(json, c);
        json.WriteEndArray();
        json.WritePropertyName("relics");
        TokenJson.Strings(json, Relics);
        json.WritePropertyName("relic_values");
        TokenJson.Rows(json, RelicValues);
        json.WritePropertyName("relic_status");
        TokenJson.Strings(json, RelicStatus);
        json.WritePropertyName("held");
        TokenJson.Strings(json, Held);
        json.WritePropertyName("potions");
        TokenJson.Strings(json, Potions);
        json.WritePropertyName("context");
        TokenJson.Strings(json, Context);
        json.WritePropertyName("map");
        TokenJson.Strings(json, Map);
        if (Pending != null)
        {
            json.WritePropertyName("pending");
            Pending.Write(json);
        }
        json.WriteEndObject();
    }

    /// <summary>A card as the trainer reads it: an array of its id, upgrade level, modifiers, amounts, and its damage,
    /// block and growth as one array.</summary>
    public static void Write(Utf8JsonWriter json, CardToken c)
    {
        json.WriteStartArray();
        json.WriteStringValue(c.Id);
        json.WriteNumberValue(c.Up);
        TokenJson.Strings(json, c.Modifiers);
        TokenJson.Numbers(json, c.Amounts);
        TokenJson.Numbers(json, [c.Damage, c.Block, c.Grown]);
        json.WriteEndArray();
    }
}

/// <summary>
/// An event option picked and not yet played out (<see cref="RunView.Pending"/>), as V reads it: everything its button
/// shows. Its key; the cards it names, each as a card of the deck reads, and whether it is one of the player's own;
/// the relics, each as a relic of the run reads (its numbers and status), and whether the player holds it; the potions,
/// and whether the belt holds one; and its numbers by "EVENT.Name", each its value / 20, capped at ±20.
/// </summary>
public sealed record PendingTokens(
    string Key, IReadOnlyList<CardToken> Cards, IReadOnlyList<double> CardsOwned,
    IReadOnlyList<string> Relics, IReadOnlyList<IReadOnlyList<double>> RelicValues, IReadOnlyList<string> RelicStatus, IReadOnlyList<double> RelicsOwned,
    IReadOnlyList<string> Potions, IReadOnlyList<double> PotionsOwned, IReadOnlyList<string> Numbers, IReadOnlyList<double> NumberValues)
{
    /// <summary>An event's numbers, scaled, are capped at ±this: gold up to 400 reads apart.</summary>
    public const double NumberCap = 20;

    /// <summary>The tokens as the trainer reads them: an object by the names above in snake case, a card as in
    /// <see cref="RunTokens.Write(Utf8JsonWriter, CardToken)"/>.</summary>
    public void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteString("key", Key);
        json.WriteStartArray("cards");
        foreach (var c in Cards) RunTokens.Write(json, c);
        json.WriteEndArray();
        json.WritePropertyName("cards_owned");
        TokenJson.Numbers(json, CardsOwned);
        json.WritePropertyName("relics");
        TokenJson.Strings(json, Relics);
        json.WritePropertyName("relic_values");
        TokenJson.Rows(json, RelicValues);
        json.WritePropertyName("relic_status");
        TokenJson.Strings(json, RelicStatus);
        json.WritePropertyName("relics_owned");
        TokenJson.Numbers(json, RelicsOwned);
        json.WritePropertyName("potions");
        TokenJson.Strings(json, Potions);
        json.WritePropertyName("potions_owned");
        TokenJson.Numbers(json, PotionsOwned);
        json.WritePropertyName("numbers");
        TokenJson.Strings(json, Numbers);
        json.WritePropertyName("number_values");
        TokenJson.Numbers(json, NumberValues);
        json.WriteEndObject();
    }
}

/// <summary>
/// V, run by ONNX Runtime: a net over the run's cards, relics (with their numbers and
/// statuses, as the combat net reads them, and the cards they keep), potions and context, the map points still reachable
/// and the run's numbers (<see cref="NetFile.State"/>).
/// The graph gives one hazard per floor: the chance to die on that floor if still alive there. The value is the chance to
/// survive every floor from the current one on, the chance to win. This class turns a run into its tokens and numbers
/// (<see cref="Tokens"/>, <see cref="State"/>: the one reading of a run, the trainer's too) and feeds them to the graph.
/// </summary>
public sealed class RunNet : IRunValue, IDisposable
{
    /// <summary>The environment a <see cref="RunNet"/> reads its runs in when not told (<see cref="In"/>): SL's, which
    /// marks none.</summary>
    private const PlayEnvironment Plain = PlayEnvironment.SL;

    /// <summary>Upgrade levels above this read as this one.</summary>
    public const int MaxUp = 3;

    /// <summary>A card's numbers, scaled, are capped at ±Cap.</summary>
    public const double Cap = 10;

    private readonly NetFile _file;
    private readonly Dictionary<string, Dictionary<string, int>> _index;
    private readonly InferenceSession _session;

    /// <summary>Whether the graph gives each asked potion's f (<see cref="Saves(RunView, PlayEnvironment)"/>): a net of
    /// the fight score. One of before is still read until the champion is one of after; then this goes.</summary>
    private readonly bool _saves;

    private RunNet(NetFile file)
    {
        _file = file;
        _index = file.Vocab.ToDictionary(t => t.Key, t => t.Value.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i));
        // One thread per call: the sim runs many processes, and a macro decision values a handful of small inputs.
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(Convert.FromBase64String(file.Onnx), options);
        _saves = _session.InputMetadata.ContainsKey("asked");
    }

    public string Name => _file.Name;

    /// <summary>The net of a file on disk.</summary>
    public static RunNet Read(string path) => Load(ProtocolJson.Deserialize<NetFile>(File.ReadAllText(path)));

    /// <summary>The net of a file, once it gives every test run the trainer's value.</summary>
    public static RunNet Load(NetFile file)
    {
        var net = new RunNet(file);
        foreach (var test in file.Tests)
        {
            double value = net.Value(test.Run, test.Env is { } env ? Enum.Parse<PlayEnvironment>(env) : Plain);
            // Relative: a "win" net's values are mostly below 0.01. The 1e-6 is the floats' noise on the smallest of them.
            if (Math.Abs(value - test.Value) > Math.Max(1e-3 * Math.Abs(test.Value), 1e-6))
            {
                net.Dispose();
                throw new InvalidDataException($"net {file.Name} gives {value} for a test run the trainer valued at {test.Value}");
            }
            var saves = net.Saves(test.Run, test.Env is { } e ? Enum.Parse<PlayEnvironment>(e) : Plain);
            if ((saves == null) != (test.Saves == null) || saves != null && (saves.Count != test.Saves!.Count
                || saves.Zip(test.Saves).Any(p => Math.Abs(p.First - p.Second) > Math.Max(1e-3 * Math.Abs(p.Second), 1e-3))))
            {
                net.Dispose();
                throw new InvalidDataException($"net {file.Name} does not give a test run's potions the HP the trainer said they save");
            }
        }
        return net;
    }

    /// <summary>V of a run in SL (<see cref="In"/> for another environment).</summary>
    public double Value(RunView run) => Value(run, Plain);

    /// <summary>Each potion the run holds, in the belt's order, with f in SL: the HP the net says it will save.</summary>
    public IReadOnlyList<double>? Saves(RunView run) => Saves(run, Plain);

    /// <summary>This V reading every run in <paramref name="env"/>: NoSL's runs carry its mark (<see cref="CombatTokens.EnvTokens"/>).</summary>
    public IRunValue In(PlayEnvironment env) => env == Plain ? this : new Bound(this, env);

    private sealed class Bound(RunNet net, PlayEnvironment env) : IRunValue
    {
        public string Name => net.Name;

        public double Value(RunView run) => net.Value(run, env);

        public IReadOnlyList<double>? Saves(RunView run) => net.Saves(run, env);

        public IRunValue In(PlayEnvironment other) => net.In(other);
    }

    public double Value(RunView run, PlayEnvironment env)
    {
        var hazards = Graph(run, env, false).Hazards;
        int from = Math.Clamp(run.Floor, 0, _file.Floors - 1);
        double alive = 1;
        for (int g = from; g < _file.Floors; g++) alive *= 1 - hazards[g];
        return alive;
    }

    /// <summary>
    /// Each potion <paramref name="run"/> holds, in the belt's order, with f in <paramref name="env"/>: the HP the net
    /// says it will save in the fight where it is drunk (plans/2026-11-21-1604-fight-score.md, 2.3). The net reads no HP
    /// for it, so a run's f is the same at any HP. None from a net of before the fight score.
    /// </summary>
    public IReadOnlyList<double>? Saves(RunView run, PlayEnvironment env) => _saves ? [.. Graph(run, env, true).Saves] : null;

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// The net's tokens of a run: each card with its modifiers (enchantment, affliction, keywords, what Tinker Time made
    /// of it), its modifiers' amounts, its damage (Osty's for an Osty attack), block and growth, which only its own id's row
    /// reads; each relic, with ":used" once used up, its numbers and its status; each potion; the context: character,
    /// act, boss(es), the act's ancient, in the Dread mode the mode and the dominion now on the run, and the environment
    /// <paramref name="env"/>'s mark (NoSL's; SL has none); the map (<see cref="MapTokens"/>); the cards the relics keep
    /// (Pael's Tooth's); and an event option picked and not yet played out (<see cref="PendingOf"/>). Nothing is computed
    /// from the deck: what matters in it is the net's to learn.
    /// </summary>
    public static RunTokens Tokens(RunView run, PlayEnvironment env)
    {
        var context = new List<string> { "character:" + run.Character, "act:" + run.ActId, "boss:" + run.Map.Boss };
        if (run.Map.SecondBoss != null) context.Add("boss:" + run.Map.SecondBoss);
        if (run.Map.Ancient != null) context.Add("ancient:" + run.Map.Ancient);
        context.AddRange(CombatTokens.ModeTokens(run.Mode, run.Dominion));
        context.AddRange(CombatTokens.EnvTokens(env));
        return new(
            [.. run.Deck.Select(Card)],
            [.. run.Relics.Select(CombatTokens.RelicId)],
            [.. run.Relics.Select(CombatTokens.RelicValuesOf)],
            [.. run.Relics.Select(CombatTokens.RelicStatusOf)],
            [.. run.Relics.SelectMany(r => r.Cards ?? [])],
            [.. run.Potions.OfType<string>()],
            context,
            MapTokens(run.Map),
            run.Pending is { } pending ? PendingOf(pending) : null);
    }

    /// <summary>
    /// An event option's button as V reads it: its key; each card it names, read as a card of the deck is, and whether
    /// it is the player's own; each relic, read as a relic of the run is, and whether the player holds it; each potion,
    /// and whether the belt holds one; each number it shows, by "EVENT.Name", its value / 20 capped at ±20.
    /// </summary>
    public static PendingTokens PendingOf(EventOptionView option)
    {
        var cards = option.Cards ?? [];
        var relics = option.Relics ?? [];
        var potions = option.Potions ?? [];
        var numbers = option.Numbers ?? new Dictionary<string, decimal>();
        return new(option.Key,
            [.. cards.Select(c => Card(c.Card))], [.. cards.Select(c => c.Owned == true ? 1.0 : 0.0)],
            [.. relics.Select(r => CombatTokens.RelicId(r.Relic))], [.. relics.Select(r => CombatTokens.RelicValuesOf(r.Relic))],
            [.. relics.Select(r => CombatTokens.RelicStatusOf(r.Relic))], [.. relics.Select(r => r.Owned == true ? 1.0 : 0.0)],
            [.. potions.Select(p => p.Id)], [.. potions.Select(p => p.Owned == true ? 1.0 : 0.0)],
            [.. numbers.Keys.Order(StringComparer.Ordinal)],
            [.. numbers.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Math.Clamp((double)kv.Value / 20.0, -PendingTokens.NumberCap, PendingTokens.NumberCap))]);
    }

    /// <summary>A card as V reads it: its id, upgrade level, modifiers and their amounts, damage, block and growth.</summary>
    private static CardToken Card(CardView card)
    {
        var vars = card.Vars ?? new Dictionary<string, decimal>();
        decimal damage = vars.TryGetValue("Damage", out var d) ? d : vars.GetValueOrDefault("OstyDamage");
        return new(card.Id, Math.Min(card.Up ?? 0, MaxUp), Modifiers(card),
            [.. new[] { card.Enchant, card.Affliction }.OfType<ModifierView>().Select(m => Capped(m.Amount / 10.0))],
            Capped((double)damage / 20.0), Capped((double)vars.GetValueOrDefault("Block") / 20.0), Capped((double)(card.Grown ?? 0) / 10.0));
    }

    private static IReadOnlyList<string> Modifiers(CardView card)
    {
        var ids = new List<string>();
        if (card.Enchant != null) ids.Add("enchant:" + card.Enchant.Id);
        if (card.Affliction != null) ids.Add("affliction:" + card.Affliction.Id);
        ids.AddRange((card.Keywords ?? []).Select(k => "keyword:" + k));
        if (card.Tinker != null) ids.AddRange(["tinker:" + card.Tinker.Type, "rider:" + card.Tinker.Rider]);
        return ids;
    }

    /// <summary>
    /// Each map point a path leads to from where the player is (the last visited point, or the start before any), the
    /// player's own left out, sorted by (row, col): its type and how many floors ahead it is ("Elite+3"), ":quest" if a
    /// quest marks it. In a map move's afterstate (<see cref="MapView.Entering"/>) the point being entered comes first
    /// ("Elite+0"), so that options with the same points after them differ. The mark never stands for a room already
    /// played: a run logged in a room comes after its combat, and a run that died there logs none, so a net that read the
    /// type of the room it is in would take entering an elite for having beaten it.
    /// </summary>
    public static IReadOnlyList<string> MapTokens(MapView map)
    {
        var points = map.Points.ToDictionary(p => (p.Col, p.Row));
        var here = Here(map, points);
        return [.. (map.Entering == true ? Reachable(points, here).Prepend(here) : Reachable(points, here))
            .Select(k => $"{points[k].Type}+{k.Row - here.Row}" + (points[k].Quest == true ? ":quest" : ""))
        ];
    }

    /// <summary>Where the player is: the last visited point, or the start (the first by row, col) before any.</summary>
    private static (int Col, int Row) Here(MapView map, Dictionary<(int Col, int Row), MapPointView> points) =>
        map.Visited.Count > 0 ? (map.Visited[^1].Col, map.Visited[^1].Row) : points.Keys.MinBy(k => (k.Row, k.Col));

    /// <summary>The points a path leads to from <paramref name="here"/>, itself left out, sorted by (row, col).</summary>
    private static List<(int Col, int Row)> Reachable(Dictionary<(int Col, int Row), MapPointView> points, (int Col, int Row) here)
    {
        var seen = new HashSet<(int Col, int Row)>();
        var todo = new Stack<(int, int)>([here]);
        while (todo.Count > 0)
            foreach (var next in points[todo.Pop()].Next)
                if (points.ContainsKey((next.Col, next.Row)) && seen.Add((next.Col, next.Row)))
                    todo.Push((next.Col, next.Row));
        return [.. seen.OrderBy(k => k.Row).ThenBy(k => k.Col)];
    }

    /// <summary>The run's numbers a net may read, by name: floor, HP, max HP, gold, ascension, potion slots, and the
    /// game's rule, per act from 0, whether its plain monster fights are won at their start. Raw: a net file standardizes them.</summary>
    public static readonly IReadOnlyList<string> StateNames =
        ["floor", "hp", "max_hp", "gold", "ascension", "potion_slots", "auto_win_0", "auto_win_1", "auto_win_2"];

    /// <summary>The run's number of this name (<see cref="StateNames"/>).</summary>
    public static double State(RunView run, string name) => name switch
    {
        "floor" => run.Floor,
        "hp" => run.Hp,
        "max_hp" => run.MaxHp,
        "gold" => run.Gold,
        "ascension" => run.Ascension,
        "potion_slots" => run.Potions.Count,
        // The game's rule, per act from 0: whether its plain monster fights are won at their start.
        _ when name.StartsWith("auto_win_", StringComparison.Ordinal) && int.TryParse(name["auto_win_".Length..], out int act) =>
            run.AutoWin?.Contains(act) == true ? 1 : 0,
        _ => throw new InvalidDataException($"a net reads the run's {name}, which RunNet does not know"),
    };

    /// <summary>
    /// A run as the trainer reads it in <paramref name="env"/>: its tokens and its numbers by name,
    /// <c>{"tokens": ..., "state": {NAME: number}}</c>.
    /// </summary>
    public static void Write(Utf8JsonWriter json, RunView run, PlayEnvironment env)
    {
        json.WriteStartObject();
        json.WritePropertyName("tokens");
        Tokens(run, env).Write(json);
        json.WriteStartObject("state");
        foreach (string name in StateNames) json.WriteNumber(name, State(run, name));
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>Per floor, the chance to die there if still alive there, and with <paramref name="asked"/> each held
    /// potion's f: the graph on a batch of one run.</summary>
    private (float[] Hazards, float[] Saves) Graph(RunView run, PlayEnvironment env, bool asked)
    {
        var tokens = Tokens(run, env);
        var inputs = new List<NamedOnnxValue>(CardInputs("", tokens.Cards));
        inputs.AddRange(Bag("relics", "relic_mask", tokens.Relics));
        inputs.AddRange(Bag("potions", "potion_mask", tokens.Potions));
        inputs.AddRange(Bag("context", "context_mask", tokens.Context));
        inputs.AddRange(Bag("map", "map_mask", tokens.Map));
        var values = new DenseTensor<float>([1, _file.State.Names.Count]);
        for (int i = 0; i < _file.State.Names.Count; i++)
            values[0, i] = (float)((State(run, _file.State.Names[i]) - _file.State.Mean[i]) / _file.State.Std[i]);
        inputs.Add(NamedOnnxValue.CreateFromTensor("state", values));
        inputs.AddRange(CombatNet.RelicInputs(tokens.RelicValues, tokens.RelicStatus, status => Index("relic_status", status)));
        inputs.AddRange(Bag("cards", "held", "held_mask", tokens.Held));
        inputs.AddRange(PendingInputs(tokens.Pending));
        if (_saves) inputs.Add(Bag("potions", "asked", "asked_mask", asked ? tokens.Potions : [])[0]);
        using var results = _session.Run(inputs);
        float[] hazards = [.. results[0].AsEnumerable<float>()];
        return (hazards, asked ? [.. results[1].AsEnumerable<float>().Take(tokens.Potions.Count)] : []);
    }

    /// <summary>Cards as the graph takes them, by the input names with <paramref name="prefix"/>: their ids, upgrade
    /// levels, mask, modifiers with their mask and amounts, and numbers (damage, block, growth).</summary>
    private List<NamedOnnxValue> CardInputs(string prefix, IReadOnlyList<CardToken> tokens)
    {
        // Every length is at least 1, its padding masked out, as in training.
        int n = Math.Max(1, tokens.Count);
        int m = Math.Max(1, tokens.Select(c => c.Modifiers.Count).DefaultIfEmpty(0).Max());
        var cards = new DenseTensor<long>([1, n]);
        var ups = new DenseTensor<float>([1, n]);
        var cardMask = new DenseTensor<float>([1, n]);
        var mods = new DenseTensor<long>([1, n, m]);
        var modMask = new DenseTensor<float>([1, n, m]);
        var modAmounts = new DenseTensor<float>([1, n, m]);
        var numbers = new DenseTensor<float>([1, n, 3]);
        for (int k = 0; k < tokens.Count; k++)
        {
            var card = tokens[k];
            cards[0, k] = Index("cards", card.Id);
            ups[0, k] = card.Up;
            cardMask[0, k] = 1;
            for (int j = 0; j < card.Modifiers.Count; j++)
            {
                mods[0, k, j] = Index("modifiers", card.Modifiers[j]);
                modMask[0, k, j] = 1;
            }
            for (int j = 0; j < card.Amounts.Count; j++) modAmounts[0, k, j] = (float)card.Amounts[j];
            numbers[0, k, 0] = (float)card.Damage;
            numbers[0, k, 1] = (float)card.Block;
            numbers[0, k, 2] = (float)card.Grown;
        }
        return
        [
            NamedOnnxValue.CreateFromTensor(prefix + "cards", cards), NamedOnnxValue.CreateFromTensor(prefix + "ups", ups),
            NamedOnnxValue.CreateFromTensor(prefix + "card_mask", cardMask), NamedOnnxValue.CreateFromTensor(prefix + "mods", mods),
            NamedOnnxValue.CreateFromTensor(prefix + "mod_mask", modMask), NamedOnnxValue.CreateFromTensor(prefix + "mod_amounts", modAmounts),
            NamedOnnxValue.CreateFromTensor(prefix + "numbers", numbers),
        ];
    }

    /// <summary>The inputs of an event option picked and not yet played out (<see cref="PendingTokens"/>), all empty for
    /// none: its key (the table's entry 0 for a key it does not list, a row of zeros), its cards as the deck's with
    /// whether each is the player's own, its relics as the run's with whether each is held, its potions with whether
    /// each is held, and its numbers by their rows with their values.</summary>
    private List<NamedOnnxValue> PendingInputs(PendingTokens? pending)
    {
        var inputs = new List<NamedOnnxValue>(Bag("pending", "pending_mask", pending != null ? [pending.Key] : []));
        inputs.AddRange(CardInputs(Pend, pending?.Cards ?? []));
        inputs.Add(Row(Pend + "cards_owned", pending?.CardsOwned ?? []));
        inputs.AddRange(Bag("relics", Pend + "relics", Pend + "relic_mask", pending?.Relics ?? []));
        inputs.AddRange(CombatNet.RelicInputs(pending?.RelicValues ?? [], pending?.RelicStatus ?? [], status => Index("relic_status", status), Pend));
        inputs.Add(Row(Pend + "relics_owned", pending?.RelicsOwned ?? []));
        inputs.AddRange(Bag("potions", Pend + "potions", Pend + "potion_mask", pending?.Potions ?? []));
        inputs.Add(Row(Pend + "potions_owned", pending?.PotionsOwned ?? []));
        inputs.AddRange(Bag("event_vars", Pend + "vars", Pend + "var_mask", pending?.Numbers ?? []));
        inputs.Add(Row(Pend + "var_values", pending?.NumberValues ?? []));
        return inputs;
    }

    /// <summary>The prefix of the graph's inputs of an event option picked and not yet played out.</summary>
    private const string Pend = "pend_";

    /// <summary>Numbers as the graph takes them: padded to at least 1, as their bag is.</summary>
    private static NamedOnnxValue Row(string input, IReadOnlyList<double> numbers)
    {
        var row = new DenseTensor<float>([1, Math.Max(1, numbers.Count)]);
        for (int k = 0; k < numbers.Count; k++) row[0, k] = (float)numbers[k];
        return NamedOnnxValue.CreateFromTensor(input, row);
    }

    /// <summary>A token table's ids as the graph takes them: padded to at least 1, with their mask.</summary>
    private NamedOnnxValue[] Bag(string table, string mask, IReadOnlyList<string> ids) => Bag(table, table, mask, ids);

    /// <summary>Ids of <paramref name="table"/> as the graph's input <paramref name="input"/>, padded to at least 1, with their mask.</summary>
    private NamedOnnxValue[] Bag(string table, string input, string mask, IReadOnlyList<string> ids)
    {
        int n = Math.Max(1, ids.Count);
        var index = new DenseTensor<long>([1, n]);
        var present = new DenseTensor<float>([1, n]);
        for (int k = 0; k < ids.Count; k++)
        {
            index[0, k] = Index(table, ids[k]);
            present[0, k] = 1;
        }
        return [NamedOnnxValue.CreateFromTensor(input, index), NamedOnnxValue.CreateFromTensor(mask, present)];
    }

    private static double Capped(double x) => Math.Clamp(x, -Cap, Cap);

    /// <summary>An id's entry in a token table; 0 for an id the table does not list.</summary>
    private long Index(string table, string id) => _index[table].GetValueOrDefault(id);
}
