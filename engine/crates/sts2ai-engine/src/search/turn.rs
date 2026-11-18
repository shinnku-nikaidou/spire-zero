//! One turn searched depth first from a state to its ends: every action from each state once, transpositions merged by
//! the state key and the full hidden key, each state where the turn or the combat ends valued by the leaf.
//!
//! Every action from the current state is tried, once per state: the plays where the player is on this turn
//! (`order.rs`), the answers where a card prompt waits. At the turn's start the nodes are shared over the actions, each
//! with an even share plus what the ones before it left, so every first action is looked at however many states the
//! turn has; a prompt that one of them raises shares that action's nodes over its answers the same way. Below, a
//! subtree spends its share depth first. A line ends with the combat, or where the player can play again after the
//! turn's end: a prompt the end of the turn or the next turn's start raises is answered first, so the leaf reads a
//! state the combat net knows. From a root that is such a prompt (`crossing`), the next turn's start is where a line
//! ends. A move back to a state on the path with something gained is a cycle, which would spend every node on its own
//! repeats: it goes to `Loops` instead of down.
//!
//! The search needs no end's value to go on, so the leaf is asked about a turn's ends together, `CHUNK` at a time; a
//! state another line already ended at is not asked about again. A turn has had 169 thousand ends (a Regent's second
//! turn of 9 cards and 4 energy, with Toasty Mittens' exhaust after it), each held with its observation and its run
//! until asked, which took gigabytes; `CHUNK` is a multiple of the net's batch, so the leaf is asked in the same batches
//! as about them all together.
//!
//! A potion can be thrown away in a fight too, for nothing: a state is worth at least the same state with fewer potions.
//! So each end of the turn at the next turn's start takes the outcome of the best end that differs from it only by
//! holding fewer of its potions, if the leaf values that one higher: where a potion drunk changed nothing else, the end
//! that kept it is worth as much.
//!
//! Ported line for line from the C# brain (plans/2026-11-18-2132-sl-search-rust.md, B and C); the C# search was deleted
//! on 2026-11-19 at the user's word (C5), and the `C#:` names below are where each piece came from, in git's history.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use super::bound::{self, Bounds};
use super::helpers::{Ahead, Helpers};
use super::leaf::{Leaf, LeafOutcome, LeafState};
use super::loops::{LoopPoint, Loops};
use super::order;
use super::state::{self, Action, History, State, Status};
use crate::protocol::{self, CombatObservation};
use crate::rt::Str;

/// C#: TurnEnd. A state where a turn of a line ends: at the next turn's first decision, after the enemies' turn, or
/// where the combat ended.
#[derive(Clone, Debug)]
pub struct TurnEnd {
    /// The actions that reach it from where the search began.
    pub actions: Vec<Action>,
    /// For each action, the hash of the observation it is played at.
    pub hashes: Vec<u64>,
    pub status: Status,
    pub value: f64,
    /// The state's key; 0 once the combat is over.
    pub key: u64,
    pub history: Arc<History>,
    pub win: f64,
    pub hp: f64,
    /// HP plus max HP at the state.
    pub life: i32,
    /// C#: LifeBound.Reach of the state.
    pub reach: i32,
    /// C#: LifeBound.Potions of the state.
    pub potions: String,
    /// The hash of the observation at the state.
    pub shown: u64,
    /// The world at the state exactly (`State::exact`), which the solver moves a state to another line by; 0 for a line
    /// whose states no solver keeps (one read from the C# side, PIMC's).
    pub exact: u64,
}

/// C#: TurnSearch.
pub struct TurnSearch {
    /// The best end of the combat, if the turn can reach one, then the states at the next turn's start, best first.
    pub ends: Vec<TurnEnd>,
    pub nodes: i32,
    pub cut: bool,
    pub pruned: i32,
    /// The world the turn was searched from, where the searcher gives it back (`threads.rs`): the beam and the solver
    /// keep it, so that a later turn below it starts there instead of replaying its path from the root (`Worlds`).
    pub start: Option<State>,
}

