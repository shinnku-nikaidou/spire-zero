//! The draw pile as the player knows it (plans/2026-11-17-1120-nosl-dread-architecture.md, 9.3).
//!
//! The game shuffles the draw pile uniformly. After that the player sees cards enter it (on top, at the bottom, at a
//! random place), leave it (drawn from the top, taken from the bottom, taken from a place they cannot see, or the top
//! few seen together and taken), and change while in it. The program around the game records these public events
//! since the last shuffle, each card by its card key (`DrawPileLog` in src/Sts2Ai.GameBridge), and the export carries
//! the record (`sim.draw_log`).
//!
//! A sample of the order is drawn from the record exactly, by a forward filter. Every choice the player cannot see is
//! the game's: a random place uniformly among all places; a card taken, moved or changed where the player cannot see
//! uniformly among the cards that look the same (the game takes one at random, or the player picks one of a list whose
//! equal cards are in an order the player cannot tell). Given what the player saw, the pile is always some known cards
//! on top, a uniform arrangement of a multiset of cards below them (the shuffled cards and the ones that went in among
//! them), and some known cards at the bottom: the shuffle starts it so, and every event keeps it so. A card put in at a
//! random place goes in among the middle's cards with the chance of the places next to them (an arrangement stays
//! uniform), and between two known cards otherwise; a card seen leaving the middle has the chance of its key there.
//! The filter carries every such state the record allows with its probability, and a sample draws one of them and
//! arranges its middle uniformly.

use crate::rt::random::MegaRandom;
use serde_json::Value;
use std::collections::{BTreeMap, HashMap};

/// Where a card enters, leaves or goes.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Place {
    Top,
    Bottom,
    /// Entering: a uniformly random place, the pile's size plus one to choose from.
    Random,
    /// Leaving: a place the player cannot see.
    Any,
}

/// One public event of the draw pile. A card is its key's index in [`DrawLog::keys`].
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub enum Event {
    /// The pile was shuffled: these are its cards, as a multiset.
    Reset(Vec<u32>),
    /// A card entered the pile: on top, at the bottom, or at a random place.
    Add(u32, Place),
    /// A card of the pile went to the top, the bottom or a random place by a rule of the game: which of the cards that
    /// look the same, the player cannot tell.
    Move(u32, Place),
    /// A card left the pile: from the top, from the bottom, or from a place the player cannot see.
    Take(u32, Place),
    /// The top cards left the pile together, seen as a multiset.
    TakeTop(Vec<u32>),
    /// A card of the pile, where the player cannot see, now shows another key.
    Relabel(u32, u32),
}

/// The record since the last shuffle, as the export carries it.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct DrawLog {
    /// The card keys the events name, each once.
    pub keys: Vec<String>,
    pub events: Vec<Event>,
}

