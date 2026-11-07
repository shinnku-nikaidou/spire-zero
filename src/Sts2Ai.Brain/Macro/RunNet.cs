using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>A run and the value the trainer computed for it, which loading checks (docs: learning/reference §7, input parity).</summary>
public sealed record NetTest(RunView Run, double Value);

/// <summary>V as <c>learn/v/export.py</c> writes it.</summary>
/// <param name="Floors">How many floors the head covers, from floor 0.</param>
/// <param name="Vocab">Each token table's ids; an id a table does not list reads as its entry 0.</param>
/// <param name="Onnx">The net as an ONNX graph, base64: inputs as <see cref="RunNet"/> feeds them, output the hazards.</param>
/// <param name="State">The run's numbers the net reads, by name, with the mean and std that standardize them.</param>
public sealed record NetFile(
    string Name, int Floors, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab, string Onnx,
    IReadOnlyList<NetTest> Tests, NetState State);

/// <summary>The run's numbers the net reads (<c>learn/v/encode.py</c>'s STATE), and how they are standardized.</summary>
public sealed record NetState(IReadOnlyList<string> Names, IReadOnlyList<double> Mean, IReadOnlyList<double> Std);

/// <summary>A card as the net reads it: its id, upgrade level, modifiers (its enchantment and affliction first, as
/// <paramref name="Amounts"/>), its enchantment's and affliction's amounts / 10, and its damage / 20 and block / 20.</summary>
public sealed record CardToken(string Id, int Up, IReadOnlyList<string> Modifiers, IReadOnlyList<double> Amounts, double Damage, double Block);

/// <summary>A run's ids as the net reads them; <paramref name="Held"/>, the cards its relics keep, by id.</summary>
public sealed record RunTokens(IReadOnlyList<CardToken> Cards, IReadOnlyList<string> Relics, IReadOnlyList<string> Potions,
    IReadOnlyList<string> Context, IReadOnlyList<string> Map, IReadOnlyList<string> Held);

/// <summary>
/// V (docs: macro-brain/network), run by ONNX Runtime: a net over the run's cards, relics (with their numbers and
/// statuses, as the combat net reads them, and the cards they keep), potions and context, the map points still reachable
/// and the run's numbers (<see cref="NetFile.State"/>), read as <c>learn/v/encode.py</c> reads them.
/// The graph gives one hazard per floor: the chance to die on that floor if still alive there. The value is the chance to
/// survive every floor from the current one on, the chance to win. This class only turns a run into the graph's inputs.
/// </summary>
public sealed class RunNet : IRunValue, IDisposable
{
    /// <summary>Upgrade levels above this read as this one.</summary>
    public const int MaxUp = 3;

    /// <summary>A card's numbers, scaled, are capped at ±Cap.</summary>
    public const double Cap = 10;

    private readonly NetFile _file;
    private readonly Dictionary<string, Dictionary<string, int>> _index;
    private readonly InferenceSession _session;

