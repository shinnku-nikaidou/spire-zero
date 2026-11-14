//! The two numbers search tells states apart by. The hidden key is `HiddenState.Key` and `HiddenState.Full` of
//! src/Sts2Ai.GameBridge/HiddenState.cs (the SL world's `ICombatWorld.HiddenKey` is `Full`); the state key is
//! `PimcBrain.StateKey` of the observation (protocol.rs). Each is the hash of a text the C# side builds and then
//! writes as a JSON string; here the text goes into the hash as it is made and is never held.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::protocol::{self, Escaped, Fnv, Sink};
use crate::rt::{self, Combat, Str};

/// C#: ProtocolJson.Hash of a string: the hash of the text as a JSON string. `text` writes the text.
fn hash_of_text(text: impl FnOnce(&mut Escaped<Fnv>)) -> u64 {
    let mut hash = Fnv::new();
    hash.put(b"\"");
    text(&mut Escaped(&mut hash));
    hash.put(b"\"");
    hash.0
}

/// C#: `Enum.GetValues<T>()` of an enum whose values are 0, 1, 2, .. in the order declared, as RunRngType's and
/// PlayerRngType's are.
fn values_from_zero<T: Copy>(make: fn(i32) -> T, named: fn(T) -> bool) -> Vec<T> {
    (0..).map(make).take_while(|value| named(*value)).collect()
}

/// The monsters `HiddenState` reads: of the enemies and then the allies, each creature's monster that is mutable.
fn monsters(st: CombatState, c: &mut Combat) -> Vec<MonsterModel> {
    let mut creatures = g::combat::combat_state::enemies(st, c);
    creatures.extend(g::combat::combat_state::allies(st, c));
    let mut monsters = Vec::new();
    for creature in creatures {
        let monster = g::entities_creatures::creature::monster(creature, c);
        if !rt::is_null(&monster) && g::models::abstract_model::is_mutable(monster, c) {
            monsters.push(monster);
        }
    }
    monsters
}

/// C#: `rng.ToSerializable().counter`, which is the stream's private call counter.
fn counter(rng: Rng, c: &mut Combat) -> i32 {
    RngData::of(rng, c)._counter
}

/// The text `HiddenState.Key` hashes: the draw pile's card keys in order, the position of every random stream, and
/// each monster's queued move. `draw` is those card keys, as the observation of the same state read them
/// (`observe::seen`).
pub fn key_text<S: Sink>(st: CombatState, player: Player, draw: &[Vec<u8>], c: &mut Combat, out: &mut S) {
    for key in draw {
        out.put(key);
        out.put(b"|");
    }
    let run_state = g::combat::combat_state::run_state(st, c);
    let run_rng = g::dispatch::i_run_state::rng(run_state, c);
    for kind in values_from_zero(RunRngType, |v| v.name().is_some()) {
        let rng = g::runs::run_rng_set::get_rng(run_rng, kind, c);
        protocol::int(counter(rng, c) as i64, out);
        out.put(b",");
    }
    let player_rng = g::entities_players::player::player_rng(player, c);
    for kind in values_from_zero(PlayerRngType, |v| v.name().is_some()) {
        let rng = g::random::player_rng_set::get_rng(player_rng, kind, c);
        protocol::int(counter(rng, c) as i64, out);
        out.put(b",");
    }
    for monster in monsters(st, c) {
        let rng = g::models::monster_model::rng(monster, c);
        protocol::int(counter(rng, c) as i64, out);
        out.put(b":");
        let next = g::models::monster_model::next_move(monster, c);
        if !rt::is_null(&next) {
            let id = g::dispatch::monster_state::id(next, c);
            put_text(id, out);
        }
        out.put(b",");
    }
}

/// C#: StringBuilder.Append(string): a null string adds nothing.
fn put_text<S: Sink>(text: Str, out: &mut S) {
    if !text.is_null() {
        out.put(text.as_str().as_bytes());
    }
}

/// C#: HiddenState.Key.
pub fn key(st: CombatState, player: Player, draw: &[Vec<u8>], c: &mut Combat) -> u64 {
    hash_of_text(|out| key_text(st, player, draw, c, out))
}