impl DrawLog {
    /// The record from the export's `sim.draw_log`: a list of
    /// `{"op":"reset","cards":[KEY..]}`, `{"op":"add"|"move"|"take","card":KEY,"at":"top"|"bottom"|"random"|"any"}`,
    /// `{"op":"take_top","cards":[KEY..]}`, `{"op":"relabel","card":KEY,"to":KEY}`.
    pub fn parse(value: &Value) -> Result<DrawLog, String> {
        let list = value.as_array().ok_or("sim.draw_log is not a list")?;
        let mut log = DrawLog::default();
        let mut index: HashMap<String, u32> = HashMap::new();
        let mut key = |text: &Value, log: &mut DrawLog| -> Result<u32, String> {
            let text = text.as_str().ok_or("a draw log card is not a string")?;
            Ok(*index.entry(text.to_string()).or_insert_with(|| {
                log.keys.push(text.to_string());
                (log.keys.len() - 1) as u32
            }))
        };
        for item in list {
            let o = item.as_object().ok_or("a draw log event is not an object")?;
            let field = |name: &str| o.get(name).ok_or(format!("a draw log event has no {name}"));
            let op = field("op")?.as_str().ok_or("a draw log op is not a string")?;
            let at = || -> Result<Place, String> {
                Ok(match field("at")?.as_str() {
                    Some("top") => Place::Top,
                    Some("bottom") => Place::Bottom,
                    Some("random") => Place::Random,
                    Some("any") => Place::Any,
                    other => return Err(format!("a draw log place {other:?}")),
                })
            };
            let event = match op {
                "reset" | "take_top" => {
                    let cards = field("cards")?.as_array().ok_or("draw log cards are not a list")?;
                    let mut keys = Vec::with_capacity(cards.len());
                    for card in cards {
                        keys.push(key(card, &mut log)?);
                    }
                    if op == "reset" { Event::Reset(keys) } else { Event::TakeTop(keys) }
                }
                "add" | "move" | "take" => {
                    let card = key(field("card")?, &mut log)?;
                    let at = at()?;
                    match (op, at) {
                        ("add" | "move", Place::Top | Place::Bottom | Place::Random) => {
                            if op == "add" { Event::Add(card, at) } else { Event::Move(card, at) }
                        }
                        ("take", Place::Top | Place::Bottom | Place::Any) => Event::Take(card, at),
                        _ => return Err(format!("a draw log {op} at {at:?}")),
                    }
                }
                "relabel" => {
                    let card = key(field("card")?, &mut log)?;
                    let to = key(field("to")?, &mut log)?;
                    Event::Relabel(card, to)
                }
                "broken" => {
                    let why = o.get("why").and_then(Value::as_str).unwrap_or("no reason given");
                    return Err(format!("the recorder broke: {why}"));
                }
                _ => return Err(format!("a draw log op {op}")),
            };
            log.events.push(event);
        }
        if !log.events.is_empty() && !matches!(log.events[0], Event::Reset(_)) {
            return Err("the draw log does not start at a shuffle".into());
        }
        Ok(log)
    }

    /// The index of a key, made if new.
    pub fn key(&mut self, text: &str) -> u32 {
        match self.keys.iter().position(|k| k == text) {
            Some(i) => i as u32,
            None => {
                self.keys.push(text.to_string());
                (self.keys.len() - 1) as u32
            }
        }
    }
}

/// The pile in the filter: the known cards above the middle (top first), the middle's cards by key, and the known cards
/// below it (top first).
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
struct State {
    top: Vec<u32>,
    middle: Vec<u16>,
    bottom: Vec<u32>,
}

impl State {
    fn middle_size(&self) -> u32 {
        self.middle.iter().map(|&n| n as u32).sum()
    }

    fn size(&self) -> usize {
        self.top.len() + self.middle_size() as usize + self.bottom.len()
    }

    /// `k` put in at `at`, each way it can go with its chance.
    fn add(&self, k: u32, at: Place, out: &mut Vec<(State, f64)>) {
        match at {
            Place::Top => {
                let mut s = self.clone();
                s.top.insert(0, k);
                out.push((s, 1.0));
            }
            Place::Bottom => {
                let mut s = self.clone();
                s.bottom.push(k);
                out.push((s, 1.0));
            }
            Place::Random => {
                // n + 1 places: above each top card, the middle's size plus one next to the middle's cards, and below
                // each bottom card.
                let places = (self.size() + 1) as f64;
                for i in 0..self.top.len() {
                    let mut s = self.clone();
                    s.top.insert(i, k);
                    out.push((s, 1.0 / places));
                }
                let mut s = self.clone();
                s.middle[k as usize] += 1;
                out.push((s, (self.middle_size() + 1) as f64 / places));
                for j in 0..self.bottom.len() {
                    let mut s = self.clone();
                    s.bottom.insert(j + 1, k);
                    out.push((s, 1.0 / places));
                }
            }
            Place::Any => unreachable!("a card enters at a place"),
        }
    }

