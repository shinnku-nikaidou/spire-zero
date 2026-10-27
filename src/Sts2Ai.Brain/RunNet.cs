using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>A run and the value the trainer computed for it, which loading checks (docs: learning §7, input parity).</summary>
public sealed record NetTest(RunView Run, double Value);

/// <summary>
/// V v2 as <c>learn/train_value.py</c> writes it (docs: learning §1).
/// </summary>
/// <param name="Objective">What <see cref="RunNet.Value"/> returns: "floor", the floor expected to be reached, or "win",
/// the chance to survive every floor.</param>
/// <param name="Floors">How many floors the head covers, from floor 0.</param>
/// <param name="Vocab">Each token table's ids; an id a table does not list reads as its entry 0.</param>
/// <param name="Features">The features of V v0 (<see cref="RunValue.Features"/>) the net reads, in order, with the
/// <paramref name="Mean"/> and <paramref name="Std"/> that standardize them.</param>
/// <param name="Onnx">The net as an ONNX graph, base64: inputs as <see cref="RunNet"/> feeds them, output the hazards.</param>
public sealed record NetFile(
    string Name, string Objective, int Floors, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab,
    IReadOnlyList<string> Features, IReadOnlyList<double> Mean, IReadOnlyList<double> Std, string Onnx, IReadOnlyList<NetTest> Tests);

/// <summary>A card as the net reads it: its id, upgrade level and the ids of its enchantment and affliction.</summary>
public sealed record CardToken(string Id, int Up, IReadOnlyList<string> Modifiers);

/// <summary>A run as the net reads it, besides V v0's features.</summary>
public sealed record RunTokens(IReadOnlyList<CardToken> Cards, IReadOnlyList<string> Relics, IReadOnlyList<string> Potions,
    IReadOnlyList<string> Context);

/// <summary>
/// V v2 (docs: learning §1): a net over the run's cards, relics, potions and context, and V v0's features, run by ONNX
/// Runtime. The graph (<c>learn/train_value.py</c> defines it) gives one hazard per floor: the chance to die on that
/// floor if still alive there. The value is read from those chances from the current floor on
/// (<see cref="NetFile.Objective"/>). This class only turns a run into the graph's inputs.
/// </summary>
public sealed class RunNet : IRunValue, IDisposable
{
    /// <summary>Upgrade levels above this read as this one.</summary>
    public const int MaxUp = 3;

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

    /// <summary>The net of a file, once it gives every test run the trainer's value.</summary>
    public static RunNet Load(NetFile file)
    {
        var net = new RunNet(file);
        foreach (var test in file.Tests)
        {
            double value = net.Value(test.Run);
            if (Math.Abs(value - test.Value) > 1e-3 * Math.Max(1, Math.Abs(test.Value)))
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
        double alive = 1, floor = from;
        for (int g = from; g < _file.Floors; g++)
        {
            alive *= 1 - hazards[g];
            floor += alive;
        }
        return _file.Objective == "win" ? alive : floor;
    }

    public void Dispose() => _session.Dispose();

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

    /// <summary>Per floor, the chance to die there if still alive there: the graph on a batch of one run.</summary>
    private float[] Hazards(RunView run)
    {
        var tokens = Tokens(run);
        // Every length is at least 1, its padding masked out, as in training.
        int n = Math.Max(1, tokens.Cards.Count);
        var cards = new DenseTensor<long>([1, n]);
        var ups = new DenseTensor<float>([1, n]);
        var cardMask = new DenseTensor<float>([1, n]);
        var mods = new DenseTensor<long>([1, n, 2]);
        var modMask = new DenseTensor<float>([1, n, 2]);
        for (int k = 0; k < tokens.Cards.Count; k++)
        {
            var card = tokens.Cards[k];
            cards[0, k] = Index("cards", card.Id);
            ups[0, k] = Math.Min(card.Up, MaxUp);
            cardMask[0, k] = 1;
            for (int j = 0; j < Math.Min(2, card.Modifiers.Count); j++)
            {
                mods[0, k, j] = Index("modifiers", card.Modifiers[j]);
                modMask[0, k, j] = 1;
            }
        }
        var features = RunValue.Features(run);
        var scalars = new DenseTensor<float>([1, _file.Features.Count]);
        for (int i = 0; i < _file.Features.Count; i++)
            scalars[0, i] = (float)((features.GetValueOrDefault(_file.Features[i]) - _file.Mean[i]) / _file.Std[i]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("cards", cards), NamedOnnxValue.CreateFromTensor("ups", ups),
            NamedOnnxValue.CreateFromTensor("card_mask", cardMask), NamedOnnxValue.CreateFromTensor("mods", mods),
            NamedOnnxValue.CreateFromTensor("mod_mask", modMask),
        };
        inputs.AddRange(Bag("relics", "relic_mask", tokens.Relics));
        inputs.AddRange(Bag("potions", "potion_mask", tokens.Potions));
        inputs.AddRange(Bag("context", "context_mask", tokens.Context));
        inputs.Add(NamedOnnxValue.CreateFromTensor("features", scalars));
        using var results = _session.Run(inputs);
        return results[0].AsEnumerable<float>().ToArray();
    }

    /// <summary>A token table's ids as the graph takes them: padded to at least 1, with their mask.</summary>
    private NamedOnnxValue[] Bag(string table, string mask, IReadOnlyList<string> ids)
    {
        int n = Math.Max(1, ids.Count);
        var index = new DenseTensor<long>([1, n]);
        var present = new DenseTensor<float>([1, n]);
        for (int k = 0; k < ids.Count; k++)
        {
            index[0, k] = Index(table, ids[k]);
            present[0, k] = 1;
        }
        return [NamedOnnxValue.CreateFromTensor(table, index), NamedOnnxValue.CreateFromTensor(mask, present)];
    }

    private int Index(string table, string id) => _index[table].GetValueOrDefault(id);
}