/// C#: SlPlanner.Mix.
pub fn mix(a: u64, b: u64) -> u64 {
    let mut z = a ^ (b.wrapping_add(0x9E3779B97F4A7C15).wrapping_add(a << 6).wrapping_add(a >> 2));
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

/// C#: SlPlanner's Chunk. The ends at the next turn's start the leaf is asked about at a time: a multiple of the net's
/// batch, so they are asked in the same batches as all together. Each is held with its observation and its run until
/// then: 4,096 of a big Regent deck in each of max's eight turns searched at once held most of 1.8 GB.
pub const CHUNK: usize = 16 * super::leaf::BATCH;

/// C#: SlPlanner.SearchTurn. The rest of the turn from `root`, with `nodes` nodes: the best end of the combat the turn
/// can reach, if any, and the best `keep` distinct states at the next turn's start. `history` is the record of enemy
/// moves up to `root`; a line stops at a state below its potions' bound in `bounds`. Where `crossing`, the root is a
/// prompt between its turn's end and the next turn's start, and the next turn's start is where its lines end: the
/// first state where the player can play.
pub fn search(
    root: &mut State, history: &Arc<History>, nodes: i32, keep: usize, bounds: &Bounds, crossing: bool, leaf: &dyn Leaf,
    helpers: Option<&Helpers>,
) -> Result<TurnSearch, String> {
    let round = root.round();
    let mut dfs = Dfs {
        leaf,
        history: history.clone(),
        bounds,
        seen: HashSet::new(),
        keys: HashSet::new(),
        ends: Vec::new(),
        ended: Vec::new(),
        priced: Vec::new(),
        outcomes: Vec::new(),
        going: 0,
        actions: Vec::new(),
        hashes: Vec::new(),
        path: Vec::new(),
        nodes: 0,
        round,
        limit: 0,
        pruned: 0,
        cut: false,
        loops: Loops::new(round),
        records: Vec::new(),
        crossing,
        helpers,
    };
    dfs.visit(root, Some(nodes), None)?;
    dfs.value();
    let found = {
        let leaf = dfs.leaf;
        let history = dfs.history.clone();
        dfs.loops.run_found(&|state: &mut State, actions: &[Action], hashes: &[u64]| end_of(leaf, &history, state, actions, hashes))?
    };
    for end in found {
        dfs.add(end);
    }
    dfs.cut |= dfs.loops.any;
    let mut over: Option<TurnEnd> = None;
    let mut going: Vec<TurnEnd> = Vec::new();
    for end in std::mem::take(&mut dfs.ends) {
        if end.status == Status::InProgress {
            going.push(end);
        } else if over.as_ref().is_none_or(|o| end.value > o.value) {
            // C#: MaxBy, the first of the best.
            over = Some(end);
        }
    }
    // C#: OrderByDescending(Value), a stable sort.
    going.sort_by(|a, b| b.value.partial_cmp(&a.value).unwrap_or(std::cmp::Ordering::Equal));
    if going.len() > keep {
        dfs.cut = true;
        going.truncate(keep);
    }
    let mut ends: Vec<TurnEnd> = over.into_iter().collect();
    ends.extend(going);
    Ok(TurnSearch {
        ends,
        nodes: dfs.nodes,
        cut: dfs.cut,
        pruned: dfs.pruned,
        start: None,
    })
}

/// A line that ended the turn or the combat, as the world stood there, before the leaf has valued it (C#: Ended).
struct Ended {
    state: LeafState,
    actions: Vec<Action>,
    hashes: Vec<u64>,
    key: u64,
    shown: u64,
    exact: u64,
    /// At the next turn's start, the key of the state with every potion thrown away; 0 once the combat is over.
    bare: u64,
}

/// What is kept of a line once the leaf has valued it: no observation or run (C#: Priced).
struct Priced {
    actions: Vec<Action>,
    hashes: Vec<u64>,
    status: Status,
    key: u64,
    bare: u64,
    potions: Vec<Option<Str>>,
    history: Arc<History>,
    life: i32,
    reach: i32,
    shown: u64,
    exact: u64,
}

impl Ended {
    fn light(self) -> Priced {
        let obs = &self.state.obs;
        Priced {
            potions: obs.potions.clone(),
            life: obs.player.hp + obs.player.max_hp,
            reach: bound::reach(obs),
            status: self.state.status,
            history: self.state.history,
            actions: self.actions,
            hashes: self.hashes,
            key: self.key,
            bare: self.bare,
            shown: self.shown,
            exact: self.exact,
        }
    }
}

impl Priced {
    fn valued(self, outcome: LeafOutcome) -> TurnEnd {
        TurnEnd {
            potions: bound::potions(&self.potions),
            actions: self.actions,
            hashes: self.hashes,
            status: self.status,
            value: outcome.value,
            key: self.key,
            history: self.history,
            win: outcome.win,
            hp: outcome.hp,
            life: self.life,
            reach: self.reach,
            shown: self.shown,
            exact: self.exact,
        }
    }
}

struct Dfs<'a> {
    leaf: &'a dyn Leaf,
    history: Arc<History>,
    bounds: &'a Bounds,
    seen: HashSet<(u64, u64)>,
    keys: HashSet<u64>,
    ends: Vec<TurnEnd>,
    /// The lines ended, in the order found, until the leaf is asked about them.
    ended: Vec<Ended>,
    /// The lines the leaf has valued, in the order found, with their outcomes, until the turn's ends are made of them.
    priced: Vec<Priced>,
    outcomes: Vec<LeafOutcome>,
    /// How many of `ended` are at the next turn's start.
    going: usize,
    actions: Vec<Action>,
    hashes: Vec<u64>,
    path: Vec<LoopPoint>,
    nodes: i32,
    round: i32,
    limit: i32,
    pruned: i32,
    cut: bool,
    loops: Loops,
    /// The records of enemy moves the ends hold, each once: a turn's ends mostly share one (1 to 13 among 6 to 55
    /// thousand ends in the big turns of a Regent run).
    records: Vec<Arc<History>>,
    /// The root is a prompt between its turn's end and the next turn's start (`search`).
    crossing: bool,
    /// The call's spare threads, which step states ahead of the search (helpers.rs).
    helpers: Option<&'a Helpers>,
}

