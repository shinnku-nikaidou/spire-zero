using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>A round's start and what the trainer's net made of it, which loading checks (docs: learning/reference §7, input parity).</summary>
public sealed record CombatNetTest(CombatObservation Obs, IReadOnlyList<EnemyTurns> History, IReadOnlyList<double> Outcome, IReadOnlyList<double> PotionUsed);

/// <summary>The combat net as <c>learn/combat/export.py</c> writes it (docs: combat-brain/network).</summary>
/// <param name="Buckets">How many buckets of the share of max HP lost from the state to a win the head has, after its
/// first class, a death.</param>
/// <param name="LossMin">The lowest bucket's bottom (below 0: a combat that healed); the buckets span it to 1 evenly.</param>
/// <param name="Lags">How many earlier rounds of each enemy's intents the net reads.</param>
/// <param name="Vocab">Each token table's ids; an id a table does not list reads as its entry 0.</param>
/// <param name="Onnx">The net as an ONNX graph, base64: inputs as <see cref="CombatNet"/> feeds them, outputs the
/// chance of each ending (those the HP held rules out at 0) and each potion id's chance of being gone after a win.</param>
public sealed record CombatNetFile(
    string Name, int Buckets, double LossMin, int Lags, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab, string Onnx,
    IReadOnlyList<CombatNetTest> Tests);

/// <summary>
/// The learned combat leaf (docs: combat-brain/leaf §3): from a round's start, how the combat ends under the policy that
/// played the training games. The net, run by ONNX Runtime, reads the cards in each pile, the player, each enemy with
/// its intents now and in its last rounds, the powers, relics, potions, orbs and allies, the encounter and the character. It predicts
/// what is still to come: the HP share lost from here and which potions go, so the HP and potions held now count in full.
/// Every ending is priced by V (<see cref="IRunValue"/>) on the run it leaves: a death ends the run, a win is worth V of
/// the run then. That run is the one the leaf state stands in (<see cref="ICombatWorld.Run"/>: the deck, relics, max HP
/// and gold as the combat has left them so far) with the HP the bucket leaves and the potions kept, each subset of those
/// held by the chance the net gives it (each potion's taken apart from the others'). A finished combat is V of its run.
/// This class only turns a state into the graph's inputs, and its endings into runs for V.
/// </summary>
public sealed class CombatNet : ICombatLeaf, IDisposable
{
    /// <summary>Every scaled number the net reads is capped at ±this: a few states show huge ones (an erupting
    /// WATERFALL_GIANT's HP).</summary>
    private const double Cap = 10;

    private static readonly string[] Piles = ["hand", "draw", "discard", "exhaust"];

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

