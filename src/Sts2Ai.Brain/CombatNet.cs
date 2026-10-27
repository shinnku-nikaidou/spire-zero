using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>A round's start and what the trainer's net made of it, which loading checks (docs: learning §7, input parity).</summary>
public sealed record CombatNetTest(CombatObservation Obs, IReadOnlyList<EnemyTurns> History, IReadOnlyList<double> Outcome, double Potions);

/// <summary>
/// The combat net as <c>learn/train_combat.py</c> writes it (docs: learning §1).
/// </summary>
/// <param name="Buckets">How many buckets of the HP share left after a win the head has, after its first class, a death.</param>
/// <param name="Lags">How many earlier rounds of each enemy's intents the net reads.</param>
/// <param name="Round">Whether the net reads the round and how many rounds each enemy has shown.</param>
/// <param name="Vocab">Each token table's ids; an id a table does not list reads as its entry 0.</param>
/// <param name="Onnx">The net as an ONNX graph, base64: inputs as <see cref="CombatNet"/> feeds them, outputs the
/// chance of each ending and the potions left after a win.</param>
public sealed record CombatNetFile(
    string Name, int Buckets, int Lags, bool Round, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab, string Onnx,
    IReadOnlyList<CombatNetTest> Tests);

/// <summary>
/// The learned combat leaf (docs: combat-brain §4): from a round's start, how the combat ends under the policy that
/// played the training games. The net, run by ONNX Runtime, reads the cards in each pile, the player, each enemy with
/// its intents now and in its last rounds, the relics, the potions and the encounter. A state in progress is worth the
/// endings it predicts, priced as <see cref="LeafValue"/> prices a win (each HP bucket at its middle, and the potions
/// left); a state whose combat is over is <see cref="LeafValue"/>'s. This class only turns a state into the graph's
/// inputs.
/// </summary>
public sealed class CombatNet : ICombatLeaf, IDisposable
{
    private static readonly string[] Piles = ["hand", "draw", "discard", "exhaust"];
    /// <summary>Every scaled number the net reads is capped at ±this: a few states show huge ones (an erupting
    /// WATERFALL_GIANT's HP).</summary>
    private const double Cap = 10;

    private readonly CombatNetFile _file;
    private readonly LeafWeights _weights;
    private readonly Dictionary<string, Dictionary<string, int>> _index;
    private readonly InferenceSession _session;