/// The state reached here, for the leaf (C#: Reached's LeafState): its record of enemy moves goes on from the
/// search's with the observation, while the combat goes on.
fn leaf_state(history: &Arc<History>, state: &mut State) -> LeafState {
    let history = if state.status == Status::InProgress {
        Arc::new(state::witness(history, &state.obs))
    } else {
        history.clone()
    };
    LeafState {
        status: state.status,
        obs: state.obs.clone(),
        run: state.run(),
        history,
    }
}

/// C#: KeyOf. The key of a state at the next turn's start; 0 once the combat is over.
fn key_of(state: &State) -> u64 {
    if state.status == Status::InProgress { mix(state.state_key, state.hidden_key) } else { 0 }
}

/// C#: End. A line's end valued at once: the cycles' cash-outs compare the values as they go.
fn end_of(leaf: &dyn Leaf, history: &Arc<History>, state: &mut State, actions: &[Action], hashes: &[u64]) -> Result<TurnEnd, String> {
    let ended = Ended {
        state: leaf_state(history, state),
        actions: actions.to_vec(),
        hashes: hashes.to_vec(),
        key: key_of(state),
        shown: state.hash,
        exact: state.exact(),
        bare: 0,
    };
    let outcome = leaf.outcome(&ended.state)?;
    Ok(ended.light().valued(outcome))
}