    /// Each card showing `k`, uniformly, taken out (`to` None) or showing `to` in its place.
    fn pick(&self, k: u32, to: Option<u32>, out: &mut Vec<(State, f64)>) {
        let copies = self.top.iter().filter(|&&x| x == k).count()
            + self.middle[k as usize] as usize
            + self.bottom.iter().filter(|&&x| x == k).count();
        if copies == 0 {
            return;
        }
        let each = 1.0 / copies as f64;
        let change = |cards: &mut Vec<u32>, i: usize| match to {
            Some(to) => cards[i] = to,
            None => {
                cards.remove(i);
            }
        };
        for i in (0..self.top.len()).filter(|&i| self.top[i] == k) {
            let mut s = self.clone();
            change(&mut s.top, i);
            out.push((s, each));
        }
        if self.middle[k as usize] > 0 {
            let mut s = self.clone();
            s.middle[k as usize] -= 1;
            if let Some(to) = to {
                s.middle[to as usize] += 1;
            }
            out.push((s, self.middle[k as usize] as f64 * each));
        }
        for j in (0..self.bottom.len()).filter(|&j| self.bottom[j] == k) {
            let mut s = self.clone();
            change(&mut s.bottom, j);
            out.push((s, each));
        }
    }

    /// The top `ks.len()` cards leave, seen as the multiset `ks`: none where they cannot have.
    fn take_top(&self, ks: &[u32]) -> Option<(State, f64)> {
        let m = ks.len();
        let mut need = vec![0i32; self.middle.len()];
        for &k in ks {
            need[k as usize] += 1;
        }
        let from_top = m.min(self.top.len());
        let in_middle = self.middle_size() as usize;
        let from_middle = (m - from_top).min(in_middle);
        let from_bottom = m - from_top - from_middle;
        for &k in self.top[..from_top].iter().chain(&self.bottom[..from_bottom]) {
            need[k as usize] -= 1;
        }
        let mut chance = 1.0 / choose(in_middle as u32, from_middle as u32);
        let mut s = State { top: self.top[from_top..].to_vec(), middle: self.middle.clone(), bottom: self.bottom[from_bottom..].to_vec() };
        for (k, &n) in need.iter().enumerate() {
            if n < 0 || n > self.middle[k] as i32 {
                return None;
            }
            chance *= choose(self.middle[k] as u32, n as u32);
            s.middle[k] -= n as u16;
        }
        Some((s, chance))
    }

    /// The bottom card leaves, seen as `k`.
    fn take_bottom(&self, k: u32) -> Option<(State, f64)> {
        let mut s = self.clone();
        if let Some(&last) = self.bottom.last() {
            s.bottom.pop();
            return (last == k).then_some((s, 1.0));
        }
        let in_middle = self.middle_size();
        if in_middle > 0 {
            let have = self.middle[k as usize];
            s.middle[k as usize] = have.checked_sub(1)?;
            return Some((s, have as f64 / in_middle as f64));
        }
        let last = s.top.pop()?;
        (last == k).then_some((s, 1.0))
    }

    /// The states after one event, each with its chance given this one.
    fn step(&self, event: &Event) -> Result<Vec<(State, f64)>, String> {
        let mut out = Vec::new();
        match event {
            Event::Reset(_) => unreachable!("a shuffle starts the filter again"),
            Event::Add(k, at) => self.add(*k, *at, &mut out),
            Event::Move(k, at) => {
                let mut taken = Vec::new();
                self.pick(*k, None, &mut taken);
                for (s, chance) in taken {
                    let mut put = Vec::new();
                    s.add(*k, *at, &mut put);
                    out.extend(put.into_iter().map(|(t, c)| (t, chance * c)));
                }
            }
            Event::Take(k, Place::Top) => {
                if self.size() == 0 {
                    return Err("the draw log takes from an empty pile".into());
                }
                out.extend(self.take_top(&[*k]));
            }
            Event::Take(k, Place::Bottom) => {
                if self.size() == 0 {
                    return Err("the draw log takes from an empty pile".into());
                }
                out.extend(self.take_bottom(*k));
            }
            Event::Take(k, _) => self.pick(*k, None, &mut out),
            Event::TakeTop(ks) => {
                if ks.len() > self.size() {
                    return Err("the draw log takes more cards from the top than the pile holds".into());
                }
                out.extend(self.take_top(ks));
            }
            Event::Relabel(k, to) => self.pick(*k, Some(*to), &mut out),
        }
        Ok(out)
    }
}

/// How many states the filter may hold at once: far more than any record met so far needs.
const MAX_STATES: usize = 1_000_000;

/// What a sample cost: the most states the filter held at once.
#[derive(Clone, Copy, Debug, Default)]
pub struct Cost {
    pub states: u32,
}

