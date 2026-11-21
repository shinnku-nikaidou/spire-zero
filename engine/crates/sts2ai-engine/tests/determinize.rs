//! NoSL's worlds drawn from real combats (src/determinize.rs), held against the dump `sim parity --dump` writes
//! (STS2AI_ENGINE_FIXTURES names it, as for tests/fixtures.rs). plans/2026-11-17-1120-nosl-dread-architecture.md, 4.4
//! and 9.5.
//!
//!  - `shows_the_record`: every world drawn from a record's export shows the record: the observation byte for byte
//!    and the plays. Nothing the player sees is drawn again.
//!  - `forgets_the_first_draw`: a world drawn from a world already drawn is the world drawn from the real one with the
//!    same seed, object for object: nothing the first draw left behind, so nothing of the real hidden state, survives
//!    a draw.
//!  - `rank_check` (ignored; `-- --ignored --nocapture`): simulation-based calibration. At decisions picked by public
//!    information alone, a statistic of the hidden state (a card's depth in the draw pile, a monster's queued move) is
//!    read in the real combat and in K worlds; an exact sampler puts the real one at a uniform rank among the K + 1.
//!    Printed by stratum, with the chi-square of the ranks. The draw pile counts one decision of each shuffle (drawn
//!    at random: the decisions of one shuffle share one real order), a queued move one decision of each roll.
//!  - `leak_probe` (ignored): every field of every object of the combat, real against K worlds: the fields the worlds
//!    draw (they differ among themselves), and the fields every world holds alike but not as the real combat does.
//!  - `drawn_at_a_prompt`: at every card prompt the combat waits on (plans/2026-11-21-1213-nosl-prompt-worlds.md), a
//!    world stood there from the export before it (or the combat's opening) and drawn where the prompt takes its
//!    answer: drawn twice from one seed it is one world; given the game's answer, no random stream is the real one's,
//!    and the draw pile is not the real one in every world.

mod support;

use std::collections::{BTreeMap, HashMap, HashSet};
use sts2ai_engine::drawlog::{Event, Place};
use sts2ai_engine::ffi;
use sts2ai_engine::r#gen as g;
use sts2ai_engine::rt::Combat;
use support::dump::{Fixture, Tally, json_difference, sweep};

fn last_error() -> String {
    let mut buf = ffi::SzBuf::EMPTY;
    unsafe { ffi::sz_last_error(&mut buf) };
    let bytes = unsafe { std::slice::from_raw_parts(buf.ptr, buf.len) }.to_vec();
    unsafe { ffi::sz_buf_free(buf) };
    String::from_utf8_lossy(&bytes).into_owned()
}

fn text(world: u64, call: unsafe extern "C" fn(u64, *mut ffi::SzBuf) -> i32) -> Vec<u8> {
    let mut buf = ffi::SzBuf::EMPTY;
    assert_eq!(unsafe { call(world, &mut buf) }, 0, "{}", last_error());
    let bytes = unsafe { std::slice::from_raw_parts(buf.ptr, buf.len) }.to_vec();
    unsafe { ffi::sz_buf_free(buf) };
    bytes
}

/// A world drawn through the C ABI from record `k`'s export, with `seed`.
fn drawn(fixture: &Fixture, k: usize, seed: u64) -> Result<u64, String> {
    let export = fixture.bytes(k, "export.json");
    let real = unsafe { ffi::sz_world_import(export.as_ptr(), export.len(), 0) };
    if real == 0 {
        return Err(format!("sz_world_import: {}", last_error()));
    }
    let world = unsafe { ffi::sz_world_determinize(real, seed) };
    unsafe { ffi::sz_world_free(real) };
    if world == 0 {
        return Err(format!("sz_world_determinize: {}", last_error()));
    }
    Ok(world)
}

