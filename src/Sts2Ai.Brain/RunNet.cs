using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>A tensor as a net file holds it: its shape, and its numbers in row-major order.</summary>
public sealed record NetTensor(IReadOnlyList<int> Shape, float[] Data);

/// <summary>A run and the value the trainer computed for it, which loading checks (docs: learning §7, input parity).</summary>
public sealed record NetTest(RunView Run, double Value);

/// <summary>
/// V v2 as <c>learn/train_value.py</c> writes it (docs: learning §1).
/// </summary>
/// <param name="Objective">What <see cref="RunNet.Value"/> returns: "floor", the floor expected to be reached, or "win",
/// the chance to survive every floor.</param>
/// <param name="Floors">How many floors the head covers, from floor 0.</param>
/// <param name="SumScale">The factor on the sum of the cards' vectors, which goes in beside their mean.</param>
/// <param name="Vocab">Each token table's ids; an id a table does not list reads as its entry 0.</param>
/// <param name="Features">The features of V v0 (<see cref="RunValue.Features"/>) the net reads, in order, with the
/// <paramref name="Mean"/> and <paramref name="Std"/> that standardize them.</param>
/// <param name="Tensors">The token tables' embeddings (cards, card_up, modifiers, relics, potions, context), then the
/// Linear layers of phi (on each card), body and head, as PyTorch stores them: NAME.I.weight [out, in] and NAME.I.bias.</param>
public sealed record NetFile(
    string Name, string Objective, int Floors, double SumScale, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab,
    IReadOnlyList<string> Features, IReadOnlyList<double> Mean, IReadOnlyList<double> Std,
    IReadOnlyDictionary<string, NetTensor> Tensors, IReadOnlyList<NetTest> Tests);

/// <summary>A card as the net reads it: its id, upgrade level and the ids of its enchantment and affliction.</summary>
public sealed record CardToken(string Id, int Up, IReadOnlyList<string> Modifiers);

/// <summary>A run as the net reads it, besides V v0's features.</summary>
public sealed record RunTokens(IReadOnlyList<CardToken> Cards, IReadOnlyList<string> Relics, IReadOnlyList<string> Potions,
    IReadOnlyList<string> Context);

/// <summary>
/// V v2 (docs: learning §1): a net over the run's cards, relics, potions and context, and V v0's features. Each card's
/// vector (its id's, plus its upgrade level times its id's upgrade vector, plus its modifiers') goes through phi; the
/// deck is the mean and the scaled sum of the results. Relics, potions and context are sums of their vectors. Body and
/// head turn all of it into one logit per floor: the chance to die on that floor if still alive there. The value is
/// read from those chances from the current floor on (<see cref="NetFile.Objective"/>).
/// </summary>
public sealed class RunNet : IRunValue
{
    /// <summary>Upgrade levels above this read as this one.</summary>
    public const int MaxUp = 3;

    private readonly NetFile _file;
    private readonly Dictionary<string, Dictionary<string, int>> _index;
    private readonly Linear[] _phi, _body;
    private readonly Linear _head;