impl Dfs<'_> {
    /// C#: Visit. Tries every action from the state, once per state; at a state given `share_to`, the nodes up to it
    /// are shared over its actions evenly (and what the ones before left), below, a subtree spends its share depth first.
    /// `ahead`: the state's actions with their steps, queued by a helper before the search came here.
    fn visit(&mut self, state: &mut State, share_to: Option<i32>, ahead: Option<Ahead>) -> Result<(), String> {
        if !self.seen.insert((state.state_key, state.hidden_key)) {
            if let Some(ahead) = ahead {
                ahead.drop_steps();
            }
            return Ok(());
        }
        // A prompt stands in the middle of an action: a cycle is a move between states where the player can play.
        let plays = state.obs.prompt.is_none();
        if plays {
            self.path.push(LoopPoint {
                obs: state.obs.clone(),
                depth: self.actions.len(),
                state: state.copy(),
            });
        }
        let most = share_to.unwrap_or(self.limit).wrapping_sub(self.nodes);
        // The steps queued for helpers: of every action but the first, which the search makes now; or of all of them,
        // queued by the helper that made this state (only where the player plays, whose actions take no `most`).
        let (todo, left, slots, first) = match ahead {
            Some(ahead) => (ahead.actions, false, ahead.slots, 0),
            None => {
                let (todo, left) = order::actions(state, most as i64);
                let slots = match self.helpers {
                    Some(helpers) if todo.len() > 1 => helpers.queue(state, &todo[1..], super::helpers::AHEAD),
                    _ => Vec::new(),
                };
                (todo, left, slots, 1)
            }
        };
        self.cut |= left;
        let from = self.nodes;
        let mut moved = false;
        // The slots taken so far; the rest are dropped when the search leaves the state.
        let mut taken = 0;
        let result = self.actions_of(state, share_to, &todo, &slots, first, from, &mut moved, &mut taken);
        for slot in &slots[taken..] {
            slot.drop_step();
        }
        result?;
        if plays {
            self.path.pop();
        }
        Ok(())
    }

    /// The search of each action of `state` in turn, `share_to` as for `visit`; the step of action i is `slots[i -
    /// first]` where there is one.
    #[allow(clippy::too_many_arguments)]
    fn actions_of(
        &mut self, state: &mut State, share_to: Option<i32>, todo: &[Action], slots: &[std::sync::Arc<super::helpers::Slot>], first: usize,
        from: i32, moved: &mut bool, taken: &mut usize,
    ) -> Result<(), String> {
        let hash = state.hash;
        for (i, action) in todo.iter().enumerate() {
            let mut to = None;
            if let Some(end) = share_to {
                let share = from as i64 + (end as i64 - from as i64) * (i as i64 + 1) / todo.len() as i64;
                let at = (self.nodes + 1).max(share as i32);
                self.limit = at;
                to = Some(at);
            } else if *moved && self.nodes >= self.limit {
                self.cut = true;
                break;
            }
            *moved = true;
            self.nodes += 1;
            let (mut after, mut ahead) = match i.checked_sub(first).and_then(|at| slots.get(at)) {
                Some(slot) => {
                    *taken = i - first + 1;
                    slot.take(self.helpers.expect("helpers where steps were queued"))?
                }
                None => (state.step(action)?, None),
            };
            self.actions.push(action.clone());
            self.hashes.push(hash);
            let status = after.status;
            let below = !self.bounds.is_empty()
                && self.bounds.get(&bound::potions(&after.obs.potions)).is_some_and(|&b| bound::reach(&after.obs) < b);
            if below {
                self.pruned += 1;
            } else if status != Status::InProgress {
                self.ends(&mut after)?;
            } else if after.obs.prompt.is_some() {
                self.visit(&mut after, to, None)?;
            } else if action.is_end_turn() || after.round() != self.round || self.crossing {
                self.ends(&mut after)?;
            } else if !self.loops.intercept(&self.path, &after, &self.actions, &self.hashes)? {
                self.visit(&mut after, None, ahead.take())?;
            }
            if let Some(ahead) = ahead {
                ahead.drop_steps();
            }
            self.actions.pop();
            self.hashes.pop();
        }
        Ok(())
    }

    /// C#: Ends. The search's line ended here. A state another line already ended at is not asked about again.
    fn ends(&mut self, state: &mut State) -> Result<(), String> {
        if state.status != Status::InProgress {
            let ended = Ended {
                state: self.leaf_state(state),
                actions: self.actions.clone(),
                hashes: self.hashes.clone(),
                key: 0,
                shown: state.hash,
                exact: state.hash,
                bare: 0,
            };
            self.ended.push(ended);
            return Ok(());
        }
        let hidden = state.hidden_key;
        let key = mix(state.state_key, hidden);
        if !self.keys.insert(key) {
            return Ok(());
        }
        let bare = if state.obs.potions.iter().all(|p| p.is_none()) {
            key
        } else {
            let thrown = CombatObservation {
                potions: vec![None; state.obs.potions.len()],
                ..(*state.obs).clone()
            };
            mix(protocol::state_key(&thrown), hidden)
        };
        let ended = Ended {
            state: self.leaf_state(state),
            actions: self.actions.clone(),
            hashes: self.hashes.clone(),
            key,
            shown: state.hash,
            exact: state.exact(),
            bare,
        };
        self.ended.push(ended);
        self.going += 1;
        if self.going == CHUNK {
            self.price()?;
        }
        Ok(())
    }

    /// The state reached here, for the leaf (`leaf_state`), its record of enemy moves the one another end holds where it
    /// is the same.
    fn leaf_state(&mut self, state: &mut State) -> LeafState {
        let mut reached = leaf_state(&self.history, state);
        match self.records.iter().find(|record| ***record == *reached.history) {
            Some(record) => reached.history = record.clone(),
            None => self.records.push(reached.history.clone()),
        }
        reached
    }

    /// C#: Price. Asks the leaf about the ends found since it was last asked, in the order found.
    fn price(&mut self) -> Result<(), String> {
        if self.ended.is_empty() {
            return Ok(());
        }
        let states: Vec<&LeafState> = self.ended.iter().map(|e| &e.state).collect();
        let outcomes = self.leaf.outcomes(&states)?;
        self.outcomes.extend(outcomes);
        self.priced.extend(self.ended.drain(..).map(Ended::light));
        self.going = 0;
        Ok(())
    }

    /// C#: Value. The turn's ends, from all the lines the search ended, valued in the order found.
    fn value(&mut self) {
        // A failure of the leaf would have been met at a chunk already, or is met here.
        if let Err(error) = self.price() {
            panic!("the leaf failed: {error}");
        }
        let mut outcomes = std::mem::take(&mut self.outcomes);
        self.thrown(&mut outcomes);
        for (priced, outcome) in std::mem::take(&mut self.priced).into_iter().zip(outcomes) {
            self.ends.push(priced.valued(outcome));
        }
    }

    /// C#: Thrown. A potion can be thrown away in a fight too, for nothing: each end at the next turn's start takes the
    /// outcome of the best end that differs from it only by holding fewer of its potions, if the leaf values that one
    /// higher.
    fn thrown(&self, outcomes: &mut [LeafOutcome]) {
        let mut order: Vec<u64> = Vec::new();
        let mut groups: HashMap<u64, Vec<usize>> = HashMap::new();
        for (i, priced) in self.priced.iter().enumerate() {
            if priced.bare == 0 {
                continue;
            }
            groups
                .entry(priced.bare)
                .or_insert_with(|| {
                    order.push(priced.bare);
                    Vec::new()
                })
                .push(i);
        }
        let held = |i: usize| self.priced[i].potions.iter().filter(|p| p.is_some()).count();
        for bare in order {
            let mut ends = groups.remove(&bare).expect("a group of its own");
            // By how many potions each holds (a stable sort), so that an end's own is final before one holding more
            // is compared with it.
            ends.sort_by_key(|&i| held(i));
            for m in 1..ends.len() {
                for f in 0..m {
                    if outcomes[ends[f]].value > outcomes[ends[m]].value && holds(&self.priced[ends[m]].potions, &self.priced[ends[f]].potions) {
                        outcomes[ends[m]] = outcomes[ends[f]];
                    }
                }
            }
        }
    }

    /// C#: Add.
    fn add(&mut self, end: TurnEnd) {
        if end.status == Status::InProgress && !self.keys.insert(end.key) {
            return;
        }
        self.ends.push(end);
    }
}

/// C#: Holds. Whether `belt` holds every potion `fewer` does, and more.
fn holds(belt: &[Option<Str>], fewer: &[Option<Str>]) -> bool {
    let mut left: Vec<Str> = belt.iter().flatten().copied().collect();
    for potion in fewer.iter().flatten() {
        match left.iter().position(|p| p == potion) {
            Some(i) => {
                left.remove(i);
            }
            None => return false,
        }
    }
    !left.is_empty()
}
