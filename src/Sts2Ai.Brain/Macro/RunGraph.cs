using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// A run's graph as <c>learn/v</c> and <c>learn/f</c> write it (V's, <see cref="RunNet"/>; f's, <see cref="SavesNet"/>):
/// the token tables, the state's mean and std, and the ONNX session it feeds a run to, read as the trainer reads it
/// (<see cref="RunNet.Tokens"/>, <see cref="RunNet.State"/>).
/// </summary>
internal sealed class RunGraph : IDisposable
{
    private readonly Dictionary<string, Dictionary<string, int>> _index;
    private readonly NetState _state;
    private readonly InferenceSession _session;

    /// <summary>Whether the graph asks about potions (f's): it then takes the run's potions as its input "asked".</summary>
    private readonly bool _asks;

    public RunGraph(IReadOnlyDictionary<string, IReadOnlyList<string>> vocab, NetState state, string onnx)
    {
        _index = vocab.ToDictionary(t => t.Key, t => t.Value.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i));
        _state = state;
        // One thread per call: the sim runs many processes, and a macro decision values a handful of small inputs.
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(Convert.FromBase64String(onnx), options);
        _asks = _session.InputMetadata.ContainsKey("asked");
    }

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// The graph's output on a batch of one run read in <paramref name="env"/>, and the run's tokens: V's hazards, or with
    /// a graph that asks about potions (f's) each potion's f, the run's potions asked in the belt's order.
    /// </summary>
    public (float[] Output, RunTokens Tokens) Run(RunView run, PlayEnvironment env)
    {
        var tokens = RunNet.Tokens(run, env);
        var inputs = new List<NamedOnnxValue>(CardInputs("", tokens.Cards));
        inputs.AddRange(Bag("relics", "relic_mask", tokens.Relics));
        inputs.AddRange(Bag("potions", "potion_mask", tokens.Potions));
        inputs.AddRange(Bag("context", "context_mask", tokens.Context));
        inputs.AddRange(Bag("map", "map_mask", tokens.Map));
        var values = new DenseTensor<float>([1, _state.Names.Count]);
        for (int i = 0; i < _state.Names.Count; i++)
            values[0, i] = (float)((RunNet.State(run, _state.Names[i]) - _state.Mean[i]) / _state.Std[i]);
        inputs.Add(NamedOnnxValue.CreateFromTensor("state", values));
        inputs.AddRange(CombatNet.RelicInputs(tokens.RelicValues, tokens.RelicStatus, status => Index("relic_status", status)));
        inputs.AddRange(Bag("cards", "held", "held_mask", tokens.Held));
        inputs.AddRange(PendingInputs(tokens.Pending));
        if (_asks) inputs.Add(Bag("potions", "asked", "asked_mask", tokens.Potions)[0]);
        using var results = _session.Run(inputs);
        return ([.. results[0].AsEnumerable<float>()], tokens);
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

    /// <summary>An id's entry in a token table; 0 for an id the table does not list.</summary>
    private long Index(string table, string id) => _index[table].GetValueOrDefault(id);
}
