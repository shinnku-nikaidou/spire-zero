//! A NoSL world from the real combat: everything the player cannot see drawn again, the rest the combat as it is
//! (plans/2026-11-17-1120-nosl-dread-architecture.md, 3.3 and 9). What the player cannot see is short and stored in a
//! few places, each drawn from what the player has seen:
//!
//! - every random stream (`MegaRandom`, the state behind each `Rng`): a new state. A stream's future is its state
//!   alone; its call counter only counts, and stays;
//! - the run's and the players' seeds (`RunRngSet.seed`, `string_seed`, `PlayerRngSet.seed`): a monster summoned in
//!   the fight makes its stream from the run's seed, so they are drawn again too;
//! - the relic grab bag: each rarity's relics in a new order;
//! - the draw pile's order: drawn from its public record since the last shuffle (drawlog.rs), and the ids of its
//!   cards, which the game gave in the order of the opening shuffle, among them in a new order;
//! - each monster's queued move where other moves show the same intents: rolled again from where the monster rolled
//!   it, by the game's own transition, until it shows what the player sees.

use crate::drawlog::{self, DrawLog};
use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::observe;
use crate::protocol;
use crate::rt::random::MegaRandom as Stream;
use crate::rt::*;
use std::collections::HashMap;

/// How many rolls of a monster's move may be tried before its move is taken as one the monster did not roll (set by
/// the game on the spot, or out of reach of the roll from where the fight stands now).
const ROLLS: u32 = 10_000;

/// What a determinize drew, for checks and measurements.
#[derive(Clone, Copy, Debug, Default)]
pub struct Drawn {
    /// The draw pile's sample: the most states its filter held at once.
    pub draw: drawlog::Cost,
    /// Monsters whose queued move was rolled again, and the rolls that took.
    pub monsters: u32,
    pub rolls: u32,
    /// Monsters whose move no roll showed, kept as it is.
    pub kept: u32,
}

/// Draws everything the player of `c` cannot see again, from `seed`. `c` is a combat between the player's actions.
pub fn determinize(c: &mut Combat, seed: u64) -> Result<Drawn, String> {
    let mut rng = Stream::new(seed);
    let log = c.x.draw_log.clone().ok_or("the combat came with no draw pile record (sim.draw_log)")?;
    let log = log.as_ref().as_ref().map_err(|e| format!("the draw pile's record: {e}"))?;
    let mut drawn = Drawn::default();

    for i in 0..c.a.mega_random.len() {
        let fresh = Stream::new(rng.next_ulong());
        let state = MegaRandomData::of_mut(Obj::new(TAG_MEGA_RANDOM, i, false), c);
        (state._s0, state._s1, state._s2, state._s3) = (fresh.s0, fresh.s1, fresh.s2, fresh.s3);
    }
    for i in 0..c.a.run_rng_set.len() {
        let seed = rng.next_ulong();
        let set = RunRngSetData::of_mut(Obj::new(TAG_RUN_RNG_SET, i, false), c);
        set.seed = seed;
        if !set.string_seed.is_null() {
            let length = set.string_seed.as_str().chars().count();
            set.string_seed = Str::from_string(text(length, &mut rng));
        }
    }
    for i in 0..c.a.player_rng_set.len() {
        let seed = rng.next_ulong();
        PlayerRngSetData::of_mut(Obj::new(TAG_PLAYER_RNG_SET, i, false), c).seed = seed;
    }
    for i in 0..c.a.relic_grab_bag.len() {
        let bag = RelicGrabBagData::of_mut(Obj::new(TAG_RELIC_GRAB_BAG, i, false), c);
        for rarity in bag._deques.keys() {
            let deque = bag._deques.get_mut(&rarity);
            deque.sort_by_key(|relic| relic.0);
            drawlog::shuffle(deque, &mut rng);
        }
    }
    drawn.draw = draw_pile(c, log, &mut rng)?;
    moves(c, &mut rng, &mut drawn)?;
    Ok(drawn)
}

/// A seed's text: upper-case letters and digits.
fn text(length: usize, rng: &mut Stream) -> String {
    const CHARS: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    (0..length).map(|_| CHARS[rng.next_max(CHARS.len() as i32) as usize] as char).collect()
}

/// The live combat's state and the player the world is for.
fn live(c: &mut Combat) -> (CombatState, Player) {
    let manager = g::combat::combat_manager::instance(c);
    let st = g::combat::combat_manager::debug_only_get_state(manager, c);
    if is_null(&st) {
        panic!("no combat state");
    }
    let player = g::context::local_context::get_me__i_combat_state(st, c);
    if is_null(&player) {
        panic!("the combat has no player with the local net id");
    }
    (st, player)
}