#[test]
fn shows_the_record() {
    sweep(|fixture| {
        let mut tally = Tally::default();
        for k in 0..fixture.records.len() {
            if !fixture.has_export(k) {
                tally.skipped += 1;
                continue;
            }
            for seed in [1, 2] {
                let checked = drawn(fixture, k, seed + k as u64 * 7).and_then(|world| {
                    let observation = text(world, ffi::sz_world_observe);
                    let legal = text(world, ffi::sz_world_legal);
                    unsafe { ffi::sz_world_free(world) };
                    if let Some(difference) = json_difference(&fixture.bytes(k, "observation.json"), &observation) {
                        return Err(format!("observation{difference}"));
                    }
                    if legal != fixture.records[k].legal.as_bytes() {
                        return Err("the plays differ".into());
                    }
                    Ok(())
                });
                match checked {
                    Ok(()) => tally.passed += 1,
                    Err(difference) => tally.failed.push((format!("{} record {k} seed {seed}", fixture.name), difference)),
                }
            }
        }
        tally
    })
    .finish("shows_the_record");
}

/// The combat of record `k`'s export, through the library.
fn import(fixture: &Fixture, k: usize) -> Combat {
    let export = String::from_utf8(fixture.bytes(k, "export.json")).expect("UTF-8");
    sts2ai_engine::import::import(&export).unwrap_or_else(|e| panic!("{} record {k}: {e}", fixture.name))
}

/// Every object of the combat as text, by arena.
fn objects(c: &Combat) -> BTreeMap<&'static str, String> {
    let mut all = BTreeMap::new();
    c.a.each(&mut |name, arena| {
        if !arena.is_empty() {
            all.insert(name, arena.text());
        }
    });
    all
}

#[test]
fn forgets_the_first_draw() {
    sweep(|fixture| {
        let mut tally = Tally::default();
        for k in 0..fixture.records.len() {
            if !fixture.has_export(k) {
                tally.skipped += 1;
                continue;
            }
            let real = import(fixture, k);
            let mut once = real.clone();
            let mut twice = real.clone();
            let checked = sts2ai_engine::determinize::determinize(&mut once, 11)
                .and_then(|_| sts2ai_engine::determinize::determinize(&mut twice, 5))
                .and_then(|_| sts2ai_engine::determinize::determinize(&mut twice, 11));
            if let Err(e) = checked {
                tally.failed.push((format!("{} record {k}", fixture.name), e));
                continue;
            }
            let (a, b) = (objects(&once), objects(&twice));
            let differ: Vec<&str> = a.keys().chain(b.keys()).filter(|name| a.get(*name) != b.get(*name)).copied().collect();
            if differ.is_empty() {
                tally.passed += 1;
            } else {
                let first = differ[0];
                let (x, y) = (a.get(first).cloned().unwrap_or_default(), b.get(first).cloned().unwrap_or_default());
                let at = x.chars().zip(y.chars()).position(|(p, q)| p != q).unwrap_or(x.len().min(y.len()));
                let around = |t: &str| t.chars().skip(at.saturating_sub(80)).take(200).collect::<String>();
                tally.failed.push((
                    format!("{} record {k}", fixture.name),
                    format!("arenas differ: {differ:?}; {first} at {at} of {}/{}: {} | {}", x.len(), y.len(), around(&x), around(&y)),
                ));
            }
        }
        tally
    })
    .finish("forgets_the_first_draw");
}

// ---------------------------------------------------------------------------------------------------------------------
// The rank check
// ---------------------------------------------------------------------------------------------------------------------

/// The live combat's draw pile, as the card keys of its cards, top first.
fn draw_order(c: &mut Combat) -> Vec<String> {
    let manager = g::combat::combat_manager::instance(c);
    let st = g::combat::combat_manager::debug_only_get_state(manager, c);
    let player = g::context::local_context::get_me__i_combat_state(st, c);
    let pcs = g::entities_players::player::player_combat_state(player, c);
    let pile = g::entities_players::player_combat_state::draw_pile(pcs, c);
    g::entities_cards::card_pile::cards(pile, c)
        .into_iter()
        .map(|card| String::from_utf8(sts2ai_engine::protocol::card_key(&sts2ai_engine::observe::card(card, false, c))).unwrap())
        .collect()
}