/// One order of the pile drawn from what the player knows: its cards' keys, top first. An empty record is an empty
/// pile's.
pub fn sample(log: &DrawLog, rng: &mut MegaRandom) -> Result<(Vec<u32>, Cost), String> {
    let keys = log.keys.len();
    // In an order of their own, so that a seed draws the same sample in every process.
    let mut states: BTreeMap<State, f64> = BTreeMap::new();
    let mut widest = 0usize;
    for (i, event) in log.events.iter().enumerate() {
        let mut next: BTreeMap<State, f64> = BTreeMap::new();
        if let Event::Reset(cards) = event {
            let mut middle = vec![0u16; keys];
            for &k in cards {
                middle[k as usize] += 1;
            }
            next.insert(State { top: Vec::new(), middle, bottom: Vec::new() }, 1.0);
        } else {
            for (state, &weight) in &states {
                for (after, chance) in state.step(event)? {
                    if chance > 0.0 {
                        *next.entry(after).or_insert(0.0) += weight * chance;
                    }
                }
            }
        }
        // Each step's probabilities sum to one, so that a long record never runs under the floats.
        let total: f64 = next.values().sum();
        if total <= 0.0 || total.is_nan() {
            return Err(format!("no order of the draw pile gives its record (event {} of {})", i + 1, log.events.len()));
        }
        next.values_mut().for_each(|w| *w /= total);
        widest = widest.max(next.len());
        if widest > MAX_STATES {
            return Err(format!("the draw log's filter holds more than {MAX_STATES} states"));
        }
        states = next;
    }
    let Some(last) = states.keys().next_back() else { return Ok((Vec::new(), Cost::default())) };
    let mut u = rng.next_double();
    let mut chosen = last;
    for (state, &weight) in &states {
        if u < weight {
            chosen = state;
            break;
        }
        u -= weight;
    }
    // The middle: a uniform arrangement of its cards.
    let mut middle: Vec<u32> = Vec::with_capacity(chosen.middle_size() as usize);
    for (k, &n) in chosen.middle.iter().enumerate() {
        middle.extend(std::iter::repeat_n(k as u32, n as usize));
    }
    shuffle(&mut middle, rng);
    let order = chosen.top.iter().copied().chain(middle).chain(chosen.bottom.iter().copied()).collect();
    Ok((order, Cost { states: widest as u32 }))
}

/// n choose r, as a float.
fn choose(n: u32, r: u32) -> f64 {
    if r > n {
        return 0.0;
    }
    let r = r.min(n - r);
    let mut value = 1.0;
    for i in 0..r {
        value = value * (n - i) as f64 / (i + 1) as f64;
    }
    value
}