/// The text `HiddenState.Full` hashes: `Key`, each monster's moves so far, and what the combat's history holds of this
/// turn, as a multiset.
pub fn full_text<S: Sink>(st: CombatState, player: Player, draw: &[Vec<u8>], c: &mut Combat, out: &mut S) {
    protocol::uint(key(st, player, draw, c), out);
    out.put(b"#");
    for monster in monsters(st, c) {
        let machine = g::models::monster_model::move_state_machine(monster, c);
        if rt::is_null(&machine) {
            continue;
        }
        let log = g::monster_moves_monster_move_state_machine::monster_move_state_machine::state_log(machine, c);
        for (at, state) in log.into_iter().enumerate() {
            if at > 0 {
                out.put(b",");
            }
            let id = g::dispatch::monster_state::id(state, c);
            put_text(id, out);
        }
        out.put(b"|");
    }
    let manager = g::combat::combat_manager::instance(c);
    let history = g::combat::combat_manager::history(manager, c);
    let mut entries = Vec::new();
    for e in g::combat_history::combat_history::entries(history, c) {
        if g::combat_history::combat_history_entry::happened_this_turn(e, st, c) {
            entries.push(entry(e, c));
        }
    }
    // C#: Order(StringComparer.Ordinal), by UTF-16 code unit.
    entries.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));
    for (at, text) in entries.iter().enumerate() {
        if at > 0 {
            out.put(b";");
        }
        out.put(text.as_bytes());
    }
}

/// C#: HiddenState.Full. The SL world's hidden key.
pub fn full(st: CombatState, player: Player, draw: &[Vec<u8>], c: &mut Combat) -> u64 {
    hash_of_text(|out| full_text(st, player, draw, c, out))
}

/// C#: `creature?.CombatId` in an interpolated string: nothing for no creature or no id.
fn combat_id(creature: Creature, c: &mut Combat) -> String {
    if rt::is_null(&creature) {
        return String::new();
    }
    g::entities_creatures::creature::combat_id(creature, c)
        .map(|id| id.to_string())
        .unwrap_or_default()
}

/// C#: HiddenState.Entry. A history entry as its kind, who acted, and what it acted with.
fn entry(e: CombatHistoryEntry, c: &mut Combat) -> String {
    let kind = g::state::kind_of(e, c);
    let actor = g::combat_history::combat_history_entry::actor(e, c);
    let mut text = format!("{}:{}:", kind_name(kind).as_str(), combat_id(actor, c));
    let id = |model: AbstractModel, c: &mut Combat| g::models::abstract_model::id(model, c).entry.as_str();
    let is = |kind: Kind, c: &mut Combat| rt::is(e, kind, c);
    if is(Kind::CardPlayStartedEntry, c) || is(Kind::CardPlayFinishedEntry, c) {
        let play = if is(Kind::CardPlayStartedEntry, c) {
            g::combat_history_entries::card_play_started_entry::card_play(e, c)
        } else {
            g::combat_history_entries::card_play_finished_entry::card_play(e, c)
        };
        let card = g::entities_cards::card_play::card(play, c);
        let target = g::entities_cards::card_play::target(play, c);
        text.push_str(id(card, c));
        text.push('>');
        text.push_str(&combat_id(target, c));
    } else if is(Kind::CardDrawnEntry, c) {
        text.push_str(id(g::combat_history_entries::card_drawn_entry::card(e, c), c));
    } else if is(Kind::CardDiscardedEntry, c) {
        text.push_str(id(g::combat_history_entries::card_discarded_entry::card(e, c), c));
    } else if is(Kind::CardExhaustedEntry, c) {
        text.push_str(id(g::combat_history_entries::card_exhausted_entry::card(e, c), c));
    } else if is(Kind::CardGeneratedEntry, c) {
        text.push_str(id(g::combat_history_entries::card_generated_entry::card(e, c), c));
    } else if is(Kind::CardAfflictedEntry, c) {
        text.push_str(id(g::combat_history_entries::card_afflicted_entry::card(e, c), c));
    } else if is(Kind::PotionUsedEntry, c) {
        text.push_str(id(g::combat_history_entries::potion_used_entry::potion(e, c), c));
    } else if is(Kind::PowerReceivedEntry, c) {
        let power = g::combat_history_entries::power_received_entry::power(e, c);
        text.push_str(id(power, c));
        text.push_str(&g::combat_history_entries::power_received_entry::amount(e, c).to_string());
    } else if is(Kind::BlockGainedEntry, c) {
        text.push_str(&g::combat_history_entries::block_gained_entry::amount(e, c).to_string());
    } else if is(Kind::EnergySpentEntry, c) {
        text.push_str(&g::combat_history_entries::energy_spent_entry::amount(e, c).to_string());
    }
    text
}