/// Each living enemy's queued move id, where another of its moves shows the same intents: by the enemy's place, with
/// the moves that look alike and how long its log of moves is (one more each turn it rolls).
fn queued(c: &mut Combat) -> Vec<(usize, String, Vec<String>, usize)> {
    use sts2ai_engine::r#gen::kinds::Kind;
    use sts2ai_engine::r#gen::state::MonsterMoveStateMachineData;
    let manager = g::combat::combat_manager::instance(c);
    let st = g::combat::combat_manager::debug_only_get_state(manager, c);
    let player = g::context::local_context::get_me__i_combat_state(st, c);
    let me = g::entities_players::player::creature(player, c);
    let targets = vec![me];
    let mut found = Vec::new();
    for (at, enemy) in g::combat::combat_state::enemies(st, c).into_iter().enumerate() {
        if g::entities_creatures::creature::is_dead(enemy, c) {
            continue;
        }
        let monster = g::entities_creatures::creature::monster(enemy, c);
        if monster.is_null() {
            continue;
        }
        let machine = g::models::monster_model::move_state_machine(monster, c);
        if machine.is_null() {
            continue;
        }
        let next = g::models::monster_model::next_move(monster, c);
        let shown = sts2ai_engine::observe::intents(next, enemy, &targets, c);
        let mut alike = Vec::new();
        for state in MonsterMoveStateMachineData::of(machine, c).states.values() {
            if sts2ai_engine::rt::is(state, Kind::MoveState, c) && sts2ai_engine::observe::intents(state, enemy, &targets, c) == shown {
                alike.push(g::dispatch::monster_state::id(state, c).as_str().to_string());
            }
        }
        if alike.len() >= 2 {
            alike.sort();
            let log = MonsterMoveStateMachineData::of(machine, c).state_log.len();
            found.push((at, g::dispatch::monster_state::id(next, c).as_str().to_string(), alike, log));
        }
    }
    found
}

/// The depth of the shallowest card showing `key`; the pile's size where none does.
fn depth(order: &[String], key: &str) -> usize {
    order.iter().position(|k| k == key).unwrap_or(order.len())
}

/// A decision's stratum and the card key its statistic follows, from the draw pile's public record alone: the card
/// last put on top or moved to the top (Headbutt, Innate, Perfect Fit), else the card last put in at random, else the
/// card last put at the bottom, else ("plain") the first key of the pile in key order.
fn stratum(c: &Combat, order: &[String]) -> Option<(&'static str, String)> {
    let log = c.x.draw_log.as_ref()?.as_ref().as_ref().ok()?;
    let latest = |want: &dyn Fn(&Event) -> Option<u32>| log.events.iter().rev().find_map(want);
    let key = |k: u32| log.keys[k as usize].clone();
    let present = |k: &String| order.contains(k);
    if let Some(k) = latest(&|e| match e {
        Event::Add(k, Place::Top) | Event::Move(k, Place::Top) => Some(*k),
        _ => None,
    }) && present(&key(k))
    {
        return Some(("known top", key(k)));
    }
    if let Some(k) = latest(&|e| match e {
        Event::Add(k, Place::Random) => Some(*k),
        _ => None,
    }) && present(&key(k))
    {
        return Some(("random insert", key(k)));
    }
    if let Some(k) = latest(&|e| match e {
        Event::Add(k, Place::Bottom) | Event::Move(k, Place::Bottom) => Some(*k),
        _ => None,
    }) && present(&key(k))
    {
        return Some(("known bottom", key(k)));
    }
    let mut keys = order.to_vec();
    keys.sort();
    Some(("plain", keys.first()?.clone()))
}