/// Fisher-Yates.
pub fn shuffle<T>(items: &mut [T], rng: &mut MegaRandom) {
    for i in (1..items.len()).rev() {
        let j = rng.next_max(i as i32 + 1) as usize;
        items.swap(i, j);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The record's posterior over the pile's order, top first, by enumerating every real history the record allows:
    /// every arrangement of the shuffled cards and every hidden choice, each with its chance. The model is the one
    /// the sampler stands on, written plainly, card by card.
    fn exact(log: &DrawLog) -> HashMap<Vec<u32>, f64> {
        // A history: the pile as cards (an id and its key), and its chance.
        #[derive(Clone)]
        struct History {
            pile: Vec<(usize, u32)>,
            chance: f64,
        }
        let mut histories = vec![History { pile: Vec::new(), chance: 1.0 }];
        let mut next_id = 0usize;
        for event in &log.events {
            let mut after = Vec::new();
            for h in histories {
                match event {
                    Event::Reset(cards) => {
                        let ids: Vec<usize> = (0..cards.len()).map(|i| next_id + i).collect();
                        let perms = permutations(&ids);
                        let n = perms.len() as f64;
                        for perm in perms {
                            after.push(History {
                                pile: perm.iter().map(|&id| (id, cards[id - next_id])).collect(),
                                chance: h.chance / n,
                            });
                        }
                    }
                    Event::Add(k, at) => {
                        let id = next_id;
                        let places: Vec<usize> = match at {
                            Place::Top => vec![0],
                            Place::Bottom => vec![h.pile.len()],
                            _ => (0..=h.pile.len()).collect(),
                        };
                        let n = places.len() as f64;
                        for p in places {
                            let mut g = h.clone();
                            g.pile.insert(p, (id, *k));
                            g.chance /= n;
                            after.push(g);
                        }
                    }
                    Event::Move(k, at) => {
                        let picks: Vec<usize> = (0..h.pile.len()).filter(|&i| h.pile[i].1 == *k).collect();
                        let n = picks.len() as f64;
                        for i in picks {
                            let mut g = h.clone();
                            let card = g.pile.remove(i);
                            let places: Vec<usize> = match at {
                                Place::Top => vec![0],
                                Place::Bottom => vec![g.pile.len()],
                                _ => (0..=g.pile.len()).collect(),
                            };
                            let m = places.len() as f64;
                            for p in places {
                                let mut f = g.clone();
                                f.pile.insert(p, card);
                                f.chance /= n * m;
                                after.push(f);
                            }
                        }
                    }
                    Event::Take(k, Place::Top) => {
                        if h.pile.first().map(|c| c.1) == Some(*k) {
                            let mut g = h.clone();
                            g.pile.remove(0);
                            after.push(g);
                        }
                    }
                    Event::Take(k, Place::Bottom) => {
                        if h.pile.last().map(|c| c.1) == Some(*k) {
                            let mut g = h.clone();
                            g.pile.pop();
                            after.push(g);
                        }
                    }
                    Event::Take(k, _) | Event::Relabel(k, _) => {
                        let picks: Vec<usize> = (0..h.pile.len()).filter(|&i| h.pile[i].1 == *k).collect();
                        let n = picks.len() as f64;
                        for i in picks {
                            let mut g = h.clone();
                            match event {
                                Event::Relabel(_, to) => {
                                    g.pile[i].1 = *to;
                                }
                                _ => {
                                    g.pile.remove(i);
                                }
                            }
                            g.chance /= n;
                            after.push(g);
                        }
                    }
                    Event::TakeTop(ks) => {
                        let m = ks.len();
                        if h.pile.len() >= m {
                            let mut top: Vec<u32> = h.pile[..m].iter().map(|c| c.1).collect();
                            let mut want = ks.clone();
                            top.sort();
                            want.sort();
                            if top == want {
                                let mut g = h.clone();
                                g.pile.drain(..m);
                                after.push(g);
                            }
                        }
                    }
                }
            }
            if let Event::Reset(cards) = event {
                next_id += cards.len();
            } else if matches!(event, Event::Add(..)) {
                next_id += 1;
            }
            histories = after;
        }
        let total: f64 = histories.iter().map(|h| h.chance).sum();
        let mut posterior = HashMap::new();
        for h in histories {
            *posterior.entry(h.pile.iter().map(|c| c.1).collect::<Vec<u32>>()).or_insert(0.0) += h.chance / total;
        }
        posterior
    }

    fn permutations(items: &[usize]) -> Vec<Vec<usize>> {
        if items.len() <= 1 {
            return vec![items.to_vec()];
        }
        let mut all = Vec::new();
        for i in 0..items.len() {
            let mut rest = items.to_vec();
            let first = rest.remove(i);
            for mut p in permutations(&rest) {
                p.insert(0, first);
                all.push(p);
            }
        }
        all
    }

    /// The sampler's orders against the exact posterior: the largest difference of a probability, over many samples.
    fn check(log: &DrawLog, samples: u32) {
        let posterior = exact(log);
        let mut rng = MegaRandom::new(7);
        let mut seen: HashMap<Vec<u32>, f64> = HashMap::new();
        for _ in 0..samples {
            let (order, _) = sample(log, &mut rng).expect("a sample");
            *seen.entry(order).or_insert(0.0) += 1.0 / samples as f64;
        }
        for order in seen.keys() {
            assert!(posterior.contains_key(order), "the sampler gave {order:?}, which the record rules out");
        }
        // Each probability within five standard errors of the sampler's estimate.
        for (order, &p) in &posterior {
            let q = seen.get(order).copied().unwrap_or(0.0);
            let sd = (p * (1.0 - p) / samples as f64).sqrt();
            assert!((p - q).abs() <= 5.0 * sd + 1e-9, "{order:?}: exact {p:.4}, sampled {q:.4} (sd {sd:.4}) in {log:?}");
        }
    }

    fn log(keys: usize, events: Vec<Event>) -> DrawLog {
        DrawLog { keys: (0..keys).map(|k| format!("k{k}")).collect(), events }
    }

    use Event::*;
    use Place::*;

    #[test]
    fn plain_shuffle_and_draws() {
        check(&log(3, vec![Reset(vec![0, 0, 1, 2]), Take(0, Top)]), 200_000);
    }

    #[test]
    fn a_card_put_on_top() {
        // Headbutt: a card from outside on top, then the next draw.
        check(&log(3, vec![Reset(vec![0, 1, 2]), Add(0, Top)]), 200_000);
        check(&log(3, vec![Reset(vec![0, 1, 2]), Add(0, Top), Take(0, Top), Take(1, Top)]), 200_000);
    }

    #[test]
    fn random_inserts_seen_and_unseen() {
        // A Wound shuffled in, then draws that show it or not.
        check(&log(3, vec![Reset(vec![0, 0, 1]), Add(2, Random), Take(0, Top)]), 200_000);
        check(&log(3, vec![Reset(vec![0, 1, 1]), Add(2, Random), Take(1, Top), Take(2, Top)]), 200_000);
        // A key the shuffle also holds: whose copy was drawn is unknown.
        check(&log(3, vec![Reset(vec![0, 1, 2]), Add(0, Random), Add(0, Random), Take(0, Top)]), 200_000);
    }

    #[test]
    fn hidden_takes_and_moves() {
        check(&log(3, vec![Reset(vec![0, 0, 1, 2]), Add(0, Top), Take(0, Any)]), 200_000);
        check(&log(3, vec![Reset(vec![0, 1, 1, 2]), Move(1, Top), Take(1, Top), Take(1, Any)]), 200_000);
        check(&log(3, vec![Reset(vec![0, 1, 2, 2]), Move(2, Bottom), Add(1, Random), Take(2, Bottom)]), 200_000);
    }

    #[test]
    fn relabels_and_the_top_seen_together() {
        check(&log(4, vec![Reset(vec![0, 0, 1, 2]), Add(0, Top), Relabel(0, 3), Take(3, Top)]), 200_000);
        check(&log(3, vec![Reset(vec![0, 0, 1, 2]), Add(1, Random), TakeTop(vec![1, 0])]), 200_000);
    }

    #[test]
    fn random_inserts_around_known_cards() {
        // Known cards on top and at the bottom, then random places: between known cards, next to the middle.
        check(&log(4, vec![Reset(vec![0, 1, 2]), Add(3, Top), Add(3, Bottom), Add(0, Random), Add(3, Random), Take(3, Top)]), 200_000);
        check(&log(4, vec![Reset(vec![0, 1]), Add(2, Top), Add(3, Random), Add(3, Random), Take(3, Top), Take(2, Top)]), 200_000);
        // The middle emptied between known cards, then a card in at random, drawn down to the bottom.
        check(&log(3, vec![Reset(vec![0, 1]), Add(2, Top), Move(0, Bottom), Take(2, Top), Take(1, Top), Add(2, Random), Take(2, Top)]), 200_000);
        // Many random inserts of one key and the top seen together.
        check(&log(3, vec![Reset(vec![0, 1, 1]), Add(2, Random), Add(2, Random), Add(2, Random), TakeTop(vec![2, 1]), Take(2, Bottom)]), 200_000);
    }

    #[test]
    fn a_known_card_taken_where_the_player_cannot_see() {
        // Imbued: the card that started at the bottom plays itself; a copy put on top later.
        check(&log(3, vec![Reset(vec![0, 1, 2, 2]), Move(2, Bottom), Take(1, Top), Take(2, Any), Add(2, Top), Take(2, Top)]), 200_000);
        check(&log(3, vec![Reset(vec![0, 1, 2]), Add(2, Top), Move(2, Random), Take(2, Any), Relabel(0, 1), Take(1, Bottom)]), 200_000);
    }
}
