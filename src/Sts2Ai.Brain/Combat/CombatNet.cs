using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>A round's start and what the trainer's net made of it, which loading checks.</summary>
public sealed record CombatNetTest(CombatObservation Obs, IReadOnlyList<EnemyTurns> History, IReadOnlyList<double> Outcome, IReadOnlyList<double> PotionUsed);

/// <summary>The combat net as <c>learn/combat/export.py</c> writes it.</summary>
/// <param name="Buckets">How many buckets of the share of max HP lost from the state to a win the head has, after its
/// first class, a death.</param>
/// <param name="LossMin">The lowest bucket's bottom (below 0: a combat that healed); the buckets span it to 1 evenly.</param>
/// <param name="Lags">How many earlier rounds of each enemy's intents the net reads.</param>
/// <param name="Vocab">Each token table's ids; an id a table does not list reads as its entry 0.</param>
/// <param name="Onnx">The net as an ONNX graph, base64: inputs as <see cref="CombatNet"/> feeds them, outputs the
/// chance of each ending (those the HP held rules out at 0) and, after a win in each group of buckets, each potion id's
/// chance of being gone, group by group.</param>
/// <param name="PotionGroups">How many groups of buckets in a row the potion chances are given for, the buckets split evenly.</param>
public sealed record CombatNetFile(
    string Name, int Buckets, double LossMin, int Lags, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab, string Onnx,
    IReadOnlyList<CombatNetTest> Tests, int PotionGroups);

/// <summary>
/// The learned combat leaf: from a round's start, how the combat ends under the policy that
/// played the training games. The net, run by ONNX Runtime, reads the cards in each pile, the player, each enemy with
/// its intents now and in its last rounds, the powers, relics, potions and potion slots, orbs and allies, the encounter and the character
/// (<see cref="CombatTokens"/>). It predicts
/// what is still to come: the HP share lost from here and which potions go, so the HP and potions held now count in full.
/// Every ending is priced by V (<see cref="IRunValue"/>) on the run it leaves: a death ends the run, a win is worth V of
/// the run then. That run is the one the leaf state stands in (<see cref="ICombatWorld.Run"/>: the deck, relics, max HP
/// and gold as the combat has left them so far) with the HP the bucket leaves and the potions kept, each subset of those
/// held by the chance the net gives it after a win in the bucket's group (each potion's taken apart from the others'). A
/// finished combat is V of its run. A belt is priced as the best of it with any of its potions thrown away, which the
/// game lets a player do for nothing (<see cref="FreeDisposal"/>): a potion V rates below an empty slot is not drunk
/// for nothing to be rid of it.
/// This class only feeds a state's tokens to the graph, and turns its endings into runs for V.
/// </summary>
public sealed class CombatNet : ICombatLeaf, IDisposable
{
    /// <summary>How many of V's values the leaf keeps; past it they are dropped and valued again as needed.</summary>
    private const int Kept = 200_000;

    private readonly CombatNetFile _file;
    private readonly IRunValue _value;
    private readonly Dictionary<string, Dictionary<string, int>> _index;
    private readonly InferenceSession _session;

    /// <summary>V's values of the runs the leaf priced, by the run without its HP and potions (<see cref="Key"/>), the HP and the potions.</summary>
    private readonly ConcurrentDictionary<(ulong Run, int Hp, string Potions), double> _prices = new();