/// The draw pile in an order drawn from its record: the record gives the order as card keys, and the pile's cards
/// that show one key take its places in a uniform order. The pile's cards then take its cards' ids in a uniform order.
fn draw_pile(c: &mut Combat, log: &DrawLog, rng: &mut Stream) -> Result<drawlog::Cost, String> {
    let (_, player) = live(c);
    let pcs = g::entities_players::player::player_combat_state(player, c);
    let pile = g::entities_players::player_combat_state::draw_pile(pcs, c);
    let cards = g::entities_cards::card_pile::cards(pile, c);
    // The pile's cards by what they show, as the record names cards.
    let mut by_key: HashMap<String, Vec<CardModel>> = HashMap::new();
    for card in &cards {
        let key = protocol::card_key(&observe::card(*card, false, c));
        let key = String::from_utf8(key).map_err(|e| format!("a card key is not UTF-8: {e}"))?;
        by_key.entry(key).or_default().push(*card);
    }
    let (order, cost) = drawlog::sample(log, rng)?;
    // Each group in an order of its own (the cards' places in the combat's arena), then shuffled: what is dealt
    // owes nothing to the pile's order, nor to the map's.
    let mut keys: Vec<&String> = by_key.keys().collect();
    keys.sort();
    let keys: Vec<String> = keys.into_iter().cloned().collect();
    for key in &keys {
        let group = by_key.get_mut(key).expect("a key of the map");
        group.sort_by_key(|card| card.0);
        drawlog::shuffle(group, rng);
    }
    let mut dealt = Vec::with_capacity(cards.len());
    for k in order {
        let key = &log.keys[k as usize];
        let card = by_key.get_mut(key).and_then(|group| group.pop()).ok_or_else(|| {
            format!("the draw pile's record ends with a card the pile does not hold: {key}")
        })?;
        dealt.push(card);
    }
    if dealt.len() != cards.len() {
        let left: Vec<&String> = by_key.iter().filter(|(_, group)| !group.is_empty()).map(|(key, _)| key).collect();
        return Err(format!("the draw pile holds cards its record does not end with: {left:?}"));
    }
    CardPileData::of_mut(pile, c)._cards = dealt.clone();

    let db = g::game_actions_multiplayer::net_combat_card_db::instance(c);
    let mut ids: Vec<u32> = dealt
        .iter()
        .map(|card| g::game_actions_multiplayer::net_combat_card_db::get_card_id(db, *card, c))
        .collect();
    ids.sort();
    drawlog::shuffle(&mut ids, rng);
    let numbers = NetCombatCardDbData::of_mut(db, c);
    for (card, id) in dealt.into_iter().zip(ids) {
        numbers._card_to_id.set(card, id);
        numbers._id_to_card.set(id, card);
    }
    Ok(cost)
}

/// Each living enemy's queued move, where another of its moves shows the same intents, rolled again: the monster's
/// machine is set back to where it stood before the roll (its last move done, the roll's entry off its log), and the
/// game's roll is run on a new state of the stream it draws from, until the move rolled shows the intents the player
/// sees. A move the game set on the spot (a stun) is no roll: the log does not end with it, and it stays.
fn moves(c: &mut Combat, rng: &mut Stream, drawn: &mut Drawn) -> Result<(), String> {
    let (st, player) = live(c);
    let me = g::entities_players::player::creature(player, c);
    let targets = vec![me];
    for enemy in g::combat::combat_state::enemies(st, c) {
        if g::entities_creatures::creature::is_dead(enemy, c) {
            continue;
        }
        let monster = g::entities_creatures::creature::monster(enemy, c);
        if is_null(&monster) {
            continue;
        }
        let machine = g::models::monster_model::move_state_machine(monster, c);
        if is_null(&machine) {
            continue;
        }
        let next = g::models::monster_model::next_move(monster, c);
        let shown = observe::intents(next, enemy, &targets, c);
        let states = MonsterMoveStateMachineData::of(machine, c).states.values();
        let mut alike = 0;
        for state in states {
            if is(state, Kind::MoveState, c) && observe::intents(state, enemy, &targets, c) == shown {
                alike += 1;
            }
        }
        if alike < 2 {
            continue;
        }
        let stood = MonsterMoveStateMachineData::of(machine, c).clone();
        if stood.state_log.last() != Some(&next) || stood._current_state != next {
            continue;
        }
        // The machine's first move when it starts on a move: in its log from the start, not rolled.
        if stood.state_log.len() == 1 && stood._initial_state == next {
            continue;
        }
        let before =
            if stood.state_log.len() >= 2 { stood.state_log[stood.state_log.len() - 2] } else { stood._initial_state };
        drawn.monsters += 1;
        let mut rolled = false;
        for _ in 0..ROLLS {
            let mut t = c.clone();
            {
                let back = MonsterMoveStateMachineData::of_mut(machine, &mut t);
                back.state_log.pop();
                back._current_state = before;
                back._performed_first_move = stood.state_log.len() >= 2;
            }
            if is(before, Kind::MoveState, &t) && stood.state_log.len() >= 2 {
                // The move before was done: leaving it in the roll cleared this.
                MoveStateData::of_mut(before, &mut t)._performed_at_least_once = true;
            }
            let run_state = g::combat::combat_state::run_state(st, &mut t);
            let run_rng = g::dispatch::i_run_state::rng(run_state, &mut t);
            let stream = g::runs::run_rng_set::get_rng(run_rng, RunRngType::MonsterAi, &mut t);
            let (random, counter) = (RngData::of(stream, &t)._random, RngData::of(stream, &t)._counter);
            let fresh = Stream::new(rng.next_ulong());
            let state = MegaRandomData::of_mut(random, &mut t);
            (state._s0, state._s1, state._s2, state._s3) = (fresh.s0, fresh.s1, fresh.s2, fresh.s3);
            g::models::monster_model::roll_move(monster, targets.clone(), &mut t);
            // The stream's count of calls stays the real one, which already holds the real roll's.
            RngData::of_mut(stream, &mut t)._counter = counter;
            let move_ = g::models::monster_model::next_move(monster, &mut t);
            drawn.rolls += 1;
            // A roll that did not log one move (the machine stood elsewhere than set back) is not the game's.
            if MonsterMoveStateMachineData::of(machine, &t).state_log.len() != stood.state_log.len() {
                break;
            }
            if observe::intents(move_, enemy, &targets, &mut t) == shown {
                *c = t;
                rolled = true;
                break;
            }
        }
        if !rolled {
            drawn.kept += 1;
        }
    }
    Ok(())
}