    private CombatNet(CombatNetFile file, LeafWeights weights)
    {
        _file = file;
        _weights = weights;
        _index = file.Vocab.ToDictionary(t => t.Key, t => t.Value.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i));
        // One thread per call: search workers run side by side, and a leaf is one small input.
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(Convert.FromBase64String(file.Onnx), options);
    }

    public string Name => _file.Name;

    /// <summary>The net of a file, once it gives every test state the trainer's outcome.</summary>
    /// <param name="weights">The prices of the endings; null for the default leaf weights.</param>
    public static CombatNet Load(CombatNetFile file, LeafWeights? weights = null)
    {
        var net = new CombatNet(file, weights ?? LeafValue.Default);
        foreach (var test in file.Tests)
        {
            var (outcome, potions) = net.Predict(test.Obs, test.History);
            double error = Math.Abs(potions - test.Potions);
            for (int k = 0; k < outcome.Length; k++) error = Math.Max(error, Math.Abs(outcome[k] - test.Outcome[k]));
            if (outcome.Length != test.Outcome.Count || error > 1e-3)
            {
                net.Dispose();
                throw new InvalidDataException($"net {file.Name} is {error} off a test state of the trainer's");
            }
        }
        return net;
    }

    public double Value(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history)
    {
        if (status != CombatStatus.InProgress) return LeafValue.Of(status, obs, _weights);
        var (outcome, potions) = Predict(obs, history);
        double value = 0;
        for (int k = 1; k < outcome.Length; k++) value += outcome[k] * (_weights.WinBase + _weights.PerHp * (k - 0.5) / _file.Buckets);
        return value + (1 - outcome[0]) * _weights.PerPotion * potions;
    }

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// The chance of each ending (a death, then each HP bucket) and the potions left after a win: the graph on a
    /// batch of one state, read as <c>learn/train_combat.py</c> reads it. An enemy's slots are its intents now, then
    /// those of its last rounds before this one, most recent first.
    /// </summary>
    public (float[] Outcome, float Potions) Predict(CombatObservation obs, IReadOnlyList<EnemyTurns> history)
    {
        var cards = Piles.SelectMany((pile, p) => Pile(obs, pile).Select(c => (Card: c, Pile: p))).ToList();
        int n = Math.Max(1, cards.Count);
        var cardIds = new DenseTensor<long>([1, n]);
        var ups = new DenseTensor<float>([1, n]);
        var costs = new DenseTensor<float>([1, n]);
        var piles = new DenseTensor<float>([1, n, Piles.Length]);
        var mods = new DenseTensor<long>([1, n, 2]);
        var modMask = new DenseTensor<float>([1, n, 2]);
        for (int k = 0; k < cards.Count; k++)
        {
            var (card, pile) = cards[k];
            cardIds[0, k] = Index("cards", card.Id);
            ups[0, k] = Math.Min(card.Up ?? 0, RunNet.MaxUp);
            costs[0, k] = card.Cost is { } cost && cost > 0 ? (float)(cost / 3.0) : 0;
            piles[0, k, pile] = 1;
            var modifiers = new List<string>();
            if (card.Enchant != null) modifiers.Add("enchant:" + card.Enchant.Id);
            if (card.Affliction != null) modifiers.Add("affliction:" + card.Affliction.Id);
            for (int j = 0; j < modifiers.Count; j++)
            {
                mods[0, k, j] = Index("modifiers", modifiers[j]);
                modMask[0, k, j] = 1;
            }
        }

        var pl = obs.Player;
        double maxHp = Math.Max(1, pl.MaxHp);
        double[] player = [pl.Hp / maxHp, pl.Hp / 100.0, pl.MaxHp / 100.0, pl.Block / 20.0, pl.Energy / 3.0, pl.MaxEnergy / 3.0, pl.Stars / 5.0,
            _file.Round ? obs.Round / 10.0 : 0];
        var playerTensor = new DenseTensor<float>([1, player.Length]);
        for (int i = 0; i < player.Length; i++) playerTensor[0, i] = Capped(player[i]);

        var past = history.ToDictionary(h => h.Uid, h => h.Turns.Where(t => t.Round < obs.Round).ToList());
        var enemies = obs.Enemies.Where(e => e.Hp > 0).ToList();
        int slots = 1 + _file.Lags;
        int e_n = Math.Max(1, enemies.Count);
        int q_n = Math.Max(1, enemies.Select(e => e.Powers.Count).DefaultIfEmpty(0).Max());
        var enemySlots = enemies.Select(e =>
        {
            var turns = e.Uid is { } uid && past.TryGetValue(uid, out var t) ? t : [];
            var list = new List<IReadOnlyList<IntentView>> { e.Intents ?? [] };
            for (int lag = 1; lag <= _file.Lags && lag <= turns.Count; lag++) list.Add(turns[^lag].Intents);
            return (Turns: turns.Count, Slots: list);
        }).ToList();
        int i_n = Math.Max(1, enemySlots.SelectMany(s => s.Slots).Select(s => s.Count).DefaultIfEmpty(0).Max());
        var enemyIds = new DenseTensor<long>([1, e_n]);
        var enemyScalars = new DenseTensor<float>([1, e_n, 5]);
        var enemyMask = new DenseTensor<float>([1, e_n]);
        var enemyPowers = new DenseTensor<long>([1, e_n, q_n]);
        var enemyPowerAmounts = new DenseTensor<float>([1, e_n, q_n]);
        var enemyPowerMask = new DenseTensor<float>([1, e_n, q_n]);
        var intents = new DenseTensor<long>([1, e_n, slots, i_n]);
        var intentValues = new DenseTensor<float>([1, e_n, slots, i_n, 2]);
        var intentMask = new DenseTensor<float>([1, e_n, slots, i_n]);
        for (int k = 0; k < enemies.Count; k++)
        {
            var e = enemies[k];
            enemyIds[0, k] = Index("enemies", e.Id ?? "<none>");
            enemyMask[0, k] = 1;
            double enemyMax = Math.Max(1, e.MaxHp);
            double[] scalars = [e.Hp / enemyMax, e.Hp / 100.0, e.MaxHp / 100.0, e.Block / 20.0, _file.Round ? enemySlots[k].Turns / 10.0 : 0];
            for (int i = 0; i < scalars.Length; i++) enemyScalars[0, k, i] = Capped(scalars[i]);
            for (int j = 0; j < e.Powers.Count; j++)
            {
                enemyPowers[0, k, j] = Index("powers", e.Powers[j].Id);
                enemyPowerAmounts[0, k, j] = Capped(e.Powers[j].Amount / 10.0);
                enemyPowerMask[0, k, j] = 1;
            }
            var slotList = enemySlots[k].Slots;
            for (int s = 0; s < slotList.Count; s++)
                for (int j = 0; j < slotList[s].Count; j++)
                {
                    var intent = slotList[s][j];
                    int damage = intent.Damage ?? 0;
                    intents[0, k, s, j] = Index("intents", intent.Type);
                    intentMask[0, k, s, j] = 1;
                    intentValues[0, k, s, j, 0] = Capped(damage / 10.0);
                    intentValues[0, k, s, j, 1] = Capped(damage * (intent.Hits ?? 1) / 20.0);
                }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("cards", cardIds), NamedOnnxValue.CreateFromTensor("ups", ups),
            NamedOnnxValue.CreateFromTensor("costs", costs), NamedOnnxValue.CreateFromTensor("piles", piles),
            NamedOnnxValue.CreateFromTensor("mods", mods), NamedOnnxValue.CreateFromTensor("mod_mask", modMask),
        };
        inputs.AddRange(Powers("powers", "power_amounts", "power_mask", pl.Powers));
        inputs.Add(NamedOnnxValue.CreateFromTensor("player", playerTensor));
        inputs.AddRange([
            NamedOnnxValue.CreateFromTensor("enemies", enemyIds), NamedOnnxValue.CreateFromTensor("enemy_scalars", enemyScalars),
            NamedOnnxValue.CreateFromTensor("enemy_mask", enemyMask), NamedOnnxValue.CreateFromTensor("enemy_powers", enemyPowers),
            NamedOnnxValue.CreateFromTensor("enemy_power_amounts", enemyPowerAmounts),
            NamedOnnxValue.CreateFromTensor("enemy_power_mask", enemyPowerMask), NamedOnnxValue.CreateFromTensor("intents", intents),
            NamedOnnxValue.CreateFromTensor("intent_values", intentValues), NamedOnnxValue.CreateFromTensor("intent_mask", intentMask),
        ]);
        inputs.AddRange(Bag("relics", "relic_mask", obs.Relics.Select(r => r.UsedUp == true ? r.Id + ":used" : r.Id).ToList()));
        inputs.AddRange(Bag("potions", "potion_mask", obs.Potions.OfType<string>().ToList()));
        inputs.AddRange(Bag("context", "context_mask",
            ["character:" + pl.Character, "encounter:" + (obs.Encounter ?? "<none>"), "room:" + (obs.Room ?? "<none>"), $"act:{obs.Act}"]));
        using var results = _session.Run(inputs);
        return (results[0].AsEnumerable<float>().ToArray(), results[1].AsEnumerable<float>().First());
    }

    private static IReadOnlyList<CardView> Pile(CombatObservation obs, string pile) => pile switch
    {
        "hand" => obs.Hand,
        "draw" => obs.Draw,
        "discard" => obs.Discard,
        _ => obs.Exhaust,
    };

    private static float Capped(double x) => (float)Math.Clamp(x, -Cap, Cap);

    /// <summary>Powers as the graph takes them: ids, amounts over 10 and their mask, padded to at least 1.</summary>
    private NamedOnnxValue[] Powers(string ids, string amounts, string mask, IReadOnlyList<PowerView> powers)
    {
        int n = Math.Max(1, powers.Count);
        var index = new DenseTensor<long>([1, n]);
        var amount = new DenseTensor<float>([1, n]);
        var present = new DenseTensor<float>([1, n]);
        for (int k = 0; k < powers.Count; k++)
        {
            index[0, k] = Index("powers", powers[k].Id);
            amount[0, k] = Capped(powers[k].Amount / 10.0);
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

    private int Index(string table, string id) => _index[table].GetValueOrDefault(id);
}
