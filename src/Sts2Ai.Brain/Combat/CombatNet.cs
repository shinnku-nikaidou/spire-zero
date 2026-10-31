using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>A round's start and what the trainer's net made of it, which loading checks (docs: learning/reference §7, input parity).</summary>
public sealed record CombatNetTest(CombatObservation Obs, IReadOnlyList<EnemyTurns> History, IReadOnlyList<double> Outcome, double PotionsUsed);

/// <summary>
/// The combat net as <c>learn/combat/export.py</c> writes it, or as the removed <c>learn/train_combat.py</c> wrote it
/// (docs: learning/combat-net §2).
/// </summary>
/// <param name="Buckets">How many buckets of the share of max HP lost from the state to a win the head has, after its
/// first class, a death.</param>
/// <param name="LossMin">The lowest bucket's bottom (below 0: a combat that healed); the buckets span it to 1 evenly.</param>
/// <param name="Lags">How many earlier rounds of each enemy's intents the net reads.</param>
/// <param name="Round">Whether the net reads the round and how many rounds each enemy has shown.</param>
/// <param name="Vocab">Each token table's ids; an id a table does not list reads as its entry 0.</param>
/// <param name="Onnx">The net as an ONNX graph, base64: inputs as <see cref="CombatNet"/> feeds them, outputs the
/// chance of each ending and the potions used after a win.</param>
/// <param name="Env">The environment whose games it learned from, whose policy's endings it predicts: SL or NoSL. The
/// files made before nets had one (c1–c4, all NoSL's) have none.</param>
public sealed record CombatNetFile(
    string Name, int Buckets, double LossMin, int Lags, bool Round, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab, string Onnx,
    IReadOnlyList<CombatNetTest> Tests, string? Env = null);

/// <summary>
/// The learned combat leaf (docs: combat-brain/leaf §3): from a round's start, how the combat ends under the policy that
/// played the training games. The net, run by ONNX Runtime, reads the cards in each pile, the player, each enemy with
/// its intents now and in its last rounds, the relics, the potions and the encounter; a net that declares them also
/// reads the powers' stacks and kept cards, the orbs, the allies, and the ascension, orb slots and gold, and the older
/// ones (c1–c4) the cards' and relics' numbers too. It predicts what is still to come:
/// the HP share lost from here and the potions used, so the HP and potions held now count in full. A state in progress
/// is worth the endings it predicts, priced as <see cref="LeafValue"/> prices a win (the HP share now less each bucket's
/// middle, and the potions held less those used); a state whose combat is over is <see cref="LeafValue"/>'s. This class
/// only turns a state into the graph's inputs.
/// </summary>
public sealed class CombatNet : ICombatLeaf, IDisposable
{
    private static readonly string[] Piles = ["hand", "draw", "discard", "exhaust"];

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

    /// <summary>The environment whose endings it predicts (<see cref="CombatNetFile.Env"/>).</summary>
    public string? Env => _file.Env;