    private RunNet(NetFile file)
    {
        _file = file;
        _index = file.Vocab.ToDictionary(t => t.Key, t => t.Value.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i));
        _phi = Layers("phi");
        _body = Layers("body");
        _head = new Linear(file.Tensors["head.weight"], file.Tensors["head.bias"]);
    }

    public string Name => _file.Name;

    /// <summary>The net of a file, once it gives every test run the trainer's value.</summary>
    public static RunNet Load(NetFile file)
    {
        var net = new RunNet(file);
        foreach (var test in file.Tests)
        {
            double value = net.Value(test.Run);
            if (Math.Abs(value - test.Value) > 1e-3 * Math.Max(1, Math.Abs(test.Value)))
                throw new InvalidDataException($"net {file.Name} gives {value} for a test run the trainer valued at {test.Value}");
        }
        return net;
    }

    public double Value(RunView run)
    {
        var hazards = Hazards(run);
        int from = Math.Clamp(run.Floor, 0, _file.Floors - 1);
        double alive = 1, floor = from;
        for (int g = from; g < _file.Floors; g++)
        {
            alive *= 1 - hazards[g];
            floor += alive;
        }
        return _file.Objective == "win" ? alive : floor;
    }

    /// <summary>
    /// The net's tokens of a run, which <c>learn/train_value.py</c> reads the same way: each card; each relic, with
    /// ":used" once used up; each potion; and the context: character, act, boss(es) and the act's ancient.
    /// </summary>
    public static RunTokens Tokens(RunView run)
    {
        var context = new List<string> { "character:" + run.Character, "act:" + run.ActId, "boss:" + run.Map.Boss };
        if (run.Map.SecondBoss != null) context.Add("boss:" + run.Map.SecondBoss);
        if (run.Map.Ancient != null) context.Add("ancient:" + run.Map.Ancient);
        return new(
            run.Deck.Select(c => new CardToken(c.Id, c.Up ?? 0, Modifiers(c))).ToList(),
            run.Relics.Select(r => r.UsedUp == true ? r.Id + ":used" : r.Id).ToList(),
            run.Potions.OfType<string>().ToList(),
            context);

        static IReadOnlyList<string> Modifiers(CardView card)
        {
            var ids = new List<string>();
            if (card.Enchant != null) ids.Add("enchant:" + card.Enchant.Id);
            if (card.Affliction != null) ids.Add("affliction:" + card.Affliction.Id);
            return ids;
        }
    }

    /// <summary>Per floor, the chance to die there if still alive there.</summary>
    private float[] Hazards(RunView run)
    {
        var tokens = Tokens(run);
        int width = _phi[^1].Out;
        var pool = new float[width];
        foreach (var card in tokens.Cards)
        {
            int id = Index("cards", card.Id);
            var x = Row("cards", id).ToArray();
            Add(x, Row("card_up", id), Math.Min(card.Up, MaxUp));
            foreach (var modifier in card.Modifiers) Add(x, Row("modifiers", Index("modifiers", modifier)), 1);
            Add(pool, Forward(_phi, x), 1);
        }
        var input = new List<float>();
        input.AddRange(pool.Select(v => tokens.Cards.Count == 0 ? 0 : v / tokens.Cards.Count));
        input.AddRange(pool.Select(v => (float)(v * _file.SumScale)));
        input.AddRange(Sum("relics", tokens.Relics));
        input.AddRange(Sum("potions", tokens.Potions));
        input.AddRange(Sum("context", tokens.Context));
        var features = RunValue.Features(run);
        for (int i = 0; i < _file.Features.Count; i++)
            input.Add((float)((features.GetValueOrDefault(_file.Features[i]) - _file.Mean[i]) / _file.Std[i]));
        var logits = _head.Apply(Forward(_body, input.ToArray()));
        return logits.Select(l => 1 / (1 + MathF.Exp(-l))).ToArray();
    }

    private int Index(string table, string id) => _index[table].GetValueOrDefault(id);

    /// <summary>Entry <paramref name="index"/> of a token table's embedding.</summary>
    private ReadOnlySpan<float> Row(string table, int index)
    {
        var tensor = _file.Tensors[table];
        int dim = tensor.Shape[1];
        return tensor.Data.AsSpan(index * dim, dim);
    }

    private float[] Sum(string table, IReadOnlyList<string> ids)
    {
        var sum = new float[_file.Tensors[table].Shape[1]];
        foreach (var id in ids) Add(sum, Row(table, Index(table, id)), 1);
        return sum;
    }

    private static void Add(float[] to, ReadOnlySpan<float> x, float times)
    {
        for (int i = 0; i < to.Length; i++) to[i] += times * x[i];
    }

    /// <summary>Each layer, then ReLU.</summary>
    private static float[] Forward(Linear[] layers, float[] x)
    {
        foreach (var layer in layers)
        {
            x = layer.Apply(x);
            for (int i = 0; i < x.Length; i++) x[i] = Math.Max(0, x[i]);
        }
        return x;
    }

    private Linear[] Layers(string name) =>
        Enumerable.Range(0, int.MaxValue).TakeWhile(i => _file.Tensors.ContainsKey($"{name}.{i}.weight"))
            .Select(i => new Linear(_file.Tensors[$"{name}.{i}.weight"], _file.Tensors[$"{name}.{i}.bias"])).ToArray();

    /// <summary>y = W x + b, with W as PyTorch stores it: [out, in], row-major.</summary>
    private sealed class Linear(NetTensor weight, NetTensor bias)
    {
        public int Out => weight.Shape[0];

        public float[] Apply(float[] x)
        {
            int inputs = weight.Shape[1];
            if (x.Length != inputs) throw new InvalidDataException($"a layer of {inputs} inputs given {x.Length}");
            var y = new float[Out];
            var w = weight.Data;
            for (int o = 0; o < y.Length; o++)
            {
                float sum = bias.Data[o];
                int row = o * inputs;
                for (int i = 0; i < inputs; i++) sum += w[row + i] * x[i];
                y[o] = sum;
            }
            return y;
        }
    }
}