    /// <summary>The value, the chance not to die (1 less the death class), and the HP held at a win: the HP share now
    /// less each win bucket's middle, weighted by the buckets.</summary>
    public LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history)
    {
        if (status == CombatStatus.Lost) return new(0, 0, 0);
        ulong key = Key(run);
        if (status == CombatStatus.Won) return new(Price(run, key, run.Hp, run.Potions), 1, run.Hp);
        var outputs = Graph(obs, history);
        var (outcome, potionUsed) = (outputs[0], outputs[1]);
        var held = Enumerable.Range(0, run.Potions.Count).Where(s => run.Potions[s] != null).ToList();
        var kept = held.Select(s => 1.0 - potionUsed[Index("potions", run.Potions[s]!)]).ToList();
        int maxHp = Math.Max(1, run.MaxHp);
        double share = run.Hp / (double)maxHp;
        double width = (1 - _file.LossMin) / _file.Buckets, value = 0, left = 0;
        for (int k = 1; k < outcome.Length; k++)
        {
            if (outcome[k] == 0) continue;
            double after = share - (_file.LossMin + (k - 0.5) * width);
            int hp = Math.Clamp((int)Math.Round(after * maxHp), 1, maxHp);
            for (int mask = 0; mask < 1 << held.Count; mask++)
            {
                double chance = outcome[k];
                var potions = run.Potions.ToArray();
                for (int i = 0; i < held.Count; i++)
                {
                    bool keeps = (mask >> i & 1) == 1;
                    chance *= keeps ? kept[i] : 1 - kept[i];
                    if (!keeps) potions[held[i]] = null;
                }
                if (chance > 0) value += chance * Price(run, key, hp, potions);
            }
            left += outcome[k] * after;
        }
        double win = 1 - outcome[0];
        return new(value, win, win > 0 ? Math.Clamp(left / win, 0, 1) * maxHp : 0);
    }

    /// <summary>The run without its HP and potions, hashed: what the leaf's prices of one state share.</summary>
    private static ulong Key(RunView run) => ProtocolJson.Hash(run with { Hp = 0, Potions = [] });

    /// <summary>V of <paramref name="run"/> (whose <see cref="Key"/> is <paramref name="key"/>) at <paramref name="hp"/> with <paramref name="potions"/>.</summary>
    private double Price(RunView run, ulong key, int hp, IReadOnlyList<string?> potions)
    {
        var at = (key, hp, string.Join(",", potions));
        if (_prices.TryGetValue(at, out double known)) return known;
        if (_prices.Count >= Kept) _prices.Clear();
        return _prices[at] = _value.Value(run with { Hp = hp, Potions = potions });
    }

    public void Dispose() => _session.Dispose();

    /// <summary>The graph's outputs on a batch of one state, read as <c>learn/combat/encode.py</c> reads it.</summary>
    private float[][] Graph(CombatObservation obs, IReadOnlyList<EnemyTurns> history)
    {
        var pl = obs.Player;
        var inputs = new List<NamedOnnxValue>();
        inputs.AddRange(Cards(obs));
        inputs.AddRange(Powers("powers", "power_amounts", "power_mask", pl.Powers));
        inputs.AddRange(PowerNumbers(pl.Powers));
        inputs.Add(Player(obs));
        inputs.AddRange(Enemies(obs, history));
        inputs.AddRange(Bag("relics", "relic_mask", [.. obs.Relics.Select(r => r.UsedUp == true ? r.Id + ":used" : r.Id)]));
        inputs.AddRange(RelicNumbers(obs.Relics));
        inputs.AddRange(Bag("potions", "potion_mask", [.. obs.Potions.OfType<string>()]));
        inputs.AddRange(Bag("context", "context_mask",
            ["character:" + pl.Character, "encounter:" + (obs.Encounter ?? "<none>"), "room:" + (obs.Room ?? "<none>"), $"act:{obs.Act}"]));
        inputs.AddRange(Bag("orbs", "orb_mask", [.. pl.Orbs.Select(o => o.Id)]));
        var orbValues = new DenseTensor<float>([1, Math.Max(1, pl.Orbs.Count), 2]);
        for (int k = 0; k < pl.Orbs.Count; k++)
        {
            orbValues[0, k, 0] = Capped((double)pl.Orbs[k].Passive / 10.0);
            orbValues[0, k, 1] = Capped((double)pl.Orbs[k].Evoke / 10.0);
        }
        inputs.Add(NamedOnnxValue.CreateFromTensor("orb_values", orbValues));
        inputs.AddRange(Allies([.. obs.Allies.Where(a => a.Hp > 0)]));
        var extra = new DenseTensor<float>([1, 3]);
        extra[0, 0] = (float)(obs.Ascension / 10.0);
        extra[0, 1] = (float)(pl.OrbSlots / 10.0);
        extra[0, 2] = Capped(pl.Gold / 100.0);
        inputs.Add(NamedOnnxValue.CreateFromTensor("extra", extra));
        using var results = _session.Run(inputs);
        return [.. results.Select(r => r.AsEnumerable<float>().ToArray())];
    }

    /// <summary>The cards of every pile: id, upgrade level, pile, cost / 3 (0 for X or none), the modifiers' tokens
    /// (<see cref="Modifiers"/>) and the enchantment's and affliction's amounts / 10, damage / 20 (Osty's for an Osty
    /// attack, 0 for none), and the numbers: block / 20, the calculated number / 20, replays / 2, star cost / 5 and the
    /// damage its enchantment gathered / 20 (Momentum's).</summary>
    private NamedOnnxValue[] Cards(CombatObservation obs)
    {
        var cards = Piles.SelectMany((pile, p) => Pile(obs, pile).Select(c => (Card: c, Pile: p, Mods: Modifiers(c)))).ToList();
        int n = Math.Max(1, cards.Count);
        int m = Math.Max(1, cards.Select(c => c.Mods.Count).DefaultIfEmpty(0).Max());
        var ids = new DenseTensor<long>([1, n]);
        var ups = new DenseTensor<float>([1, n]);
        var costs = new DenseTensor<float>([1, n]);
        var damages = new DenseTensor<float>([1, n]);
        var piles = new DenseTensor<float>([1, n, Piles.Length]);
        var mods = new DenseTensor<long>([1, n, m]);
        var modMask = new DenseTensor<float>([1, n, m]);
        var modAmounts = new DenseTensor<float>([1, n, m]);
        var numbers = new DenseTensor<float>([1, n, 5]);
        for (int k = 0; k < cards.Count; k++)
        {
            var (card, pile, modifiers) = cards[k];
            ids[0, k] = Index("cards", card.Id);
            ups[0, k] = Math.Min(card.Up ?? 0, RunNet.MaxUp);
            costs[0, k] = card.Cost is { } cost && cost > 0 ? (float)(cost / 3.0) : 0;
            piles[0, k, pile] = 1;
            var vars = card.Vars ?? new Dictionary<string, decimal>();
            damages[0, k] = Capped((double)(vars.TryGetValue("Damage", out var damage) ? damage : vars.GetValueOrDefault("OstyDamage")) / 20.0);
            for (int j = 0; j < modifiers.Count; j++)
            {
                mods[0, k, j] = Index("modifiers", modifiers[j]);
                modMask[0, k, j] = 1;
            }
            int[] amounts = [.. new[] { card.Enchant, card.Affliction }.OfType<ModifierView>().Select(x => x.Amount)];
            for (int j = 0; j < amounts.Length; j++) modAmounts[0, k, j] = Capped(amounts[j] / 10.0);
            double[] x = [(double)vars.GetValueOrDefault("Block") / 20.0, (double)(card.Calc ?? 0) / 20.0, (card.Replay ?? 0) / 2.0,
                (card.Stars ?? 0) / 5.0, (card.Enchant?.Extra ?? 0) / 20.0];
            for (int i = 0; i < x.Length; i++) numbers[0, k, i] = Capped(x[i]);
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

    /// <summary>The player's HP share, HP, max HP, block, energy, max energy and stars, and the round.</summary>
    private static NamedOnnxValue Player(CombatObservation obs)
    {
        var pl = obs.Player;
        double maxHp = Math.Max(1, pl.MaxHp);
        double[] player = [pl.Hp / maxHp, pl.Hp / 100.0, pl.MaxHp / 100.0, pl.Block / 20.0, pl.Energy / 3.0, pl.MaxEnergy / 3.0, pl.Stars / 5.0, obs.Round / 10.0];
        var tensor = new DenseTensor<float>([1, player.Length]);
        for (int i = 0; i < player.Length; i++) tensor[0, i] = Capped(player[i]);
        return NamedOnnxValue.CreateFromTensor("player", tensor);
    }

    /// <summary>The enemies, one lying dead until it revives included: id, HP share, HP, max HP, block and how many rounds
    /// each has shown, its powers, and its slots of intents: those now, then those of its last rounds before this one,
    /// most recent first.</summary>
    private NamedOnnxValue[] Enemies(CombatObservation obs, IReadOnlyList<EnemyTurns> history)
    {
        var past = history.ToDictionary(h => h.Uid, h => h.Turns.Where(t => t.Round < obs.Round).ToList());
        var enemies = obs.Enemies;
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
        var ids = new DenseTensor<long>([1, e_n]);
        var scalars = new DenseTensor<float>([1, e_n, 5]);
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
            ids[0, k] = Index("enemies", e.Id ?? "<none>");
            mask[0, k] = 1;
            double enemyMax = Math.Max(1, e.MaxHp);
            double[] numbers = [e.Hp / enemyMax, e.Hp / 100.0, e.MaxHp / 100.0, e.Block / 20.0, enemySlots[k].Turns / 10.0];
            for (int i = 0; i < numbers.Length; i++) scalars[0, k, i] = Capped(numbers[i]);
            for (int j = 0; j < e.Powers.Count; j++)
            {
                var power = e.Powers[j];
                powers[0, k, j] = Index("powers", power.Id);
                powerAmounts[0, k, j] = Capped(power.Amount / 10.0);
                powerMask[0, k, j] = 1;
                powerStacks[0, k, j] = power.Stacks is { } stacks ? Capped(stacks / 10.0) : 0;
                powerDetails[0, k, j] = Capped((double)(power.Detail ?? 0) / 20.0);
                if ((power.Card ?? power.Stolen) is not { } card) continue;
                powerCards[0, k, j] = Index("cards", card.Id);
                powerCardMask[0, k, j] = 1;
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

    /// <summary>The player's powers' stacks / 10 (0 where the tooltip states none apart), kept or stolen cards, and
    /// numbers moved from their own / 20 (else 0).</summary>
    private NamedOnnxValue[] PowerNumbers(IReadOnlyList<PowerView> powers)
    {
        int n = Math.Max(1, powers.Count);
        var stacks = new DenseTensor<float>([1, n]);
        var cards = new DenseTensor<long>([1, n]);
        var mask = new DenseTensor<float>([1, n]);
        var details = new DenseTensor<float>([1, n]);
        for (int k = 0; k < powers.Count; k++)
        {
            stacks[0, k] = powers[k].Stacks is { } s ? Capped(s / 10.0) : 0;
            details[0, k] = Capped((double)(powers[k].Detail ?? 0) / 20.0);
            if ((powers[k].Card ?? powers[k].Stolen) is not { } card) continue;
            cards[0, k] = Index("cards", card.Id);
            mask[0, k] = 1;
        }
        return
        [
            NamedOnnxValue.CreateFromTensor("power_stacks", stacks), NamedOnnxValue.CreateFromTensor("power_cards", cards),
            NamedOnnxValue.CreateFromTensor("power_card_mask", mask), NamedOnnxValue.CreateFromTensor("power_details", details),
        ];
    }

    /// <summary>The relics' numbers, in the relics' order: counter / 10 (0 when none shows), whether one shows, whether the
    /// relic is melted, and its status (when none, Spent for a once-a-combat effect used, else Normal).</summary>
    private NamedOnnxValue[] RelicNumbers(IReadOnlyList<RelicView> relics)
    {
        int n = Math.Max(1, relics.Count);
        var values = new DenseTensor<float>([1, n, 3]);
        var status = new DenseTensor<long>([1, n]);
        for (int k = 0; k < relics.Count; k++)
        {
            var relic = relics[k];
            values[0, k, 0] = relic.Counter is { } counter ? Capped(counter / 10.0) : 0;
            values[0, k, 1] = relic.Counter != null ? 1 : 0;
            values[0, k, 2] = relic.Melted == true ? 1 : 0;
            status[0, k] = Index("relic_status", relic.Status ?? (relic.Spent == true ? "Spent" : "Normal"));
        }
        return [NamedOnnxValue.CreateFromTensor("relic_values", values), NamedOnnxValue.CreateFromTensor("relic_status", status)];
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

    /// <summary>An id's entry in a token table; 0 for an id the table does not list.</summary>
    private long Index(string table, string id) => _index[table].GetValueOrDefault(id);
}
