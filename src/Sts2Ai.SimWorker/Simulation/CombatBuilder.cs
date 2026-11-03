using System.Reflection;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// Builds a sim combat that shows exactly a given observation, such as one the mod sends from the real game, so a
/// search can start where the real player is.
///
/// The combat starts fresh: the observed encounter, with a loadout made from the observation (every observed card
/// in the deck, the observed relics). Then everything the observation shows is overwritten (<see cref="Apply"/>),
/// and everything it hides is sampled from the setup's seed (<see cref="CombatSession"/>'s resampling). Only the
/// observation and that seed go in, so the sim knows nothing the player doesn't.
///
/// Uids: the creatures and the cards in hand get the uids the observation names, so an action found in the sim
/// names the same card or creature in the real game. The other cards get fresh ids above those.
///
/// With the enemies' intents in the rounds so far (Harness's record of them, which the player saw), each monster gets
/// the log of the moves it made (<see cref="MoveLogs"/>), which its choice of the next move can depend on.
///
/// Not reproduced, because the observation doesn't show it: this turn's history (cards played, damage taken, ...),
/// counters kept inside powers and monsters, how long a card's cost change lasts (a card in hand is taken to be
/// cheaper this turn or until played, a card elsewhere for the rest of the combat), and which debuffs on the player
/// skip their next tick.
/// </summary>
internal static class CombatBuilder
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo TurnNumber = typeof(PlayerCombatState).GetField("<TurnNumber>k__BackingField", Private)!;
    private static readonly FieldInfo EnemyList = typeof(CombatState).GetField("_enemies", Private)!;
    private static readonly FieldInfo AllyList = typeof(CombatState).GetField("_allies", Private)!;
    private static readonly FieldInfo NextCreatureId = typeof(CombatState).GetField("_nextCreatureId", Private)!;
    private static readonly FieldInfo PowerList = typeof(Creature).GetField("_powers", Private)!;
    private static readonly FieldInfo InternalData = typeof(PowerModel).GetField("_internalData", Private)!;
    private static readonly FieldInfo PerformedFirstMove = typeof(MonsterMoveStateMachine).GetField("_performedFirstMove", Private)!;
    private static readonly FieldInfo NextCardId = typeof(NetCombatCardDb).GetField("_nextId", Private)!;

    /// <summary>The setup that builds <paramref name="obs"/>; <paramref name="seed"/> picks the hidden state.</summary>
    /// <param name="history">The enemies' intents in the rounds so far, for the monsters' move logs; none leaves each
    /// monster the log of a fresh combat.</param>
    public static CombatSetup Setup(CombatObservation obs, string seed, IReadOnlyList<EnemyTurns>? history = null)
    {
        var encounter = Models.Find<EncounterModel>(obs.Encounter ?? throw new ArgumentException("the observation names no encounter"));
        var deck = obs.Hand.Concat(obs.Draw).Concat(obs.Discard).Concat(obs.Exhaust)
            .Select(c => new CardSpec(c.Id, c.Up ?? 0, c.Enchant?.Id, c.Enchant?.Amount ?? 0)).ToList();
        var loadout = new Loadout(deck, obs.Relics.Select(r => r.Id).ToList(), Potions: [], obs.Player.Hp, obs.Player.MaxHp);
        return new CombatSetup(obs.Player.Character, encounter, obs.Act, seed, obs.Ascension, loadout, From: obs, History: history);
    }

    /// <summary>Overwrites the freshly started combat with everything <paramref name="obs"/> shows.</summary>
    public static void Apply(CombatState st, Player player, CombatObservation obs, Pump pump, IReadOnlyList<EnemyTurns>? history = null)
    {
        var pcs = player.PlayerCombatState!;
        st.RoundNumber = obs.Round;
        TurnNumber.SetValue(pcs, obs.Round);

        Enemies(st, obs.Enemies, pump);
        var allies = Allies(st, player, obs.Allies, pump);
        player.Creature.CombatId = obs.Player.Uid;
        foreach (var (enemy, view) in st.Enemies.Zip(obs.Enemies)) enemy.CombatId = view.Uid;
        foreach (var (ally, view) in allies.Zip(obs.Allies)) ally.CombatId = view.Uid;
        NextCreatureId.SetValue(st, st.Creatures.Max(c => c.CombatId ?? 0) + 1);

        Relics(player, obs.Relics);
        Powers(st, player, player.Creature, obs.Player.Powers);
        foreach (var (enemy, view) in st.Enemies.Zip(obs.Enemies)) Powers(st, player, enemy, view.Powers);
        foreach (var (ally, view) in allies.Zip(obs.Allies)) Powers(st, player, ally, view.Powers);
        // After the powers, which can change max HP and max energy.
        Stats(player.Creature, obs.Player.Hp, obs.Player.MaxHp, obs.Player.Block);
        foreach (var (enemy, view) in st.Enemies.Zip(obs.Enemies)) Stats(enemy, view.Hp, view.MaxHp, view.Block);
        foreach (var (ally, view) in allies.Zip(obs.Allies)) Stats(ally, view.Hp, view.MaxHp, view.Block);
        player.MaxEnergy += obs.Player.MaxEnergy - pcs.MaxEnergy;
        pcs.Energy = obs.Player.Energy;
        pcs.Stars = obs.Player.Stars;
        player.Gold = obs.Player.Gold;
        Potions(player, obs.Potions);
        Orbs(player, obs.Player.OrbSlots, obs.Player.Orbs);

        // After powers and relics, whose global modifiers show in card costs and keywords.
        Cards(st, player, obs);
        NumberCards(pcs, obs.Hand.Select(c => c.Uid ?? throw new ArgumentException($"card {c.Id} in hand has no uid")).ToList());

        // After the powers, which change the damage an intent shows.
        foreach (var machine in st.Enemies.Select(e => e.Monster?.MoveStateMachine))
            if (machine != null && obs.Round > 1) PerformedFirstMove.SetValue(machine, true);
        Moves(st, obs.Enemies, [player.Creature]);
        if (history != null) MoveLogs(st, history, obs.Round, [player.Creature]);
        pump.Drain();
    }

    /// <summary>
    /// Gives each monster the move log of the record: the move each earlier round's intents show, then its current
    /// move. A monster picks its next move by its last ones (not the same one three times, a fixed cycle), so a log
    /// that starts afresh makes it pick another move than the real one. An earlier round's numbers can differ from
    /// what the move shows now (the monster gained strength since), so a round matches the move that shows the same
    /// intents, else the same intent types and hits, else the same types. A monster with a round that no move matches
    /// keeps the log it has.
    ///
    /// A stun is not one of the monster's moves and is not logged (<see cref="Creature.StunInternal"/>): a round shown
    /// stunned is left out, and a monster stunned now goes on with the move it showed at this round's start, the last
    /// one logged, rather than the one the fresh log gave its stun.
    /// </summary>
    private static void MoveLogs(CombatState st, IReadOnlyList<EnemyTurns> history, int round, Creature[] targets)
    {
        var record = history.ToDictionary(h => h.Uid);
        foreach (var enemy in st.Enemies)
        {
            if (enemy.CombatId is not { } uid || enemy.Monster?.MoveStateMachine is not { } machine || !record.TryGetValue(uid, out var turns)) continue;
            var shown = turns.Turns.Where(t => !t.Intents.All(i => i.Type == nameof(IntentType.Stun))).OrderBy(t => t.Round).ToList();
            var past = shown.Where(t => t.Round < round).Select(t => Made(enemy, machine, targets, t.Intents)).ToList();
            if (past.Any(m => m == null)) continue;
            var log = past.Select(m => (MonsterState)m!).ToList();
            var next = enemy.Monster.NextMove;
            if (machine.States.ContainsKey(next.Id)) log.Add(next);
            else if (shown.FirstOrDefault(t => t.Round == round) is { } now && Made(enemy, machine, targets, now.Intents) is { } queued)
            {
                log.Add(queued);
                enemy.Monster.SetMoveImmediate(Stunned(queued.Id), forceTransition: true);
            }
            if (log.Count == 0) continue;
            machine.StateLog.Clear();
            machine.StateLog.AddRange(log);
        }
    }

    /// <summary>The move that showed <paramref name="intents"/> in an earlier round, or null if none could have.</summary>
    private static MoveState? Made(Creature enemy, MonsterMoveStateMachine machine, Creature[] targets, IReadOnlyList<IntentView> intents)
    {
        var moves = machine.States.Values.OfType<MoveState>().OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        foreach (var key in new Func<IReadOnlyList<IntentView>, string>[] { Key, Types, i => string.Join(",", i.Select(x => x.Type)) })
        {
            string want = key(intents);
            if (moves.FirstOrDefault(m => key(Observe.Intents(m, enemy, targets)) == want) is { } move) return move;
        }
        return null;

        static string Types(IReadOnlyList<IntentView> i) => string.Join(",", i.Select(x => $"{x.Type}x{x.Hits ?? 1}"));
    }

    /// <summary>
    /// Gives each enemy a move that shows its observed intents. What an intent shows can also depend on a hidden
    /// switch, such as which way the player faces when Surrounded (an enemy behind them hits harder), or on a number
    /// the monster keeps, such as the Waterfall Giant's pressure gun, which hits harder each time it fires. So while
    /// some enemy doesn't show its intents, each value of each enum field of a power is tried, then each value below
    /// 100 of each int field of a monster, then each one below 1,000 (the Giant's eruption, which grows all fight, can
    /// pass 100), and the one under which the most enemies show theirs is kept.
    /// </summary>
    private static void Moves(CombatState st, IReadOnlyList<CreatureView> views, Creature[] targets)
    {
        var switches = st.Creatures.SelectMany(c => c.Powers).SelectMany(Switches);
        var fields = st.Enemies.Where(e => e.Monster != null).SelectMany(e => FieldKnobs(e.Monster!, typeof(MonsterModel))).ToList();
        var numbers = fields.Select(k => (k, Enumerable.Range(0, 100).ToArray()))
            .Concat(fields.Select(k => (k, Enumerable.Range(100, 900).ToArray())));
        int best = SetMoves(st, views, targets);
        foreach (var (knob, values) in switches.Concat(numbers).ToList())
        {
            if (best == views.Count) return;
            int keep = knob.Get();
            foreach (int value in values)
            {
                knob.Set(value);
                int shown = SetMoves(st, views, targets);
                if (shown > best) (best, keep) = (shown, value);
            }
            knob.Set(keep);
            SetMoves(st, views, targets);
        }
    }

    /// <summary>Sets each enemy's move to one that shows its observed intents; returns how many enemies show them.</summary>
    private static int SetMoves(CombatState st, IReadOnlyList<CreatureView> views, Creature[] targets)
    {
        int shown = 0;
        foreach (var (enemy, view) in st.Enemies.Zip(views))
        {
            if (view.Intents == null || view.Hp <= 0 || enemy.Monster?.MoveStateMachine is not { } machine) { shown++; continue; }
            var intents = Key(view.Intents);
            if (Shows(enemy.Monster.NextMove, enemy, targets) != intents)
            {
                var move = MovesShowing(enemy, targets, intents).FirstOrDefault() ?? Stunned(machine.StateLog.Last().Id);
                if (Shows(move, enemy, targets) == intents) enemy.Monster.SetMoveImmediate(move, forceTransition: true);
            }
            if (Shows(enemy.Monster.NextMove, enemy, targets) == intents) shown++;
        }
        return shown;
    }

    /// <summary>
    /// The move of a stunned monster, made on the spot as the game's <see cref="Creature.StunInternal"/> makes it,
    /// since it is not one of the monster's own. On its turn the real one does what the stun's cause gives it to do;
    /// this one does nothing, then goes on with <paramref name="followUp"/>, the last move logged.
    /// </summary>
    private static MoveState Stunned(string followUp) => new("STUNNED", _ => Task.CompletedTask, new StunIntent())
    {
        FollowUpStateId = followUp,
        MustPerformOnceBeforeTransitioning = true,
    };

    /// <summary>The monster's moves that would show the same intents as <paramref name="intents"/>, by id.</summary>
    public static List<MoveState> MovesShowing(Creature monster, Creature[] targets, string intents) =>
        monster.Monster!.MoveStateMachine!.States.Values.OfType<MoveState>()
            .Where(m => Shows(m, monster, targets) == intents)
            .OrderBy(m => m.Id, StringComparer.Ordinal).ToList();

    public static string Shows(MoveState move, Creature monster, Creature[] targets) => Key(Observe.Intents(move, monster, targets));

    private static string Key(IReadOnlyList<IntentView> intents) => ProtocolJson.Serialize(intents);

    /// <summary>
    /// Gives the cards in hand the given ids, in order, and every other card a fresh id above those. New cards are
    /// numbered after all of them, as in the game.
    /// </summary>
    public static void NumberCards(PlayerCombatState pcs, IReadOnlyList<uint> handIds)
    {
        var db = NetCombatCardDb.Instance;
        db.ClearCardsForTesting();
        foreach (var (card, id) in pcs.Hand.Cards.Zip(handIds))
        {
            NextCardId.SetValue(db, id);
            db.IdCardForTesting(card);
        }
        NextCardId.SetValue(db, handIds.Count == 0 ? 0u : handIds.Max() + 1);
        foreach (var card in pcs.AllPiles.SelectMany(p => p.Cards)) db.IdCardForTesting(card);
    }

    /// <summary>What a build does not set: the state a combat gathers as it goes, and which a built combat starts without.</summary>
    private static readonly HashSet<string> NotBuilt = ["lasting", "stars_lasting", "replay", "spent", "calc", "extra", "detail", "stolen", "this_turn"];

    /// <summary>
    /// Where <paramref name="actual"/> differs from <paramref name="expected"/>, as JSON paths: what the built
    /// combat failed to reproduce, leaving out <see cref="NotBuilt"/> and the moves of the dead.
    /// </summary>
    public static List<string> Differences(CombatObservation expected, CombatObservation actual)
    {
        var found = new List<string>();
        Walk("", JsonNode.Parse(ProtocolJson.Serialize(expected)), JsonNode.Parse(ProtocolJson.Serialize(actual)));
        return found;

        void Walk(string path, JsonNode? a, JsonNode? b)
        {
            if (a is JsonObject oa && b is JsonObject ob)
            {
                foreach (var key in oa.Select(kv => kv.Key).Union(ob.Select(kv => kv.Key)))
                    if (!NotBuilt.Contains(key) && !(key == "intents" && oa["hp"]?.GetValue<int>() <= 0)) Walk($"{path}.{key}", oa[key], ob[key]);
            }
            else if (a is JsonArray aa && b is JsonArray ab && aa.Count == ab.Count)
            {
                // Items with an id are named by it: ".enemies[0 AEONGLASS].powers[1 STRENGTH_POWER].amount".
                for (int i = 0; i < aa.Count; i++)
                    Walk(aa[i] is JsonObject item && item["id"] is JsonValue id ? $"{path}[{i} {id}]" : $"{path}[{i}]", aa[i], ab[i]);
            }
            else if (!JsonNode.DeepEquals(a, b))
            {
                found.Add($"{path}: want {a?.ToJsonString() ?? "none"}, got {b?.ToJsonString() ?? "none"}");
            }
        }
    }

    /// <summary>
    /// Matches the fresh combat's enemies to the observed ones by monster id, in order; spawns the missing ones the
    /// way summons arrive, removes the extra ones, and puts them in the observed order.
    /// </summary>
    private static void Enemies(CombatState st, IReadOnlyList<CreatureView> views, Pump pump)
    {
        var fresh = st.Enemies.ToList();
        var matched = new List<Creature>();
        foreach (var view in views)
        {
            var enemy = fresh.FirstOrDefault(e => e.Monster?.Id.Entry == view.Id);
            if (enemy != null) fresh.Remove(enemy);
            else
            {
                var slot = st.Encounter?.Slots.FirstOrDefault(s => st.Enemies.All(e => e.SlotName != s));
                var monster = Models.Find<MonsterModel>(view.Id ?? throw new ArgumentException("an enemy that is not a monster")).ToMutable();
                var adding = CreatureCmd.Add(monster, st, CombatSide.Enemy, slot);
                pump.RunUntil(() => adding.IsCompleted, () => $"spawning {view.Id}");
                enemy = adding.Result;
            }
            matched.Add(enemy);
        }
        foreach (var extra in fresh)
        {
            CombatManager.Instance.RemoveCreature(extra);
            st.RemoveCreature(extra);
        }
        var list = (List<Creature>)EnemyList.GetValue(st)!;
        list.Clear();
        list.AddRange(matched);
    }

    /// <summary>
    /// The allies besides the player, matched like <see cref="Enemies"/>: the fresh combat's (a relic's pet, which
    /// joins at the start) by monster id, in order. Each missing one joins as the player's pet, as a summon makes it
    /// (Osty, which any character can summon with a card from another character's pool); the extra ones are removed.
    /// </summary>
    private static List<Creature> Allies(CombatState st, Player player, IReadOnlyList<CreatureView> views, Pump pump)
    {
        var fresh = st.Allies.Where(c => c != player.Creature).ToList();
        var matched = new List<Creature>();
        foreach (var view in views)
        {
            var ally = fresh.FirstOrDefault(a => a.Monster?.Id.Entry == view.Id);
            if (ally != null) fresh.Remove(ally);
            else
            {
                var monster = Models.Find<MonsterModel>(view.Id ?? throw new ArgumentException("an ally that is not a monster")).ToMutable();
                ally = st.CreateCreature(monster, CombatSide.Player, null);
                var adding = PlayerCmd.AddPet(ally, player);
                pump.RunUntil(() => adding.IsCompleted, () => $"adding the pet {view.Id}");
            }
            matched.Add(ally);
        }
        foreach (var extra in fresh)
        {
            CombatManager.Instance.RemoveCreature(extra);
            st.RemoveCreature(extra);
        }
        var list = (List<Creature>)AllyList.GetValue(st)!;
        list.Clear();
        list.Add(player.Creature);
        list.AddRange(matched);
        return matched;
    }

    private static void Stats(Creature c, int hp, int maxHp, int block)
    {
        c.SetMaxHpInternal(maxHp);
        c.SetCurrentHpInternal(hp);
        if (c.Block > block) c.LoseBlockInternal(c.Block - block);
        else if (c.Block < block) c.GainBlockInternal(block - c.Block);
    }

    /// <summary>
    /// Makes the visible powers the observed ones, in the observed order. A power the fresh combat already has keeps
    /// its own state; a missing one is attached without its on-apply effects, which the observation already shows.
    /// Hidden powers are left alone. A card a power keeps is the player's, outside every pile.
    /// </summary>
    private static void Powers(CombatState st, Player player, Creature c, IReadOnlyList<PowerView> views)
    {
        var visible = c.Powers.Where(p => p.IsVisible).ToList();
        var wanted = new List<PowerModel>();
        foreach (var view in views)
        {
            var power = visible.FirstOrDefault(p => p.Id.Entry == view.Id);
            if (power != null) visible.Remove(power);
            else
            {
                power = Models.Find<PowerModel>(view.Id).ToMutable();
                // The game: a power's target "is used on instanced powers that each target a player".
                if (power.InstanceType != PowerInstanceType.None && c.Side == CombatSide.Enemy) power.Target = c.CombatState!.Players[0].Creature;
                power.ApplyInternal(c, (view.Stacks ?? view.Amount) is var amount and not 0 ? amount : 1, silent: true);
            }
            if (view.Stacks is int stacks) power.SetAmount(stacks, silent: true);
            if (view.Card is { } held)
            {
                var card = Create(st, player, new CardSpec(held.Id, held.Up ?? 0, held.Enchant?.Id, held.Enchant?.Amount ?? 0));
                Modify(card, held, inHand: false);
                HeldCards.Set(power, card);
            }
            Solve(() => power.DisplayAmount, view.Amount, Knobs(power, amountKnown: view.Stacks != null));
            wanted.Add(power);
        }
        foreach (var power in visible) power.RemoveInternal();

        var list = (List<PowerModel>)PowerList.GetValue(c)!;
        var places = Enumerable.Range(0, list.Count).Where(i => list[i].IsVisible).ToList();
        foreach (var (i, power) in places.Zip(wanted)) list[i] = power;
    }

    private static readonly PropertyInfo ToothCards = typeof(PaelsTooth).GetProperty(nameof(PaelsTooth.SerializableCards))!;

    private static void Relics(Player player, IReadOnlyList<RelicView> views)
    {
        foreach (var (relic, view) in player.Relics.Zip(views))
        {
            if (relic.Id.Entry != view.Id) continue;
            // Pael's Tooth's counter is how many cards it keeps, so they go in first. Their upgrades are hidden, and
            // they come back only after the combat.
            if (relic is PaelsTooth) ToothCards.SetValue(relic, (view.Cards ?? []).Select(id => new CardSpec(id).ToSerializable()).ToList());
            if (view.Counter is int counter) Solve(() => relic.DisplayAmount, counter, FieldKnobs(relic, typeof(RelicModel)));
            // A hidden counter can be one that ran out, such as Silver Crucible's after its third card reward.
            else Solve(() => relic.ShowCounter ? 1 : 0, 0, FieldKnobs(relic, typeof(RelicModel)));
            // After the counter, which can be half of it: Silver Crucible is used up once its counter ran out and a
            // treasure room was entered. A knob that would change the counter doesn't count.
            bool CounterAsObserved() => view.Counter is int c ? relic.ShowCounter && relic.DisplayAmount == c : !relic.ShowCounter;
            if (view.UsedUp == true)
                Solve(() => relic.IsUsedUp && CounterAsObserved() ? 1 : 0, 1,
                    FieldKnobs(relic, typeof(RelicModel)).Concat(FlagKnobs(relic, typeof(RelicModel))));
            relic.Status = view.Status == null ? RelicStatus.Normal : Enum.Parse<RelicStatus>(view.Status);
            relic.IsMelted = view.Melted == true;
        }
    }

    /// <summary>A number inside a model, which the number it shows may be computed from.</summary>
    private sealed record Knob(Func<int> Get, Action<int> Set);

    /// <summary>
    /// The number a power or relic shows is computed from its own state, differently for each one. Tries each knob in
    /// turn, set to a guess or a small number, until <paramref name="shown"/> gives <paramref name="want"/>; a knob
    /// that doesn't get there is put back.
    /// </summary>
    private static void Solve(Func<int> shown, int want, IEnumerable<Knob> knobs)
    {
        foreach (var knob in knobs)
        {
            if (shown() == want) return;
            int old = knob.Get();
            foreach (int value in Enumerable.Range(0, 100).Prepend(want).Prepend(old + want - shown()))
            {
                knob.Set(value);
                if (shown() == want) return;
            }
            knob.Set(old);
        }
    }

    /// <summary>
    /// A power's amount first, unless it is known (shown apart from the icon's number), then its own numbers, then its
    /// fields and those of its internal data.
    /// </summary>
    private static IEnumerable<Knob> Knobs(PowerModel power, bool amountKnown)
    {
        if (!amountKnown) yield return new(() => power.Amount, v => power.SetAmount(v, silent: true));
        foreach (var v in power.DynamicVars.Values) yield return new(() => (int)v.BaseValue, x => v.BaseValue = x);
        foreach (var knob in FieldKnobs(power, typeof(PowerModel))) yield return knob;
        // Such as Hardened Shell's damage taken this turn, a decimal, which the amount it shows is net of.
        if (InternalData.GetValue(power) is { } data)
            foreach (var knob in FieldKnobs(data, typeof(object)).Concat(DecimalKnobs(data, typeof(object)))) yield return knob;
    }

    /// <summary>The int fields declared by <paramref name="model"/>'s classes below <paramref name="baseType"/>.</summary>
    private static IEnumerable<Knob> FieldKnobs(object model, Type baseType) =>
        Fields(model, baseType).Where(f => f.FieldType == typeof(int))
            .Select(f => new Knob(() => (int)f.GetValue(model)!, v => f.SetValue(model, v)));

    /// <summary>The decimal fields, as <see cref="FieldKnobs"/>, set to whole numbers.</summary>
    private static IEnumerable<Knob> DecimalKnobs(object model, Type baseType) =>
        Fields(model, baseType).Where(f => f.FieldType == typeof(decimal))
            .Select(f => new Knob(() => (int)(decimal)f.GetValue(model)!, v => f.SetValue(model, (decimal)v)));

    /// <summary>The bool fields, as <see cref="FieldKnobs"/>: a flag reads as 0 or 1 and is set by any number but 0.</summary>
    private static IEnumerable<Knob> FlagKnobs(object model, Type baseType) =>
        Fields(model, baseType).Where(f => f.FieldType == typeof(bool))
            .Select(f => new Knob(() => (bool)f.GetValue(model)! ? 1 : 0, v => f.SetValue(model, v != 0)));

    /// <summary>The enum fields declared by a power's classes, with their values.</summary>
    private static IEnumerable<(Knob, int[])> Switches(PowerModel power) =>
        Fields(power, typeof(PowerModel)).Where(f => f.FieldType.IsEnum).Select(f => (
            new Knob(() => Convert.ToInt32(f.GetValue(power)), v => f.SetValue(power, Enum.ToObject(f.FieldType, v))),
            Enum.GetValuesAsUnderlyingType(f.FieldType).Cast<object>().Select(Convert.ToInt32).ToArray()));

    private static IEnumerable<FieldInfo> Fields(object model, Type baseType)
    {
        for (var type = model.GetType(); type != null && type != baseType; type = type.BaseType)
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return f;
    }

    /// <summary>
    /// The observed orb slots and orbs, in order, put in without their on-channel effects. An orb's numbers can drift
    /// from its fresh ones (a Glass orb loses one passive damage each time it fires), so they are solved like a power's.
    /// </summary>
    private static void Orbs(Player player, int slots, IReadOnlyList<OrbView> views)
    {
        var queue = player.PlayerCombatState!.OrbQueue;
        queue.Clear();
        queue.AddCapacity(slots);
        foreach (var (view, i) in views.Select((v, i) => (v, i)))
        {
            var orb = Models.Find<OrbModel>(view.Id).ToMutable();
            orb.Owner = player;
            queue.Insert(i, orb);
            var knobs = DecimalKnobs(orb, typeof(OrbModel)).ToList();
            Solve(() => (int)orb.PassiveVal, (int)view.Passive, knobs);
            Solve(() => (int)orb.EvokeVal, (int)view.Evoke, knobs);
        }
    }

    private static void Potions(Player player, IReadOnlyList<string?> slots)
    {
        foreach (var potion in player.Potions.ToList()) player.DiscardPotionInternal(potion, silent: true);
        int more = slots.Count - player.MaxPotionCount;
        if (more > 0) player.AddToMaxPotionCount(more);
        else if (more < 0) player.SubtractFromMaxPotionCount(-more);
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] is { } id) player.AddPotionInternal(Models.Find<PotionModel>(id).ToMutable(), i, silent: true);
    }

    /// <summary>
    /// Fills the piles with the observed cards. The fresh combat's cards are reused where id, upgrades and
    /// enchantment match (they come from the deck, as in the game); any card still missing is created, and the rest
    /// leave the combat.
    /// </summary>
    private static void Cards(CombatState st, Player player, CombatObservation obs)
    {
        var pcs = player.PlayerCombatState!;
        var spare = new Dictionary<string, Queue<CardModel>>();
        foreach (var pile in pcs.AllPiles)
        {
            foreach (var card in pile.Cards.ToList())
            {
                pile.RemoveInternal(card, silent: true);
                var key = DeckKey(card.Id.Entry, card.CurrentUpgradeLevel, card.Enchantment?.Id.Entry, card.Enchantment?.Amount ?? 0);
                if (!spare.TryGetValue(key, out var queue)) spare[key] = queue = new();
                queue.Enqueue(card);
            }
        }
        foreach (var (pile, views) in new[] { (pcs.Hand, obs.Hand), (pcs.DrawPile, obs.Draw), (pcs.DiscardPile, obs.Discard), (pcs.ExhaustPile, obs.Exhaust) })
        {
            foreach (var view in views)
            {
                var spec = new CardSpec(view.Id, view.Up ?? 0, view.Enchant?.Id, view.Enchant?.Amount ?? 0);
                var card = spare.GetValueOrDefault(DeckKey(spec.Id, spec.Upgrades, spec.Enchant, spec.EnchantAmount)) is { Count: > 0 } queue
                    ? queue.Dequeue()
                    : Create(st, player, spec);
                pile.AddInternal(card, silent: true);
                Modify(card, view, inHand: pile == pcs.Hand);
            }
        }
        foreach (var card in spare.Values.SelectMany(q => q)) st.RemoveCard(card);
    }

    private static string DeckKey(string id, int upgrades, string? enchant, int enchantAmount) =>
        enchant == null ? $"{id}+{upgrades}" : $"{id}+{upgrades} {enchant} {enchantAmount}";

    private static CardModel Create(CombatState st, Player player, CardSpec spec)
    {
        var card = CardModel.FromSerializable(spec.ToSerializable());
        st.AddCard(card, player);
        return card;
    }

    /// <summary>The changes a card picks up during a combat (affliction, numbers, keywords, cost), and what Tinker Time
    /// made of a Mad Science, which a card built from its id alone lacks.</summary>
    private static void Modify(CardModel card, CardView view, bool inHand)
    {
        if (card is MadScience mad && view.Tinker is { } tinker)
        {
            mad.TinkerTimeType = Enum.Parse<CardType>(tinker.Type);
            mad.TinkerTimeRider = Enum.Parse<TinkerTime.RiderEffect>(tinker.Rider);
        }
        if (card.Affliction?.Id.Entry != view.Affliction?.Id)
        {
            card.ClearAfflictionInternal();
            if (view.Affliction != null)
            {
                var affliction = Models.Find<AfflictionModel>(view.Affliction.Id).ToMutable();
                card.AfflictInternal(affliction, view.Affliction.Amount);
                affliction.AfterApplied();
            }
        }
        if (card.Affliction != null && card.Affliction.Amount != view.Affliction!.Amount) card.Affliction.Amount = view.Affliction.Amount;

        foreach (var (name, value) in view.Vars ?? new Dictionary<string, decimal>())
            if (card.DynamicVars.TryGetValue(name, out var v) && v.BaseValue != value) v.BaseValue = value;

        var want = view.Keywords ?? [];
        var have = card.Keywords.Select(k => k.ToString()).ToList();
        foreach (var k in have.Except(want)) card.RemoveKeyword(Enum.Parse<CardKeyword>(k));
        foreach (var k in want.Except(have)) card.AddKeyword(Enum.Parse<CardKeyword>(k));

        var cost = card.EnergyCost;
        if (!cost.CostsX && view.Cost is int energy && cost.GetWithModifiers(CostModifiers.All) != energy)
        {
            // Global modifiers (powers, relics) apply on top of the card's own; leave room for them. The card's own cost
            // may be below 0 under a global increase (Pinpoint after 3 Skills, Entangled: 0 - 3 + 1 shows 0).
            int local = energy - (cost.GetWithModifiers(CostModifiers.All) - cost.GetWithModifiers(CostModifiers.Local));
            if (inHand) cost.SetThisTurnOrUntilPlayed(local);
            else cost.SetThisCombat(local);
        }
        if (view.Stars is int stars && card.GetStarCostWithModifiers() != stars)
        {
            if (inHand) card.SetStarCostThisTurn(stars);
            else card.SetStarCostThisCombat(stars);
        }
    }
}
