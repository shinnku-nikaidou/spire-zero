//! One turn searched depth first from a state to its ends (C#: SlPlanner.SearchTurn and its TurnDfs, where the
//! reasons are): every action from each state once, transpositions merged by the state key and the full hidden key,
//! the nodes shared over the first actions, each state where the turn or the combat ends valued by the leaf, `CHUNK`
//! at a time, a potion thrown away for nothing worth no more than one kept, and cycles set aside for `Loops`.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use super::bound::{self, Bounds};
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
}

/// C#: TurnSearch.
pub struct TurnSearch {
    /// The best end of the combat, if the turn can reach one, then the states at the next turn's start, best first.
    pub ends: Vec<TurnEnd>,
    pub nodes: i32,
    pub cut: bool,
    pub pruned: i32,
}

/// C#: SlPlanner.Mix.
pub fn mix(a: u64, b: u64) -> u64 {
    let mut z = a ^ (b.wrapping_add(0x9E3779B97F4A7C15).wrapping_add(a << 6).wrapping_add(a >> 2));
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

/// C#: SlPlanner's Chunk. The ends at the next turn's start the leaf is asked about at a time: a multiple of the net's
/// batch, so they are asked in the same batches as all together.
pub const CHUNK: usize = 256 * super::leaf::BATCH;

/// C#: SlPlanner.SearchTurn. The rest of the turn from `root`, with `nodes` nodes: the best end of the combat the turn
/// can reach, if any, and the best `keep` distinct states at the next turn's start. `history` is the record of enemy
/// moves up to `root`; a line stops at a state below its potions' bound in `bounds`.
pub fn search(root: &mut State, history: &Arc<History>, nodes: i32, keep: usize, bounds: &Bounds, leaf: &dyn Leaf) -> Result<TurnSearch, String> {
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
    };
    dfs.visit(root, Some(nodes))?;
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
    })
}

/// A line that ended the turn or the combat, as the world stood there, before the leaf has valued it (C#: Ended).
struct Ended {
    state: LeafState,
    actions: Vec<Action>,
    hashes: Vec<u64>,
    key: u64,
    shown: u64,
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
        bare: 0,
    };
    let outcome = leaf.outcome(&ended.state)?;
    Ok(ended.light().valued(outcome))
}

impl Dfs<'_> {
    /// C#: Visit. Tries every action from the state, once per state; at a state given `share_to`, the nodes up to it
    /// are shared over its actions evenly (and what the ones before left), below, a subtree spends its share depth first.
    fn visit(&mut self, state: &mut State, share_to: Option<i32>) -> Result<(), String> {
        if !self.seen.insert((state.state_key, state.hidden_key)) {
            return Ok(());
        }
        let hash = state.hash;
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
        let (todo, left) = order::actions(state, most as i64);
        self.cut |= left;
        let from = self.nodes;
        let mut moved = false;
        for (i, action) in todo.iter().enumerate() {
            let mut to = None;
            if let Some(end) = share_to {
                let share = from as i64 + (end as i64 - from as i64) * (i as i64 + 1) / todo.len() as i64;
                let at = (self.nodes + 1).max(share as i32);
                self.limit = at;
                to = Some(at);
            } else if moved && self.nodes >= self.limit {
                self.cut = true;
                break;
            }
            moved = true;
            self.nodes += 1;
            let mut after = state.step(action)?;
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
                self.visit(&mut after, to)?;
            } else if action.is_end_turn() || after.round() != self.round {
                self.ends(&mut after)?;
            } else if !self.loops.intercept(&self.path, &after, &self.actions, &self.hashes)? {
                self.visit(&mut after, None)?;
            }
            self.actions.pop();
            self.hashes.pop();
        }
        if plays {
            self.path.pop();
        }
        Ok(())
    }

    /// C#: Ends. The search's line ended here. A state another line already ended at is not asked about again.
    fn ends(&mut self, state: &mut State) -> Result<(), String> {
        if state.status != Status::InProgress {
            let ended = Ended {
                state: leaf_state(&self.history, state),
                actions: self.actions.clone(),
                hashes: self.hashes.clone(),
                key: 0,
                shown: state.hash,
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
            state: leaf_state(&self.history, state),
            actions: self.actions.clone(),
            hashes: self.hashes.clone(),
            key,
            shown: state.hash,
            bare,
        };
        self.ended.push(ended);
        self.going += 1;
        if self.going == CHUNK {
            self.price()?;
        }
        Ok(())
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