/// A rank among the K + 1 with ties split at random: below plus a uniform share of the ties.
fn rank(real: usize, worlds: &[usize], rng: &mut sts2ai_engine::rt::random::MegaRandom) -> usize {
    let below = worlds.iter().filter(|&&w| w < real).count();
    let ties = worlds.iter().filter(|&&w| w == real).count();
    below + rng.next_max(ties as i32 + 1) as usize
}

fn chi_square(ranks: &[usize], bins: usize) -> f64 {
    let expected = ranks.len() as f64 / bins as f64;
    let mut counts = vec![0usize; bins];
    for &r in ranks {
        counts[r] += 1;
    }
    counts.iter().map(|&n| (n as f64 - expected).powi(2) / expected).sum()
}

/// FNV-1a of a text: a fixture's own seed, so that no two combats share their worlds' draws or their ties' order.
fn fnv(text: &str) -> u64 {
    text.bytes().fold(14695981039346656037u64, |h, b| (h ^ b as u64).wrapping_mul(1099511628211))
}

#[test]
#[ignore]
fn rank_check() {
    const K: usize = 19;
    let ranks: std::sync::Mutex<BTreeMap<&'static str, Vec<usize>>> = Default::default();
    let costs: std::sync::Mutex<Vec<u32>> = Default::default();
    sweep(|fixture| {
        let seed = fnv(&fixture.name);
        let mut rng = sts2ai_engine::rt::random::MegaRandom::new(seed);
        // The draw pile's ranks by shuffle and stratum: the decisions of one shuffle share one real order, so their
        // ranks are not independent; one of them, drawn at random, stands for it.
        let mut by_shuffle: BTreeMap<(usize, &'static str), Vec<usize>> = BTreeMap::new();
        let mut shuffle = 0usize;
        let mut before: Option<Vec<Event>> = None;
        let mut rolls = HashSet::new();
        for k in 0..fixture.records.len() {
            if !fixture.has_export(k) {
                continue;
            }
            let mut real = import(fixture, k);
            let order = draw_order(&mut real);
            let events = real.x.draw_log.as_ref().and_then(|l| l.as_ref().as_ref().ok()).map_or(Vec::new(), |l| l.events.clone());
            // A record that does not go on from the one before starts at a new shuffle.
            if before.as_ref().is_some_and(|b| b.len() > events.len() || events[..b.len()] != b[..]) {
                shuffle += 1;
            }
            before = Some(events);
            let mut worlds = Vec::with_capacity(K);
            for w in 0..K {
                let mut world = real.clone();
                let drawn = sts2ai_engine::determinize::determinize(&mut world, seed ^ ((k * 1000 + w) as u64 + 1).wrapping_mul(0x9E37_79B9_7F4A_7C15))
                    .unwrap_or_else(|e| panic!("{} record {k}: {e}", fixture.name));
                costs.lock().unwrap().push(drawn.draw.states);
                worlds.push(world);
            }
            if order.len() >= 2
                && let Some((name, key)) = stratum(&real, &order)
            {
                let at: Vec<usize> = worlds.iter_mut().map(|w| depth(&draw_order(w), &key)).collect();
                by_shuffle.entry((shuffle, name)).or_default().push(rank(depth(&order, &key), &at, &mut rng));
            }
            // The monsters' queued moves: the move's place among those that look alike, once a roll (each roll is
            // the game's own, independent of the ones before given what the player saw).
            for (at, move_, alike, log) in queued(&mut real) {
                if !rolls.insert((at, log)) {
                    continue;
                }
                let index = |m: &str| alike.iter().position(|a| a == m).unwrap();
                let mut in_worlds = Vec::with_capacity(K);
                for w in worlds.iter_mut() {
                    let q = queued(w);
                    let Some((_, m, _, _)) = q.iter().find(|(a, _, _, _)| *a == at) else { continue };
                    in_worlds.push(index(m));
                }
                if in_worlds.len() == K {
                    let r = rank(index(&move_), &in_worlds, &mut rng);
                    ranks.lock().unwrap().entry("queued move").or_default().push(r);
                }
            }
        }
        let mut ranks = ranks.lock().unwrap();
        for ((_, name), found) in by_shuffle {
            ranks.entry(name).or_default().push(found[rng.next_max(found.len() as i32) as usize]);
        }
        Tally::default()
    });
    println!("K = {K} worlds a decision; each rank expects {:.3} of the decisions under an exact sampler", 1.0 / (K + 1) as f64);
    println!("chi-square on {K} degrees of freedom: 1 % critical value 36.19; draw pile strata: one decision a shuffle");
    for (name, r) in ranks.into_inner().unwrap() {
        let mut counts = vec![0usize; K + 1];
        for &x in &r {
            counts[x] += 1;
        }
        println!("{name:>14}: {:>5} decisions, chi-square {:>6.1}, ranks {counts:?}", r.len(), chi_square(&r, K + 1));
    }
    let mut states = costs.into_inner().unwrap();
    states.sort();
    if !states.is_empty() {
        let mean = states.iter().map(|&r| r as f64).sum::<f64>() / states.len() as f64;
        println!(
            "draw pile filter's states a world: mean {mean:.2}, median {}, p99 {}, max {} ({} worlds)",
            states[states.len() / 2],
            states[states.len() * 99 / 100],
            states[states.len() - 1],
            states.len()
        );
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// The leak probe
// ---------------------------------------------------------------------------------------------------------------------

/// The leaves of a value as `{:?}` writes it: each path (fields by name, list items by place) and its text.
fn leaves(text: &str) -> Vec<(String, String)> {
    let chars: Vec<char> = text.chars().collect();
    let mut out = Vec::new();
    let mut i = 0;
    value(&chars, &mut i, String::new(), &mut out);
    out
}

fn value(s: &[char], i: &mut usize, path: String, out: &mut Vec<(String, String)>) {
    let start = *i;
    // A name before an opening (a struct's or a variant's), kept as part of the leaf when nothing opens.
    while *i < s.len() && !matches!(s[*i], '{' | '[' | '(' | ',' | '}' | ']' | ')' | '"') {
        *i += 1;
    }
    if *i < s.len() && s[*i] == '"' {
        *i += 1;
        while *i < s.len() && s[*i] != '"' {
            if s[*i] == '\\' {
                *i += 1;
            }
            *i += 1;
        }
        *i += 1;
        out.push((path, s[start..*i].iter().collect()));
        return;
    }
    let name: String = s[start..*i].iter().collect::<String>().trim().to_string();
    if *i < s.len() && matches!(s[*i], '{' | '[' | '(') {
        let close = match s[*i] {
            '{' => '}',
            '[' => ']',
            _ => ')',
        };
        let named = s[*i] == '{';
        *i += 1;
        let mut index = 0;
        let prefix = if name.is_empty() { path.clone() } else { format!("{path}<{name}>") };
        loop {
            while *i < s.len() && s[*i] == ' ' {
                *i += 1;
            }
            if *i >= s.len() || s[*i] == close {
                *i += 1;
                break;
            }
            let child = if named {
                let field_start = *i;
                while s[*i] != ':' {
                    *i += 1;
                }
                let field: String = s[field_start..*i].iter().collect();
                *i += 1;
                while s[*i] == ' ' {
                    *i += 1;
                }
                format!("{prefix}.{}", field.trim())
            } else {
                index += 1;
                format!("{prefix}[{}]", index - 1)
            };
            value(s, i, child, out);
            while *i < s.len() && s[*i] == ' ' {
                *i += 1;
            }
            if *i < s.len() && s[*i] == ',' {
                *i += 1;
            }
        }
        if index == 0 && !named {
            out.push((format!("{prefix}[]"), String::new()));
        }
        return;
    }
    out.push((path, name));
}

/// A leaf's path with the places in lists taken out: the field it is of, by arena.
fn field_of(path: &str) -> String {
    let mut out = String::new();
    let mut skip = false;
    for ch in path.chars() {
        match ch {
            '[' => {
                skip = true;
                out.push_str("[]");
            }
            ']' => skip = false,
            _ if !skip => out.push(ch),
            _ => {}
        }
    }
    out
}

#[test]
#[ignore]
fn leak_probe() {
    const K: usize = 4;
    // field -> (decisions where the worlds differ among themselves, where they agree but not with the real combat)
    let found: std::sync::Mutex<HashMap<String, (usize, usize)>> = Default::default();
    let examples: std::sync::Mutex<HashMap<String, String>> = Default::default();
    let decisions = std::sync::Mutex::new(0usize);
    sweep(|fixture| {
        // A few decisions a combat: each round's first.
        for k in (0..fixture.records.len()).filter(|&k| fixture.has_export(k)).step_by(4).take(6) {
            let real = import(fixture, k);
            let leaves_of = |c: &Combat| -> HashMap<String, String> {
                let mut all = HashMap::new();
                for (arena, text) in objects(c) {
                    for (path, leaf) in leaves(&text) {
                        all.insert(format!("{arena}{path}"), leaf);
                    }
                }
                all
            };
            let real_leaves = leaves_of(&real);
            let worlds: Vec<HashMap<String, String>> = (0..K)
                .map(|w| {
                    let mut world = real.clone();
                    sts2ai_engine::determinize::determinize(&mut world, (k * 31 + w) as u64 + 3).expect("a world");
                    leaves_of(&world)
                })
                .collect();
            let mut by_field: HashMap<String, (bool, bool)> = HashMap::new();
            let paths: HashSet<&String> = real_leaves.keys().chain(worlds.iter().flat_map(|w| w.keys())).collect();
            for path in paths {
                let values: Vec<Option<&String>> = worlds.iter().map(|w| w.get(path)).collect();
                let differ = values.iter().any(|v| *v != values[0]);
                let moved = !differ && values[0] != real_leaves.get(path);
                let entry = by_field.entry(field_of(path)).or_default();
                entry.0 |= differ;
                entry.1 |= moved;
                if moved {
                    examples.lock().unwrap().entry(field_of(path)).or_insert_with(|| {
                        format!("{} #{k}: real {:?} worlds {:?}", fixture.name, real_leaves.get(path), values[0])
                    });
                }
            }
            let mut found = found.lock().unwrap();
            for (field, (differ, moved)) in by_field {
                let entry = found.entry(field).or_default();
                entry.0 += differ as usize;
                entry.1 += moved as usize;
            }
            *decisions.lock().unwrap() += 1;
        }
        Tally::default()
    });
    let n = decisions.into_inner().unwrap();
    let found = found.into_inner().unwrap();
    let examples = examples.into_inner().unwrap();
    println!("{n} decisions, {K} worlds each");
    let mut drawn: Vec<(&String, &(usize, usize))> = found.iter().filter(|(_, c)| c.0 > 0).collect();
    drawn.sort();
    println!("\n-- drawn again (the worlds differ among themselves): {} fields", drawn.len());
    for (field, (d, _)) in drawn {
        println!("  {d:>4}/{n}  {field}");
    }
    let mut moved: Vec<(&String, &(usize, usize))> = found.iter().filter(|(_, c)| c.1 > 0).collect();
    moved.sort();
    println!("\n-- the same in every world, not the real one's: {} fields", moved.len());
    for (field, (_, m)) in moved {
        println!("  {m:>4}/{n}  {field}    e.g. {}", examples.get(field).map_or("", |e| &e[..e.len().min(300)]));
    }
}



// ---------------------------------------------------------------------------------------------------------------------
// Worlds drawn at a card prompt
// ---------------------------------------------------------------------------------------------------------------------

/// The live combat's hand, as the card keys of its cards, in order.
fn hand_keys(c: &mut Combat) -> Vec<String> {
    let manager = g::combat::combat_manager::instance(c);
    let st = g::combat::combat_manager::debug_only_get_state(manager, c);
    let player = g::context::local_context::get_me__i_combat_state(st, c);
    let pcs = g::entities_players::player::player_combat_state(player, c);
    let hand = g::entities_players::player_combat_state::hand(pcs, c);
    g::entities_cards::card_pile::cards(hand, c)
        .into_iter()
        .map(|card| String::from_utf8(sts2ai_engine::protocol::card_key(&sts2ai_engine::observe::card(card, false, c))).unwrap())
        .collect()
}

/// The world standing at record `k`, a card prompt, as a host stands the engine there: the combat as last written out
/// before it (the record's export, or the opening with its start's answers), along the combat's actions since, with the
/// draw pile's record at the prompt.
fn stood(fixture: &Fixture, k: usize) -> Result<sts2ai_engine::world::Live, String> {
    use sts2ai_engine::ffi::World as _;
    use sts2ai_engine::world::Live;
    let from = (0..k).rev().find(|&j| fixture.has_export(j));
    let mut live = match from {
        Some(j) => Live::import(&String::from_utf8(fixture.bytes(j, "export.json")).expect("UTF-8"))?,
        None => {
            let opening = std::fs::read_to_string(fixture.folder.join("opening.export.json")).map_err(|e| format!("no opening: {e}"))?;
            let mut live = Live::begin(&opening)?;
            let answers: Vec<Vec<i32>> = match std::fs::read(fixture.folder.join("opening.answers.json")) {
                Ok(text) => serde_json::from_slice(&text).expect("opening.answers.json"),
                Err(_) => Vec::new(),
            };
            for answer in answers {
                live.apply(&format!("given:{}", answer.iter().map(|i| i.to_string()).collect::<Vec<_>>().join(",")))?;
            }
            live
        }
    };
    for r in from.unwrap_or(0)..k {
        live.apply(fixture.records[r].action.as_deref().expect("a record before the last has an action"))?;
    }
    if live.asked().is_none() {
        return Err(format!("record {k} is a card prompt, and the world stood there is not"));
    }
    let log: serde_json::Value = serde_json::from_slice(&fixture.bytes(k, "draw_log.json")).map_err(|e| e.to_string())?;
    live.combat().x.draw_log = Some(std::sync::Arc::new(sts2ai_engine::drawlog::DrawLog::parse(&log)));
    Ok(live)
}

/// The random streams of a combat, as text.
fn streams(c: &Combat) -> String {
    objects(c).into_iter().filter(|(name, _)| name.contains("random")).map(|(_, text)| text).collect::<Vec<_>>().join("\n")
}

#[test]
fn drawn_at_a_prompt() {
    use sts2ai_engine::ffi::World as _;
    const K: u64 = 16;
    let (redrawn, kept) = (std::sync::atomic::AtomicUsize::new(0), std::sync::atomic::AtomicUsize::new(0));
    let (opened, opened_redrawn) = (std::sync::atomic::AtomicUsize::new(0), std::sync::atomic::AtomicUsize::new(0));
    sweep(|fixture| {
        let mut tally = Tally::default();
        // The prompts of the combat's start, before record 0 (a combat on its own answers them there): a world drawn
        // at each, given the rest of the start's answers, against record 0.
        if let (Ok(opening), Ok(answers), Ok(logs)) = (
            std::fs::read_to_string(fixture.folder.join("opening.export.json")),
            std::fs::read(fixture.folder.join("opening.answers.json")),
            std::fs::read(fixture.folder.join("opening.draw_logs.json")),
        ) {
            let answers: Vec<Vec<i32>> = serde_json::from_slice(&answers).expect("opening.answers.json");
            let logs: Vec<serde_json::Value> = serde_json::from_slice(&logs).expect("opening.draw_logs.json");
            let given = |answer: &Vec<i32>| format!("given:{}", answer.iter().map(|i| i.to_string()).collect::<Vec<_>>().join(","));
            for i in 0..answers.len() {
                let name = format!("{} opening prompt {i}", fixture.name);
                let checked = (|| -> Result<(), String> {
                    let mut live = sts2ai_engine::world::Live::begin(&opening)?;
                    for answer in &answers[..i] {
                        live.apply(&given(answer))?;
                    }
                    live.combat().x.draw_log = Some(std::sync::Arc::new(sts2ai_engine::drawlog::DrawLog::parse(&logs[i])));
                    let mut real = import(fixture, 0);
                    let mut same_hand = 0;
                    for seed in 0..K {
                        let mut world = live.clone();
                        world.draw_at_prompt(seed)?;
                        for answer in &answers[i..] {
                            world.apply(&given(answer))?;
                        }
                        if streams(world.combat()) == streams(&real) {
                            return Err(format!("seed {seed}: a random stream at record 0 is the real one's"));
                        }
                        same_hand += (hand_keys(world.combat()) == hand_keys(&mut real)) as u64;
                    }
                    opened.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                    if same_hand < K {
                        opened_redrawn.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                    }
                    Ok(())
                })();
                match checked {
                    Ok(()) => tally.passed += 1,
                    Err(e) => tally.failed.push((name, e)),
                }
            }
        }
        for k in 0..fixture.records.len().saturating_sub(1) {
            if !fixture.records[k].prompt || !fixture.file(k, "draw_log.json").exists() {
                continue;
            }
            let name = format!("{} record {k}", fixture.name);
            let answer = fixture.records[k].action.clone().expect("a prompt's answer");
            let checked = stood(fixture, k).and_then(|live| {
                let draw = |seed: u64| -> Result<sts2ai_engine::world::Live, String> {
                    let mut world = live.clone();
                    world.draw_at_prompt(seed)?;
                    world.step(&answer)
                };
                // The real combat after the answer, where it stands between actions.
                let mut real = fixture.has_export(k + 1).then(|| import(fixture, k + 1));
                let (mut same_pile, mut same_hand) = (0, 0);
                for seed in 0..K {
                    let mut once = draw(seed)?;
                    let mut again = draw(seed)?;
                    if objects(once.combat()) != objects(again.combat()) {
                        return Err(format!("seed {seed}: drawn twice at the prompt, two worlds"));
                    }
                    let Some(real) = real.as_mut() else { continue };
                    if streams(once.combat()) == streams(real) {
                        return Err(format!("seed {seed}: a random stream after the answer is the real one's"));
                    }
                    same_pile += (draw_order(once.combat()) == draw_order(real)) as u64;
                    same_hand += (hand_keys(once.combat()) == hand_keys(real)) as u64;
                }
                let Some(real) = real.as_mut() else { return Ok(()) };
                let pile = draw_order(real);
                let distinct: HashSet<&String> = pile.iter().collect();
                if distinct.len() >= 2 && same_pile == K {
                    return Err(format!("the draw pile after the answer is the real one in all {K} worlds: {pile:?}"));
                }
                if same_hand == K {
                    kept.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                } else {
                    redrawn.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                }
                Ok(())
            });
            match checked {
                Ok(()) => tally.passed += 1,
                Err(e) => tally.failed.push((name, e)),
            }
        }
        tally
    })
    .finish("drawn_at_a_prompt");
    println!(
        "prompts whose answer leaves a hand not the real one in some world: {}; the real hand in every world: {}; \
         prompts of a combat's start: {}, of them with record 0's hand not the real one in some world: {}",
        redrawn.into_inner(),
        kept.into_inner(),
        opened.into_inner(),
        opened_redrawn.into_inner()
    );
}