    /// <summary>The net of a file, once it gives every test state the trainer's outcome.</summary>
    /// <param name="weights">The prices of the endings; null for the default leaf weights.</param>
    public static CombatNet Load(CombatNetFile file, LeafWeights? weights = null)
    {
        var net = new CombatNet(file, weights ?? LeafValue.Default);
        foreach (var test in file.Tests)
        {
            var (outcome, used) = net.Predict(test.Obs, test.History);
            double error = Math.Abs(used - test.PotionsUsed);
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
        var (outcome, used) = Predict(obs, history);
        double share = obs.Player.Hp / (double)Math.Max(1, obs.Player.MaxHp);
        double width = (1 - _file.LossMin) / _file.Buckets, value = 0;
        for (int k = 1; k < outcome.Length; k++)
            value += outcome[k] * (_weights.WinBase + _weights.PerHp * (share - (_file.LossMin + (k - 0.5) * width)));
        int held = obs.Potions.Count(p => p != null);
        return value + (1 - outcome[0]) * _weights.PerPotion * (held - used);
    }

    /// <summary>
    /// The state's worth at V's prices (<see cref="PricedLeaf"/>): a death at its price, a win at the price of the HP
    /// it leaves, less the potions used since the prices were made and, in progress, those still to be used.
    /// </summary>
    public double Value(CombatStatus status, CombatObservation obs, IReadOnlyList<EnemyTurns> history, EndPrices prices)
    {
        if (status == CombatStatus.Lost) return prices.Death;
        double maxHp = Math.Max(1, obs.Player.MaxHp);
        int spent = prices.Potions - obs.Potions.Count(p => p != null);
        if (status == CombatStatus.Won) return HpPrice(prices, obs.Player.Hp) - prices.Potion * spent;
        var (outcome, used) = Predict(obs, history);
        double share = obs.Player.Hp / maxHp, width = (1 - _file.LossMin) / _file.Buckets;
        double value = outcome[0] * prices.Death;
        for (int k = 1; k < outcome.Length; k++)
            value += outcome[k] * HpPrice(prices, (share - (_file.LossMin + (k - 0.5) * width)) * maxHp);
        return value - (1 - outcome[0]) * prices.Potion * (spent + used);
    }

    /// <summary>The price of a win with this HP left, at least 1 and at most the table's: between two HPs, in proportion.</summary>
    private static double HpPrice(EndPrices prices, double hp)
    {
        double h = Math.Clamp(hp, 1, prices.Hp.Count - 1);
        int low = (int)Math.Floor(h);
        return low >= prices.Hp.Count - 1 ? prices.Hp[^1] : prices.Hp[low] + (h - low) * (prices.Hp[low + 1] - prices.Hp[low]);
    }

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// The chance of each ending (a death, then each bucket of HP lost) and the potions used after a win: the graph on a
    /// batch of one state, read as <c>learn/combat/encode.py</c> reads it (each input fed only if the graph declares it). An enemy's slots are its intents now, then
    /// those of its last rounds before this one, most recent first.
    /// </summary>
    public (float[] Outcome, float PotionsUsed) Predict(CombatObservation obs, IReadOnlyList<EnemyTurns> history)
    {
        var cards = Piles.SelectMany((pile, p) => Pile(obs, pile).Select(c => (Card: c, Pile: p))).ToList();
        int n = Math.Max(1, cards.Count);
        var cardIds = new DenseTensor<long>([1, n]);
        var ups = new DenseTensor<float>([1, n]);
        // OLD NETS: costs (here and below) and the cards' and relics' numbers are c1–c4's inputs; remove once no
        // champion uses them.
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
        // OLD NETS: c1–c4's. What the first nets did not read, fed only to a graph that declares it ("costs" is already in).
        inputs.AddRange(NetNumbers.Cards(cards.Select(c => c.Card).ToList(), Index).Where(v => v.Name != "costs"));
        inputs.AddRange(PowerNumbers(pl.Powers));
        var enemyStacks = new DenseTensor<float>([1, e_n, q_n]);
        var enemyCards = new DenseTensor<long>([1, e_n, q_n]);
        var enemyCardMask = new DenseTensor<float>([1, e_n, q_n]);
        for (int k = 0; k < enemies.Count; k++)
            for (int j = 0; j < enemies[k].Powers.Count; j++)
            {
                var power = enemies[k].Powers[j];
                enemyStacks[0, k, j] = power.Stacks is { } stacks ? Capped(stacks / 10.0) : 0;
                if (power.Card is not { } card) continue;
                enemyCards[0, k, j] = Index("cards", card.Id);
                enemyCardMask[0, k, j] = 1;
            }
        inputs.AddRange([
            NamedOnnxValue.CreateFromTensor("enemy_power_stacks", enemyStacks), NamedOnnxValue.CreateFromTensor("enemy_power_cards", enemyCards),
            NamedOnnxValue.CreateFromTensor("enemy_power_card_mask", enemyCardMask),
        ]);
        // OLD NETS: c1–c4's.
        inputs.AddRange(NetNumbers.Relics(obs.Relics, Index));
        inputs.AddRange(Orbs(pl.Orbs));
        inputs.AddRange(Allies(obs.Allies.Where(a => a.Hp > 0).ToList()));
        var extra = new DenseTensor<float>([1, 3]);
        extra[0, 0] = (float)(obs.Ascension / 10.0);
        extra[0, 1] = (float)(pl.OrbSlots / 10.0);
        extra[0, 2] = Capped(pl.Gold / 100.0);
        inputs.Add(NamedOnnxValue.CreateFromTensor("extra", extra));
        using var results = _session.Run(NetNumbers.Declared(_session, inputs));
        return (results[0].AsEnumerable<float>().ToArray(), results[1].AsEnumerable<float>().First());
    }

    /// <summary>The player's powers' stacks / 10 (0 where the tooltip states none apart) and kept cards.</summary>
    private NamedOnnxValue[] PowerNumbers(IReadOnlyList<PowerView> powers)
    {
        int n = Math.Max(1, powers.Count);
        var stacks = new DenseTensor<float>([1, n]);
        var cards = new DenseTensor<long>([1, n]);
        var mask = new DenseTensor<float>([1, n]);
        for (int k = 0; k < powers.Count; k++)
        {
            stacks[0, k] = powers[k].Stacks is { } s ? Capped(s / 10.0) : 0;
            if (powers[k].Card is not { } card) continue;
            cards[0, k] = Index("cards", card.Id);
            mask[0, k] = 1;
        }
        return
        [
            NamedOnnxValue.CreateFromTensor("power_stacks", stacks), NamedOnnxValue.CreateFromTensor("power_cards", cards),
            NamedOnnxValue.CreateFromTensor("power_card_mask", mask),
        ];
    }

    /// <summary>The player's orbs: ids, passive and evoke numbers / 10, and their mask.</summary>
    private NamedOnnxValue[] Orbs(IReadOnlyList<OrbView> orbs)
    {
        int n = Math.Max(1, orbs.Count);
        var ids = new DenseTensor<long>([1, n]);
        var values = new DenseTensor<float>([1, n, 2]);
        var mask = new DenseTensor<float>([1, n]);
        for (int k = 0; k < orbs.Count; k++)
        {
            ids[0, k] = Index("orbs", orbs[k].Id);
            values[0, k, 0] = Capped((double)orbs[k].Passive / 10);
            values[0, k, 1] = Capped((double)orbs[k].Evoke / 10);
            mask[0, k] = 1;
        }
        return [NamedOnnxValue.CreateFromTensor("orbs", ids), NamedOnnxValue.CreateFromTensor("orb_values", values), NamedOnnxValue.CreateFromTensor("orb_mask", mask)];
    }

    /// <summary>The living allies: ids (a player as "&lt;player&gt;"), HP share, HP, max HP and block, and their powers.</summary>
    private NamedOnnxValue[] Allies(IReadOnlyList<CreatureView> allies)
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
            ids[0, k] = Index("allies", a.Id ?? "<player>");
            double[] x = [a.Hp / (double)Math.Max(1, a.MaxHp), a.Hp / 100.0, a.MaxHp / 100.0, a.Block / 20.0];
            for (int i = 0; i < x.Length; i++) scalars[0, k, i] = Capped(x[i]);
            mask[0, k] = 1;
            for (int j = 0; j < a.Powers.Count; j++)
            {
                powers[0, k, j] = Index("powers", a.Powers[j].Id);
                amounts[0, k, j] = Capped(a.Powers[j].Amount / 10.0);
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

    private static IReadOnlyList<CardView> Pile(CombatObservation obs, string pile) => pile switch
    {
        "hand" => obs.Hand,
        "draw" => obs.Draw,
        "discard" => obs.Discard,
        _ => obs.Exhaust,
    };

    private static float Capped(double x) => NetNumbers.Capped(x);

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

    /// <summary>An id's entry in a token table; 0 for an id the table does not list, or a table the file does not have.</summary>
    private long Index(string table, string id) => _index.TryGetValue(table, out var ids) ? ids.GetValueOrDefault(id) : 0;
}