    private RunNet(NetFile file)
    {
        _file = file;
        _index = file.Vocab.ToDictionary(t => t.Key, t => t.Value.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i));
        // One thread per call: the sim runs many processes, and a macro decision values a handful of small inputs.
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(Convert.FromBase64String(file.Onnx), options);
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
            double value = net.Value(test.Run);
            // Relative: a "win" net's values are mostly below 0.01. The 1e-6 is the floats' noise on the smallest of them.
            if (Math.Abs(value - test.Value) > Math.Max(1e-3 * Math.Abs(test.Value), 1e-6))
            {
                net.Dispose();
                throw new InvalidDataException($"net {file.Name} gives {value} for a test run the trainer valued at {test.Value}");
            }
        }
        return net;
    }

    public double Value(RunView run)
    {
        var hazards = Hazards(run);
        int from = Math.Clamp(run.Floor, 0, _file.Floors - 1);
        double alive = 1;
        for (int g = from; g < _file.Floors; g++) alive *= 1 - hazards[g];
        return alive;
    }

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// The net's tokens of a run, which <c>learn/v/encode.py</c> reads the same way: each card with its modifiers
    /// (enchantment, affliction, keywords, what Tinker Time made of it), its modifiers' amounts, its damage (Osty's for an
    /// Osty attack) and block, which only its own id's row reads; each relic, with ":used"
    /// once used up; each potion; the context: character, act, boss(es) and the act's ancient; the map (<see cref="MapTokens"/>);
    /// and the cards the relics keep (Pael's Tooth's). The relics' numbers and statuses are fed apart (<see cref="CombatNet.RelicInputs"/>).
    /// </summary>
    public static RunTokens Tokens(RunView run)
    {
        var context = new List<string> { "character:" + run.Character, "act:" + run.ActId, "boss:" + run.Map.Boss };
        if (run.Map.SecondBoss != null) context.Add("boss:" + run.Map.SecondBoss);
        if (run.Map.Ancient != null) context.Add("ancient:" + run.Map.Ancient);
        return new(
            [.. run.Deck.Select(Card)],
            [.. run.Relics.Select(r => r.UsedUp == true ? r.Id + ":used" : r.Id)],
            [.. run.Potions.OfType<string>()],
            context,
            MapTokens(run.Map),
            [.. run.Relics.SelectMany(r => r.Cards ?? [])]);

        static CardToken Card(CardView card)
        {
            var vars = card.Vars ?? new Dictionary<string, decimal>();
            decimal damage = vars.TryGetValue("Damage", out var d) ? d : vars.GetValueOrDefault("OstyDamage");
            return new(card.Id, card.Up ?? 0, Modifiers(card),
                [.. new[] { card.Enchant, card.Affliction }.OfType<ModifierView>().Select(m => Capped(m.Amount / 10.0))],
                Capped((double)damage / 20.0), Capped((double)vars.GetValueOrDefault("Block") / 20.0));
        }

        static IReadOnlyList<string> Modifiers(CardView card)
        {
            var ids = new List<string>();
            if (card.Enchant != null) ids.Add("enchant:" + card.Enchant.Id);
            if (card.Affliction != null) ids.Add("affliction:" + card.Affliction.Id);
            ids.AddRange((card.Keywords ?? []).Select(k => "keyword:" + k));
            if (card.Tinker != null) ids.AddRange(["tinker:" + card.Tinker.Type, "rider:" + card.Tinker.Rider]);
            return ids;
        }
    }

    /// <summary>
    /// Each map point a path leads to from where the player is (the last visited point, or the start before any), the
    /// player's own left out, sorted by (row, col): its type and how many floors ahead it is ("Elite+3"), ":quest" if a
    /// quest marks it.
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

    /// <summary>The run's number the net reads by this name (<c>learn/v/encode.py</c>'s state).</summary>
    private static double StateValue(RunView run, string name) => name switch
    {
        "floor" => run.Floor,
        "hp" => run.Hp,
        "max_hp" => run.MaxHp,
        "gold" => run.Gold,
        "ascension" => run.Ascension,
        "potion_slots" => run.Potions.Count,
        _ => throw new InvalidDataException($"a net reads the run's {name}, which RunNet does not know"),
    };

    /// <summary>Per floor, the chance to die there if still alive there: the graph on a batch of one run.</summary>
    private float[] Hazards(RunView run)
    {
        var tokens = Tokens(run);
        // Every length is at least 1, its padding masked out, as in training.
        int n = Math.Max(1, tokens.Cards.Count);
        int m = Math.Max(1, tokens.Cards.Select(c => c.Modifiers.Count).DefaultIfEmpty(0).Max());
        var cards = new DenseTensor<long>([1, n]);
        var ups = new DenseTensor<float>([1, n]);
        var cardMask = new DenseTensor<float>([1, n]);
        var mods = new DenseTensor<long>([1, n, m]);
        var modMask = new DenseTensor<float>([1, n, m]);
        var modAmounts = new DenseTensor<float>([1, n, m]);
        var numbers = new DenseTensor<float>([1, n, 2]);
        for (int k = 0; k < tokens.Cards.Count; k++)
        {
            var card = tokens.Cards[k];
            cards[0, k] = Index("cards", card.Id);
            ups[0, k] = Math.Min(card.Up, MaxUp);
            cardMask[0, k] = 1;
            for (int j = 0; j < card.Modifiers.Count; j++)
            {
                mods[0, k, j] = Index("modifiers", card.Modifiers[j]);
                modMask[0, k, j] = 1;
            }
            for (int j = 0; j < card.Amounts.Count; j++) modAmounts[0, k, j] = (float)card.Amounts[j];
            numbers[0, k, 0] = (float)card.Damage;
            numbers[0, k, 1] = (float)card.Block;
        }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("cards", cards), NamedOnnxValue.CreateFromTensor("ups", ups),
            NamedOnnxValue.CreateFromTensor("card_mask", cardMask), NamedOnnxValue.CreateFromTensor("mods", mods),
            NamedOnnxValue.CreateFromTensor("mod_mask", modMask), NamedOnnxValue.CreateFromTensor("mod_amounts", modAmounts),
            NamedOnnxValue.CreateFromTensor("numbers", numbers),
        };
        inputs.AddRange(Bag("relics", "relic_mask", tokens.Relics));
        inputs.AddRange(Bag("potions", "potion_mask", tokens.Potions));
        inputs.AddRange(Bag("context", "context_mask", tokens.Context));
        inputs.AddRange(Bag("map", "map_mask", tokens.Map));
        var values = new DenseTensor<float>([1, _file.State.Names.Count]);
        for (int i = 0; i < _file.State.Names.Count; i++)
            values[0, i] = (float)((StateValue(run, _file.State.Names[i]) - _file.State.Mean[i]) / _file.State.Std[i]);
        inputs.Add(NamedOnnxValue.CreateFromTensor("state", values));
        inputs.AddRange(CombatNet.RelicInputs(run.Relics, status => Index("relic_status", status)));
        inputs.AddRange(Bag("cards", "held", "held_mask", tokens.Held));
        using var results = _session.Run(inputs);
        return [.. results[0].AsEnumerable<float>()];
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