    private CombatNet(CombatNetFile file, IRunValue value)
    {
        _file = file;
        _value = value;
        _index = file.Vocab.ToDictionary(t => t.Key, t => t.Value.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i));
        // One thread per call: search workers run side by side, and a leaf is one small input.
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(Convert.FromBase64String(file.Onnx), options);
    }

    public string Name => _file.Name;

    /// <summary>The net of a file, its endings priced by <paramref name="value"/>, once it gives every test state the trainer's outcome.</summary>
    public static CombatNet Load(CombatNetFile file, IRunValue value)
    {
        var net = new CombatNet(file, value);
        foreach (var test in file.Tests)
        {
            var outputs = net.Graph(test.Obs, test.History);
            var (outcome, potionUsed) = (outputs[0], outputs[1]);
            double error = 0;
            for (int k = 0; k < potionUsed.Length; k++) error = Math.Max(error, Math.Abs(potionUsed[k] - test.PotionUsed[k]));
            for (int k = 0; k < outcome.Length; k++) error = Math.Max(error, Math.Abs(outcome[k] - test.Outcome[k]));
            if (outcome.Length != test.Outcome.Count || potionUsed.Length != test.PotionUsed.Count || error > 1e-3)
            {
                net.Dispose();
                throw new InvalidDataException($"net {file.Name} is {error} off a test state of the trainer's");
            }
        }
        return net;
    }

    /// <summary>How many states the net is asked about in one evaluation (<see cref="Outcomes"/>).</summary>
    public const int Batch = 16;

    public LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history) =>
        Ended(status, run) ?? Priced(run, Graphs([CombatTokens.Of(obs, history, _file.Lags)])[0]);

    /// <summary>
    /// The states' outcomes with the net asked about <see cref="Batch"/> of them at a time, in their order: one
    /// evaluation of a batch costs a few times one state's. A batch pads every state to its widest, so a state's
    /// numbers can differ in their last bits from what it gets alone or in other company; the batches follow from
    /// the states and their order only.
    /// </summary>
    public IReadOnlyList<LeafOutcome> Outcomes(IReadOnlyList<LeafState> states)
    {
        var outcomes = new LeafOutcome[states.Count];
        var going = new List<int>();
        for (int i = 0; i < states.Count; i++)
        {
            if (Ended(states[i].Status, states[i].Run) is { } ended) outcomes[i] = ended;
            else going.Add(i);
        }
        for (int from = 0; from < going.Count; from += Batch)
        {
            var asked = going.GetRange(from, Math.Min(Batch, going.Count - from));
            var outputs = Graphs([.. asked.Select(i => CombatTokens.Of(states[i].Obs, states[i].History, _file.Lags))]);
            for (int k = 0; k < asked.Count; k++) outcomes[asked[k]] = Priced(states[asked[k]].Run, outputs[k]);
        }
        return outcomes;
    }

    /// <summary>A finished combat: nothing for a death, V of its run for a win; null while the combat goes on.</summary>
    private LeafOutcome? Ended(CombatStatus status, RunView run) => status switch
    {
        CombatStatus.Lost => new(0, 0, 0),
        CombatStatus.Won => new(FreeDisposal.Best(run.Potions, potions => Price(run, Key(run), run.Hp, potions)), 1, run.Hp),
        _ => null,
    };

    /// <summary>The value, the chance not to die (1 less the death class), and the HP held at a win: the HP share now
    /// less each win bucket's middle, weighted by the buckets.</summary>
    /// <param name="outputs">The graph's outputs for the state: the endings' chances, and the potions' chances of being gone.</param>
    private LeafOutcome Priced(RunView run, float[][] outputs)
    {
        ulong key = Key(run);
        var (outcome, potionUsed) = (outputs[0], outputs[1]);
        var held = Enumerable.Range(0, run.Potions.Count).Where(s => run.Potions[s] != null).ToList();
        // Each held potion's chance of being kept after a win in each group of buckets.
        int table = _file.Vocab["potions"].Count, perGroup = _file.Buckets / _file.PotionGroups;
        var kept = Enumerable.Range(0, _file.PotionGroups)
            .Select(g => held.Select(s => 1.0 - potionUsed[g * table + (int)Index("potions", run.Potions[s]!)]).ToList()).ToList();
        int maxHp = Math.Max(1, run.MaxHp);
        double share = run.Hp / (double)maxHp;
        double width = (1 - _file.LossMin) / _file.Buckets, value = 0, left = 0;
        var belts = FreeDisposal.Kept(run.Potions);
        for (int k = 1; k < outcome.Length; k++)
        {
            if (outcome[k] == 0) continue;
            double after = share - (_file.LossMin + (k - 0.5) * width);
            int hp = Math.Clamp((int)Math.Round(after * maxHp), 1, maxHp);
            // Each belt kept is worth the best of it with any of its potions thrown away (FreeDisposal).
            var prices = belts.Select(belt => Price(run, key, hp, belt)).ToArray();
            FreeDisposal.Spread(prices);
            for (int mask = 0; mask < belts.Length; mask++)
            {
                double chance = outcome[k];
                for (int i = 0; i < held.Count; i++)
                {
                    double keptHere = kept[(k - 1) / perGroup][i];
                    chance *= (mask >> i & 1) == 1 ? keptHere : 1 - keptHere;
                }
                value += chance * prices[mask];
            }
            left += outcome[k] * after;
        }
        double win = 1 - outcome[0];
        return new(value, win, win > 0 ? Math.Clamp(left / win, 0, 1) * maxHp : 0);
    }

    /// <summary>The run without its HP and potions, hashed: what the leaf's prices of one state share. A thread's
    /// leaves mostly stand in one run that differs in nothing else, and a world that hands out the same deck and
    /// relics for it lets that be seen without writing the run again.</summary>
    private ulong Key(RunView run)
    {
        if (_keyed.Value is var (last, kept) && run with { Hp = last.Hp, Potions = last.Potions } == last) return kept;
        ulong key = ProtocolJson.Hash(run with { Hp = 0, Potions = [] });
        _keyed.Value = (run, key);
        return key;
    }

    /// <summary>The run a thread last made a key of, and the key.</summary>
    private readonly ThreadLocal<(RunView Run, ulong Key)?> _keyed = new();

    /// <summary>V of <paramref name="run"/> (whose <see cref="Key"/> is <paramref name="key"/>) at <paramref name="hp"/> with <paramref name="potions"/>.</summary>
    private double Price(RunView run, ulong key, int hp, IReadOnlyList<string?> potions)
    {
        var at = (key, hp, string.Join(",", potions));
        if (_prices.TryGetValue(at, out double known)) return known;
        if (_prices.Count >= Kept) _prices.Clear();
        return _prices[at] = _value.Value(run with { Hp = hp, Potions = potions });
    }

    public void Dispose()
    {
        _session.Dispose();
        _keyed.Dispose();
    }

    /// <summary>The graph's outputs on a batch of one state.</summary>
    private float[][] Graph(CombatObservation obs, IReadOnlyList<EnemyTurns> history) => Graphs([CombatTokens.Of(obs, history, _file.Lags)])[0];

    /// <summary>The graph's outputs for each of the states, asked as one batch.</summary>
    private float[][][] Graphs(IReadOnlyList<CombatTokens> states)
    {
        using var results = _session.Run(Stack([.. states.Select(Feed)]));
        var outputs = results.Select(r => r.AsEnumerable<float>().ToArray()).ToList();
        return [.. Enumerable.Range(0, states.Count).Select(b => outputs.Select(all =>
        {
            int width = all.Length / states.Count;
            return all[(b * width)..((b + 1) * width)];
        }).ToArray())];
    }

    /// <summary>
    /// Several states' inputs as one batch: every input padded with zeros to its widest state in each dimension. Zero
    /// is what the graph reads as nothing there (a mask of 0, a card in no pile), as in the trainer's batches.
    /// </summary>
    private static List<NamedOnnxValue> Stack(IReadOnlyList<List<NamedOnnxValue>> states)
    {
        if (states.Count == 1) return states[0];
        var batch = new List<NamedOnnxValue>(states[0].Count);
        for (int k = 0; k < states[0].Count; k++)
        {
            string name = states[0][k].Name;
            batch.Add(states[0][k].Value switch
            {
                DenseTensor<float> => NamedOnnxValue.CreateFromTensor(name, Stacked<float>(states, k)),
                DenseTensor<long> => NamedOnnxValue.CreateFromTensor(name, Stacked<long>(states, k)),
                var other => throw new InvalidOperationException($"input {name} is a {other.GetType().Name}"),
            });
        }
        return batch;
    }

    /// <summary>Input <paramref name="k"/> of each state, each a batch of one, as one tensor with a row a state.</summary>
    private static DenseTensor<T> Stacked<T>(IReadOnlyList<List<NamedOnnxValue>> states, int k)
    {
        var parts = states.Select(inputs => (DenseTensor<T>)inputs[k].Value).ToList();
        int rank = parts[0].Rank;
        var dims = new int[rank];
        dims[0] = parts.Count;
        for (int d = 1; d < rank; d++)
            foreach (var part in parts) dims[d] = Math.Max(dims[d], part.Dimensions[d]);
        var batch = new DenseTensor<T>(dims);
        var to = batch.Buffer.Span;
        for (int b = 0; b < parts.Count; b++)
        {
            var from = parts[b].Buffer.Span;
            for (int i = 0; i < from.Length; i++)
            {
                // The element's place in its state, dimension by dimension, is its place in the state's row of the batch.
                int rest = i, at = b * batch.Strides[0];
                for (int d = 1; d < rank; d++)
                {
                    int index = rest / parts[b].Strides[d];
                    rest -= index * parts[b].Strides[d];
                    at += index * batch.Strides[d];
                }
                to[at] = from[i];
            }
        }
        return batch;
    }

    /// <summary>
    /// The graph's inputs of one state's tokens: each id as its entry in its table, each number as it is, every set padded
    /// to at least 1 with its mask. The trainer builds the same from the same tokens (<c>learn/combat/net.py</c>'s Batch).
    /// </summary>
    private List<NamedOnnxValue> Feed(CombatTokens t)
    {
        var inputs = new List<NamedOnnxValue>();
        inputs.AddRange(Cards(t.Cards));
        inputs.AddRange(Powers("powers", "power_amounts", "power_mask", t.Powers));
        inputs.AddRange(PowerNumbers(t.Powers));
        inputs.Add(Row("player", t.Player));
        inputs.AddRange(Enemies(t.Enemies));
        inputs.AddRange(Bag("relics", "relic_mask", t.Relics));
        inputs.AddRange(RelicInputs(t.RelicValues, t.RelicStatus, status => Index("relic_status", status)));
        inputs.AddRange(Bag("potions", "potion_mask", t.Potions));
        inputs.AddRange(Bag("context", "context_mask", t.Context));
        inputs.AddRange(Bag("orbs", "orb_mask", t.Orbs));
        var orbValues = new DenseTensor<float>([1, Math.Max(1, t.Orbs.Count), 2]);
        for (int k = 0; k < t.OrbValues.Count; k++)
            for (int j = 0; j < 2; j++) orbValues[0, k, j] = (float)t.OrbValues[k][j];
        inputs.Add(NamedOnnxValue.CreateFromTensor("orb_values", orbValues));
        inputs.AddRange(Allies(t.Allies));
        inputs.Add(Row("extra", t.Extra));
        return inputs;
    }

    private NamedOnnxValue[] Cards(IReadOnlyList<CombatCardToken> cards)
    {
        int n = Math.Max(1, cards.Count);
        int m = Math.Max(1, cards.Select(c => c.Modifiers.Count).DefaultIfEmpty(0).Max());
        var ids = new DenseTensor<long>([1, n]);
        var ups = new DenseTensor<float>([1, n]);
        var costs = new DenseTensor<float>([1, n]);
        var damages = new DenseTensor<float>([1, n]);
        var piles = new DenseTensor<float>([1, n, CombatTokens.Piles.Count]);
        var mods = new DenseTensor<long>([1, n, m]);
        var modMask = new DenseTensor<float>([1, n, m]);
        var modAmounts = new DenseTensor<float>([1, n, m]);
        var numbers = new DenseTensor<float>([1, n, CombatTokens.CardNumbers]);
        for (int k = 0; k < cards.Count; k++)
        {
            var card = cards[k];
            ids[0, k] = Index("cards", card.Id);
            ups[0, k] = card.Up;
            costs[0, k] = (float)card.Cost;
            piles[0, k, card.Pile] = 1;
            damages[0, k] = (float)card.Damage;
            for (int j = 0; j < card.Modifiers.Count; j++)
            {
                mods[0, k, j] = Index("modifiers", card.Modifiers[j]);
                modMask[0, k, j] = 1;
            }
            for (int j = 0; j < card.Amounts.Count; j++) modAmounts[0, k, j] = (float)card.Amounts[j];
            for (int i = 0; i < card.Numbers.Count; i++) numbers[0, k, i] = (float)card.Numbers[i];
        }
        return
        [
            NamedOnnxValue.CreateFromTensor("cards", ids), NamedOnnxValue.CreateFromTensor("ups", ups),
            NamedOnnxValue.CreateFromTensor("costs", costs), NamedOnnxValue.CreateFromTensor("piles", piles),
            NamedOnnxValue.CreateFromTensor("mods", mods), NamedOnnxValue.CreateFromTensor("mod_mask", modMask),
            NamedOnnxValue.CreateFromTensor("mod_amounts", modAmounts), NamedOnnxValue.CreateFromTensor("damages", damages),
            NamedOnnxValue.CreateFromTensor("numbers", numbers),
        ];
    }

    private NamedOnnxValue[] Enemies(IReadOnlyList<EnemyTokens> enemies)
    {
        int slots = 1 + _file.Lags;
        int e_n = Math.Max(1, enemies.Count);
        int q_n = Math.Max(1, enemies.Select(e => e.Powers.Count).DefaultIfEmpty(0).Max());
        int i_n = Math.Max(1, enemies.SelectMany(e => e.Slots).Select(s => s.Count).DefaultIfEmpty(0).Max());
        var ids = new DenseTensor<long>([1, e_n]);
        var scalars = new DenseTensor<float>([1, e_n, CombatTokens.EnemyScalars]);
        var mask = new DenseTensor<float>([1, e_n]);
        var powers = new DenseTensor<long>([1, e_n, q_n]);
        var powerAmounts = new DenseTensor<float>([1, e_n, q_n]);
        var powerMask = new DenseTensor<float>([1, e_n, q_n]);
        var powerStacks = new DenseTensor<float>([1, e_n, q_n]);
        var powerCards = new DenseTensor<long>([1, e_n, q_n]);
        var powerCardMask = new DenseTensor<float>([1, e_n, q_n]);
        var powerDetails = new DenseTensor<float>([1, e_n, q_n]);
        var intents = new DenseTensor<long>([1, e_n, slots, i_n]);
        var intentValues = new DenseTensor<float>([1, e_n, slots, i_n, 2]);
        var intentMask = new DenseTensor<float>([1, e_n, slots, i_n]);
        for (int k = 0; k < enemies.Count; k++)
        {
            var e = enemies[k];
            ids[0, k] = Index("enemies", e.Id);
            mask[0, k] = 1;
            for (int i = 0; i < e.Scalars.Count; i++) scalars[0, k, i] = (float)e.Scalars[i];
            for (int j = 0; j < e.Powers.Count; j++)
            {
                var power = e.Powers[j];
                powers[0, k, j] = Index("powers", power.Id);
                powerAmounts[0, k, j] = (float)power.Amount;
                powerMask[0, k, j] = 1;
                powerStacks[0, k, j] = (float)power.Stacks;
                powerDetails[0, k, j] = (float)power.Detail;
                if (power.Card == null) continue;
                powerCards[0, k, j] = Index("cards", power.Card);
                powerCardMask[0, k, j] = 1;
            }
            for (int s = 0; s < e.Slots.Count; s++)
                for (int j = 0; j < e.Slots[s].Count; j++)
                {
                    var intent = e.Slots[s][j];
                    intents[0, k, s, j] = Index("intents", intent.Type);
                    intentMask[0, k, s, j] = 1;
                    intentValues[0, k, s, j, 0] = (float)intent.Damage;
                    intentValues[0, k, s, j, 1] = (float)intent.Total;
                }
        }
        return
        [
            NamedOnnxValue.CreateFromTensor("enemies", ids), NamedOnnxValue.CreateFromTensor("enemy_scalars", scalars),
            NamedOnnxValue.CreateFromTensor("enemy_mask", mask), NamedOnnxValue.CreateFromTensor("enemy_powers", powers),
            NamedOnnxValue.CreateFromTensor("enemy_power_amounts", powerAmounts),
            NamedOnnxValue.CreateFromTensor("enemy_power_mask", powerMask), NamedOnnxValue.CreateFromTensor("enemy_power_stacks", powerStacks),
            NamedOnnxValue.CreateFromTensor("enemy_power_cards", powerCards), NamedOnnxValue.CreateFromTensor("enemy_power_card_mask", powerCardMask),
            NamedOnnxValue.CreateFromTensor("enemy_power_details", powerDetails),
            NamedOnnxValue.CreateFromTensor("intents", intents), NamedOnnxValue.CreateFromTensor("intent_values", intentValues),
            NamedOnnxValue.CreateFromTensor("intent_mask", intentMask),
        ];
    }

    /// <summary>The player's powers' stacks, kept or stolen cards, and numbers moved from their own.</summary>
    private NamedOnnxValue[] PowerNumbers(IReadOnlyList<PowerToken> powers)
    {
        int n = Math.Max(1, powers.Count);
        var stacks = new DenseTensor<float>([1, n]);
        var cards = new DenseTensor<long>([1, n]);
        var mask = new DenseTensor<float>([1, n]);
        var details = new DenseTensor<float>([1, n]);
        for (int k = 0; k < powers.Count; k++)
        {
            stacks[0, k] = (float)powers[k].Stacks;
            details[0, k] = (float)powers[k].Detail;
            if (powers[k].Card is not { } card) continue;
            cards[0, k] = Index("cards", card);
            mask[0, k] = 1;
        }
        return
        [
            NamedOnnxValue.CreateFromTensor("power_stacks", stacks), NamedOnnxValue.CreateFromTensor("power_cards", cards),
            NamedOnnxValue.CreateFromTensor("power_card_mask", mask), NamedOnnxValue.CreateFromTensor("power_details", details),
        ];
    }

    /// <summary>The graph inputs relic_values and relic_status of a run's relics (<see cref="CombatTokens.RelicValuesOf"/>,
    /// <see cref="CombatTokens.RelicStatusOf"/>), as both nets take them; a status's row by <paramref name="index"/>.</summary>
    public static NamedOnnxValue[] RelicInputs(IReadOnlyList<IReadOnlyList<double>> values, IReadOnlyList<string> status, Func<string, long> index)
    {
        int n = Math.Max(1, values.Count);
        var numbers = new DenseTensor<float>([1, n, 3]);
        var rows = new DenseTensor<long>([1, n]);
        for (int k = 0; k < values.Count; k++)
        {
            for (int j = 0; j < values[k].Count; j++) numbers[0, k, j] = (float)values[k][j];
            rows[0, k] = index(status[k]);
        }
        return [NamedOnnxValue.CreateFromTensor("relic_values", numbers), NamedOnnxValue.CreateFromTensor("relic_status", rows)];
    }

    private NamedOnnxValue[] Allies(IReadOnlyList<AllyTokens> allies)
    {
        int n = Math.Max(1, allies.Count);
        int q = Math.Max(1, allies.Select(a => a.Powers.Count).DefaultIfEmpty(0).Max());
        var ids = new DenseTensor<long>([1, n]);
        var scalars = new DenseTensor<float>([1, n, 4]);
        var mask = new DenseTensor<float>([1, n]);
        var powers = new DenseTensor<long>([1, n, q]);
        var amounts = new DenseTensor<float>([1, n, q]);
        var powerMask = new DenseTensor<float>([1, n, q]);
        for (int k = 0; k < allies.Count; k++)
        {
            var a = allies[k];
            ids[0, k] = Index("allies", a.Id);
            for (int i = 0; i < a.Scalars.Count; i++) scalars[0, k, i] = (float)a.Scalars[i];
            mask[0, k] = 1;
            for (int j = 0; j < a.Powers.Count; j++)
            {
                powers[0, k, j] = Index("powers", a.Powers[j].Id);
                amounts[0, k, j] = (float)a.Powers[j].Amount;
                powerMask[0, k, j] = 1;
            }
        }
        return
        [
            NamedOnnxValue.CreateFromTensor("allies", ids), NamedOnnxValue.CreateFromTensor("ally_scalars", scalars),
            NamedOnnxValue.CreateFromTensor("ally_mask", mask), NamedOnnxValue.CreateFromTensor("ally_powers", powers),
            NamedOnnxValue.CreateFromTensor("ally_power_amounts", amounts), NamedOnnxValue.CreateFromTensor("ally_power_mask", powerMask),
        ];
    }

    /// <summary>Numbers as one row of the graph's input <paramref name="name"/>.</summary>
    private static NamedOnnxValue Row(string name, IReadOnlyList<double> numbers)
    {
        var tensor = new DenseTensor<float>([1, numbers.Count]);
        for (int i = 0; i < numbers.Count; i++) tensor[0, i] = (float)numbers[i];
        return NamedOnnxValue.CreateFromTensor(name, tensor);
    }

    /// <summary>Powers as the graph takes them: ids, amounts and their mask, padded to at least 1.</summary>
    private NamedOnnxValue[] Powers(string ids, string amounts, string mask, IReadOnlyList<PowerToken> powers)
    {
        int n = Math.Max(1, powers.Count);
        var index = new DenseTensor<long>([1, n]);
        var amount = new DenseTensor<float>([1, n]);
        var present = new DenseTensor<float>([1, n]);
        for (int k = 0; k < powers.Count; k++)
        {
            index[0, k] = Index("powers", powers[k].Id);
            amount[0, k] = (float)powers[k].Amount;
            present[0, k] = 1;
        }
        return [NamedOnnxValue.CreateFromTensor(ids, index), NamedOnnxValue.CreateFromTensor(amounts, amount), NamedOnnxValue.CreateFromTensor(mask, present)];
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

    /// <summary>An id's entry in a token table; 0 for an id the table does not list.</summary>
    private long Index(string table, string id) => _index[table].GetValueOrDefault(id);
}
