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
//! The search keeps its path of states itself, each with the actions it has left (`Frame`), so it can stop after a
//! number of nodes and go on later from there (`Turn`, `job`): it then visits, finds and asks the leaf about the same
//! states in the same order and batches as a search that never stopped.
//!
//! The solver's searches keep what they went through (`Graph`): each state by its world exactly, what each of its
//! actions came to, and each end with the leaf's outcome. A later search of the same turn from a state on it, where the
//! combat went on into the turn, takes it as its memo: at a state of the same world exactly it takes the graph's steps
//! instead of stepping the world, and the graph's ends instead of asking the leaf, and it makes a world only where it
//! comes to one the graph does not hold (a line that reached a state otherwise first, there with its piles in another
//! order), a line the graph's bounds pruned, or a move a cycle could come back by (`loops::Recur` tells it without a
//! world). It finds what a search without the memo finds, to the last bit but the leaf's values, which come from other
//! batches. Measured on the 4 biggest turns of a Regent A10 run, from 12 states into each: 21.6 s alone, 1.7 s with it.
//! A search that paused before its end is a memo too, of what it went through so far (`Memo::Paused`): an action it had
//! not tried yet is stepped. A search with a memo keeps in its own graph what it took from it, so it is the next one's
//! memo in turn; the steps it takes from a memo are no nodes of a job's (`job`), as they step no world.
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
//! Of the turn's ends at the next turn's start, one another dominates is dropped (`dominance.rs`, a rule written by hand
//! at the user's word): the same in all but the enemies' HP, their debuffs, the player's HP and the allies', and worse in
//! those.
//!
//! Ported line for line from the C# brain (plans/2026-11-18-2132-sl-search-rust.md, B and C); the C# search was deleted
//! on 2026-11-19 at the user's word (C5), and the `C#:` names below are where each piece came from, in git's history.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use super::bound::{self, Bounds};
use super::dominance::{self, Shape};
use super::beam::Job;
use super::helpers::{Ahead, Helpers, Slot};
use super::leaf::{Leaf, LeafOutcome, LeafState};
use super::loops::{self, LoopPoint, Loops, Recur};
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
    /// A search that stopped where its job said (`Job::most`), to go on with in a later job (`Job::resume`): nothing
    /// of the turn is in `ends` yet.
    pub paused: Option<Box<Turn>>,
    /// What the search went through, where its job asked for it (`Job::record`) and it was not cut and found no
    /// cycle: a later search of the turn from a state on it takes it instead of stepping the worlds again (`Memo`).
    pub graph: Option<Arc<Graph>>,
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
    let mut turn = Turn::new(root, history, nodes, bounds.clone(), crossing, false, None);
    turn.go(leaf, helpers, None)?;
    turn.end(leaf, keep)
}

/// What a search takes as its memo (`Turn::new`): an earlier search's graph, or an earlier search of the turn that
/// paused before its end, whose graph so far is made when the search begins (`Turn::graph_so_far`).
pub enum Memo {
    Graph(Arc<Graph>),
    Paused(Box<Turn>),
}

/// A job's turn (`Job`): the search it paused gone on with (`Job::resume`), else one from `state()`, each spending
/// at most the job's `most` nodes before it pauses again (`TurnSearch::paused`). The nodes are the ones spent in this
/// call, steps taken from a memo not counted (`Dfs::replayed`); a search begun here gives back the world it began at
/// (`TurnSearch::start`).
#[allow(clippy::too_many_arguments)]
pub fn job(
    job: &Job, state: impl FnOnce() -> Result<State, String>, nodes: i32, keep: usize, bounds: &Bounds, leaf: &dyn Leaf,
    helpers: Option<&Helpers>,
) -> Result<TurnSearch, String> {
    let paused = job.resume.lock().expect("a job's paused search").take();
    let (mut turn, start) = match paused {
        Some(turn) => (turn, None),
        None => {
            let state = state()?;
            let memo = match job.memo.lock().expect("a job's memo").take() {
                Some(Memo::Graph(graph)) => Some(graph),
                Some(Memo::Paused(turn)) => turn.graph_so_far(leaf)?,
                None => None,
            };
            (Box::new(Turn::new(&state, &job.history, nodes, bounds.clone(), job.crossing, job.record, memo)), Some(state))
        }
    };
    let before = turn.dfs.live();
    let done = turn.go(leaf, helpers, job.most.map(|most| before.saturating_add(most)))?;
    let spent = turn.dfs.live() - before;
    let mut found = if done {
        (*turn).end(leaf, keep)?
    } else {
        TurnSearch {
            ends: Vec::new(),
            nodes: 0,
            cut: false,
            pruned: 0,
            start: None,
            paused: Some(turn),
            graph: None,
        }
    };
    found.nodes = spent;
    found.start = start;
    Ok(found)
}

/// A turn's search, depth first from its root (`Dfs`), which can stop after a number of nodes and go on later from
/// where it stopped: the states on its path are kept, each with the actions it has left, and all it found so far.
/// Going on, it visits the same states in the same order, and asks the leaf about the same ends in the same batches, as
/// a search that never stopped.
pub struct Turn {
    dfs: Dfs,
    /// The state the search begins at, until it does.
    root: Option<State>,
    /// The nodes the search may spend in all (`search`).
    nodes: i32,
}

impl Turn {
    /// `record`: the search keeps what it goes through (`TurnSearch::graph`); `memo`, what an earlier search of the
    /// turn went through, to take where it holds the same worlds (`Dfs::memo`).
    pub fn new(
        root: &State, history: &Arc<History>, nodes: i32, bounds: Bounds, crossing: bool, record: bool, memo: Option<Arc<Graph>>,
    ) -> Turn {

        let round = root.round();
        Turn {
            dfs: Dfs {
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
                stack: Vec::new(),
                nodes: 0,
                replayed: 0,
                round,
                limit: 0,
                pruned: 0,
                cut: false,
                loops: Loops::new(round),
                records: Vec::new(),
                crossing,
                graph: record.then(Recorder::default),
                memo,
            },
            root: Some(root.copy()),
            nodes,
        }
    }

    /// The lines it stopped at a state below its potions' bound so far.
    pub fn pruned(&self) -> i32 {
        self.dfs.pruned
    }

    /// What the search went through so far, for a search of the turn from a state on it to take as its memo, its ends
    /// asked of the leaf now: the states it left with every action tried, and those still on its path with the actions
    /// tried so far (the rest not tried, `To::Untried`). None where it keeps nothing, has not begun, or a cycle cut it.
    pub fn graph_so_far(mut self, leaf: &dyn Leaf) -> Result<Option<Arc<Graph>>, String> {
        let dfs = &mut self.dfs;
        if self.root.is_some() || dfs.graph.is_none() || dfs.cut || dfs.loops.any {
            return Ok(None);
        }
        dfs.price(leaf)?;
        dfs.keep_ends();
        Ok(dfs.graph.take().map(|recorder| Arc::new(recorder.graph)))
    }

    /// Searches on, until the search is done (true) or has spent `until` nodes in all, steps taken from its memo not
    /// counted (false: it goes on from there at the next call). The steps queued for `helpers` and not taken go when it
    /// stops: helpers live for one call.
    pub fn go(&mut self, leaf: &dyn Leaf, helpers: Option<&Helpers>, until: Option<i32>) -> Result<bool, String> {
        let cx = Cx { leaf, helpers };
        if let Some(mut root) = self.root.take() {
            let exact = if self.dfs.graph.is_some() || self.dfs.memo.is_some() { root.exact() } else { 0 };
            self.dfs.enter(&cx, root, Some(self.nodes), None, exact);
        }
        let went = self.dfs.walk(&cx, until);
        if !matches!(went, Ok(true)) {
            self.dfs.unslot();
        }
        went
    }

    /// The turn's search once it is done (`go`): its ends valued, the cycles found cashed out, and the ends sorted.
    pub fn end(mut self, leaf: &dyn Leaf, keep: usize) -> Result<TurnSearch, String> {
        let dfs = &mut self.dfs;
        dfs.value(leaf);
        let found = {
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
        let graph = dfs.graph.take().filter(|_| !dfs.cut).map(|recorder| Arc::new(recorder.graph));
        Ok(TurnSearch {
            ends,
            nodes: dfs.nodes,
            cut: dfs.cut,
            pruned: dfs.pruned,
            start: None,
            paused: None,
            graph,
        })
    }
}

/// What a turn's search went through, for a later search of the same turn from a state on it (`Memo`) to take
/// instead of stepping the worlds and asking the leaf again: each state it came to the first time, with its world
/// exactly and what each of its actions led to, and each end, with what the leaf said of it before `thrown`.
#[derive(Default)]
pub struct Graph {
    /// The actions of its steps, each once.
    actions: Vec<Action>,
    /// The states, in the order the search came to them; the root first.
    states: Vec<Visited>,
    /// The states by their keys.
    by_key: HashMap<(u64, u64), u32>,
    /// The potions held at its states and ends (`bound::potions`), each once.
    belts: Vec<String>,
    steps: Vec<Step>,
    arrivals: Vec<Arrival>,
    /// The ends, in the order the leaf was asked about them.
    ends: Vec<Valued>,
}

/// A state of a graph: its keys and its world exactly, its observation's hash, its actions' steps (`count` of them
/// from `first`), and, where the player can play, what a cycle back to it is told by (`Recur`).
struct Visited {
    key: (u64, u64),
    exact: u64,
    hash: u64,
    /// Its potions, by their place among the graph's belts, and its `bound::reach`, which a search's bounds prune it
    /// by: both read only what its keys hold, so they are the same for any world of them.
    belt: u32,
    reach: i32,
    first: u32,
    count: u32,
    recur: Option<Recur>,
}

/// An action of a state, by its place among the graph's actions, what it came to, and that world exactly where it is
/// a state.
#[derive(Clone, Copy)]
struct Step {
    action: u32,
    to: To,
    exact: u64,
    /// Where it came to a state with another world than the graph's: whether a cycle back from it is told as from the
    /// graph's (`Recur`), so the search's look for one goes the same way.
    same: bool,
    /// Where the graph's search pruned it: its potions, by their place among the graph's belts, and its reach, which a
    /// search with other bounds prunes it by.
    belt: u32,
    reach: i32,
}

/// What a step came to.
#[derive(Clone, Copy)]
enum To {
    /// Not tried: the search's nodes ran out first.
    Untried,
    /// A state below its potions' bound.
    Pruned,
    /// A move back to a state on the path, which the search took for a cycle.
    Cycle,
    /// A state, by its place among the graph's states.
    State(u32),
    /// An end, by its place among the arrivals.
    End(u32),
}

/// A step to an end: the state's key (0 once the combat is over), its world exactly and its observation's hash there,
/// and the end the leaf was asked about for that key.
struct Arrival {
    key: u64,
    exact: u64,
    shown: u64,
    end: u32,
}

/// An end of a graph, with the leaf's outcome before `thrown`.
struct Valued {
    status: Status,
    key: u64,
    bare: u64,
    potions: Vec<Option<Str>>,
    /// `potions` by its place among the graph's belts.
    belt: u32,
    history: Arc<History>,
    life: i32,
    reach: i32,
    outcome: LeafOutcome,
    shape: Option<Shape>,
}

impl Valued {
    fn of(priced: &Priced, outcome: LeafOutcome, belt: u32) -> Valued {
        Valued {
            status: priced.status,
            key: priced.key,
            bare: priced.bare,
            potions: priced.potions.clone(),
            belt,
            history: priced.history.clone(),
            life: priced.life,
            reach: priced.reach,
            outcome,
            shape: priced.shape.clone(),
        }
    }
}

impl Graph {
    /// The steps the search that made it tried: its nodes.
    pub fn nodes(&self) -> usize {
        self.steps.iter().filter(|step| !matches!(step.to, To::Untried)).count()
    }
}

/// A graph as the search goes, with what it is found by.
#[derive(Default)]
struct Recorder {
    graph: Graph,
    actions: HashMap<Action, u32>,
    belts: HashMap<String, u32>,
    /// The ends at the next turn's start by their keys.
    ends_by_key: HashMap<u64, u32>,
}

impl Recorder {
    /// A state the search came to the first time, with its actions to try; its place among the graph's states.
    fn state(&mut self, state: &State, exact: u64, plays: bool, todo: &[Action]) -> u32 {
        let visited = Visited {
            key: (state.state_key, state.hidden_key),
            exact,
            hash: state.hash,
            belt: self.belt(bound::potions(&state.obs.potions)),
            reach: bound::reach(&state.obs),
            first: 0,
            count: 0,
            recur: plays.then(|| Recur::of(&state.obs)),
        };
        self.add(visited, todo.iter())
    }

    /// State `m` of the memo `graph`, come to the first time without a world (`Dfs::enter_memo`), as `state`.
    fn known(&mut self, graph: &Graph, m: u32) -> u32 {
        let state = &graph.states[m as usize];
        let visited = Visited {
            belt: self.belt(graph.belts[state.belt as usize].clone()),
            recur: state.recur.clone(),
            ..*state
        };
        let steps = &graph.steps[state.first as usize..(state.first + state.count) as usize];
        self.add(visited, steps.iter().map(|step| &graph.actions[step.action as usize]))
    }

    /// `visited` with its actions `todo`, none tried yet.
    fn add<'a>(&mut self, mut visited: Visited, todo: impl Iterator<Item = &'a Action>) -> u32 {
        let at = self.graph.states.len() as u32;
        visited.first = self.graph.steps.len() as u32;
        for action in todo {
            let next = self.actions.len() as u32;
            let id = *self.actions.entry(action.clone()).or_insert_with(|| {
                self.graph.actions.push(action.clone());
                next
            });
            self.graph.steps.push(Step { action: id, to: To::Untried, exact: 0, same: true, belt: 0, reach: 0 });
        }
        visited.count = self.graph.steps.len() as u32 - visited.first;
        self.graph.by_key.insert(visited.key, at);
        self.graph.states.push(visited);
        at
    }

    /// A belt's place among the graph's belts.
    fn belt(&mut self, belt: String) -> u32 {
        let next = self.graph.belts.len() as u32;
        *self.belts.entry(belt).or_insert_with_key(|belt| {
            self.graph.belts.push(belt.clone());
            next
        })
    }

    /// What action `i` of state `at` came to (a state is found by its keys `key`), its world there `exact`, `same`
    /// as for `Step`, and where it was pruned, the belt and reach it was pruned by.
    #[allow(clippy::too_many_arguments)]
    fn step(&mut self, at: u32, i: usize, to: To, key: (u64, u64), exact: u64, same: bool, pruned: Option<(String, i32)>) {
        let to = match to {
            To::State(_) => To::State(self.graph.by_key[&key]),
            other => other,
        };
        let (belt, reach) = match pruned {
            Some((belt, reach)) => (self.belt(belt), reach),
            None => (0, 0),
        };
        let step = &mut self.graph.steps[(self.graph.states[at as usize].first as usize) + i];
        step.to = to;
        step.exact = exact;
        step.same = same;
        step.belt = belt;
        step.reach = reach;
    }

    /// Whether `state`, where the player can play, is told for a cycle as the graph's state of its keys is, where the
    /// graph has one with another world.
    fn same(&self, state: &State, exact: u64) -> bool {
        match self.graph.by_key.get(&(state.state_key, state.hidden_key)) {
            Some(&at) => {
                let known = &self.graph.states[at as usize];
                known.exact == exact || known.recur.as_ref().is_some_and(|recur| *recur == Recur::of(&state.obs))
            }
            None => true,
        }
    }
}

/// What a search asks things of while it goes: the leaf, and the call's spare threads (helpers.rs).
struct Cx<'a> {
    leaf: &'a dyn Leaf,
    helpers: Option<&'a Helpers>,
}

/// A state on the search's path (C#: a call of Visit): its actions, the next one to try, and its share of the nodes.
struct Frame {
    /// The world, made where the search needs it: a state the memo holds (`memo`) goes without one until then.
    state: Option<State>,
    /// Its place among the memo's states, where the memo holds its world exactly: its actions are the memo's steps.
    memo: Option<u32>,
    /// How many actions of the path lead to it.
    depth: usize,
    /// Where the nodes up to it are shared over its actions (`Dfs::walk`).
    share_to: Option<i32>,
    todo: Vec<Action>,
    /// The steps queued for helpers: of action i, `slots[i - first]`.
    slots: Vec<Arc<Slot>>,
    first: usize,
    /// The nodes spent when the search came here.
    from: i32,
    /// Whether an action of it was tried.
    moved: bool,
    /// The slots taken so far; the rest are dropped when the search leaves the state.
    taken: usize,
    /// The action tried next.
    next: usize,
    /// Its budget ran out: no more of its actions are tried.
    done: bool,
    /// The player can play here, so it is a point of the path cycles are looked for from (`Loops`).
    plays: bool,
    /// Its place among the graph's states, where the search keeps one (`Recorder`).
    gid: u32,
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
    /// What the dominance among the turn's ends reads of it (`dominance::shape`).
    shape: Option<Shape>,
}

/// An end found, until the leaf is asked about it.
enum Pending {
    Ask(Ended),
    /// An end the memo's search found (`Dfs::memo`), with the leaf's outcome there.
    Known(Priced, LeafOutcome),
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
    shape: Option<Shape>,
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
            shape: self.shape,
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

struct Dfs {
    history: Arc<History>,
    bounds: Bounds,
    seen: HashSet<(u64, u64)>,
    keys: HashSet<u64>,
    ends: Vec<TurnEnd>,
    /// The lines ended, in the order found, until the leaf is asked about them.
    ended: Vec<Pending>,
    /// The lines the leaf has valued, in the order found, with their outcomes, until the turn's ends are made of them.
    priced: Vec<Priced>,
    outcomes: Vec<LeafOutcome>,
    /// How many of `ended` are at the next turn's start, to ask the leaf about.
    going: usize,
    actions: Vec<Action>,
    hashes: Vec<u64>,
    path: Vec<LoopPoint>,
    /// The states the search stands on, the root first.
    stack: Vec<Frame>,
    nodes: i32,
    /// The nodes that were steps taken from the memo, no world stepped (`memo_step`).
    replayed: i32,
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
    /// What the search goes through, where it keeps it.
    graph: Option<Recorder>,
    /// What an earlier search of this turn went through (`Graph`), which this one takes where it comes to the same
    /// world exactly, a step pruned there by that search's bounds made again.
    memo: Option<Arc<Graph>>,
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
        shape: None,
    };
    let outcome = leaf.outcome(&ended.state)?;
    Ok(ended.light().valued(outcome))
}

impl Dfs {
    /// C#: Visit, where the search comes to a state: the first time, the state goes on the path with its actions
    /// (true), `share_to` as for `walk`, `ahead` its actions with their steps where a helper queued them before the
    /// search came here; a state met before is not searched again (false). `exact`: the world exactly, where the search
    /// keeps what it goes through or takes from a memo, whose steps then are its actions where it is the same world.
    fn enter(&mut self, cx: &Cx, mut state: State, share_to: Option<i32>, ahead: Option<Ahead>, exact: u64) -> bool {
        let key = (state.state_key, state.hidden_key);
        if !self.seen.insert(key) {
            if let Some(ahead) = ahead {
                ahead.drop_steps();
            }
            return false;
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
        let memo = self.memo.as_ref().and_then(|graph| graph.by_key.get(&key).copied().filter(|&m| graph.states[m as usize].exact == exact));
        let most = share_to.unwrap_or(self.limit).wrapping_sub(self.nodes);
        // The steps queued for helpers: of every action but the first, which the search makes now; or of all of them,
        // queued by the helper that made this state (only where the player plays, whose actions take no `most`).
        let (todo, left, slots, first) = match (ahead, memo) {
            (ahead, Some(m)) => {
                if let Some(ahead) = ahead {
                    ahead.drop_steps();
                }
                (self.memo_actions(m), false, Vec::new(), 0)
            }
            (Some(ahead), None) => (ahead.actions, false, ahead.slots, 0),
            (None, None) => {
                let (todo, left) = order::actions(&mut state, most as i64);
                let slots = match cx.helpers {
                    Some(helpers) if todo.len() > 1 => helpers.queue(&state, &todo[1..], super::helpers::AHEAD),
                    _ => Vec::new(),
                };
                (todo, left, slots, 1)
            }
        };
        self.cut |= left;
        let gid = match &mut self.graph {
            Some(recorder) => recorder.state(&state, exact, plays, &todo),
            None => u32::MAX,
        };
        self.stack.push(Frame {
            state: Some(state),
            memo,
            depth: self.actions.len(),
            share_to,
            todo,
            slots,
            first,
            from: self.nodes,
            moved: false,
            taken: 0,
            next: 0,
            done: false,
            plays,
            gid,
        });
        true
    }

    /// `enter` for state `m` of the memo, met where the memo holds its world: no world is made for it until the
    /// search needs one (`world`).
    fn enter_memo(&mut self, m: u32, share_to: Option<i32>) -> bool {
        let graph = self.memo.clone().expect("a memo");
        let state = &graph.states[m as usize];
        if !self.seen.insert(state.key) {
            return false;
        }
        let gid = match &mut self.graph {
            Some(recorder) => recorder.known(&graph, m),
            None => u32::MAX,
        };
        self.stack.push(Frame {
            state: None,
            memo: Some(m),
            depth: self.actions.len(),
            share_to,
            todo: self.memo_actions(m),
            slots: Vec::new(),
            first: 0,
            from: self.nodes,
            moved: false,
            taken: 0,
            next: 0,
            done: false,
            plays: state.recur.is_some(),
            gid,
        });
        true
    }

    /// The actions of state `m` of the memo, in its order.
    fn memo_actions(&self, m: u32) -> Vec<Action> {
        let graph = self.memo.as_ref().expect("a memo");
        let state = &graph.states[m as usize];
        graph.steps[state.first as usize..(state.first + state.count) as usize].iter().map(|step| graph.actions[step.action as usize].clone()).collect()
    }

    /// The world at the state `at` on the path, made where it is not yet: stepped from the nearest state below with
    /// one, along the path (the states with a world are the path's beginning). Each state made where the player can
    /// play goes on `path`, in its place.
    fn world(&mut self, at: usize) -> Result<&mut State, String> {
        let from = (0..=at).rev().find(|&f| self.stack[f].state.is_some()).expect("the root's world");
        for f in from + 1..=at {
            let action = self.actions[self.stack[f].depth - 1].clone();
            let world = self.stack[f - 1].state.as_mut().expect("the world below").step(&action)?;
            if self.stack[f].plays {
                self.path.push(LoopPoint {
                    obs: world.obs.clone(),
                    depth: self.stack[f].depth,
                    state: world.copy(),
                });
            }
            self.stack[f].state = Some(world);
        }
        Ok(self.stack[at].state.as_mut().expect("the world just made"))
    }

    /// Whether a state on the path where the player can play `recurs` at `now`, told by the worlds made and the memo's.
    fn recurs_on_path(&self, now: &Recur) -> bool {
        let graph = self.memo.as_ref().expect("a memo");
        self.stack.iter().filter(|frame| frame.plays).any(|frame| match (&frame.state, frame.memo) {
            (Some(state), _) => loops::recurs_of(&*state.obs, now),
            (None, Some(m)) => loops::recurs_of(graph.states[m as usize].recur.as_ref().expect("a state where the player plays"), now),
            (None, None) => unreachable!("a state on the path with neither a world nor the memo's"),
        })
    }

    /// Action `i` of the memo's state `m`, `to` as for `walk`, as the memo's search took it: where the memo holds what
    /// the search needs of the step (no world to step), what came of it (`Some(entered)` as for `enter`), kept in this
    /// search's graph as state `gid`'s, where it keeps one; else None, and the search steps the world. The step is
    /// pruned by this search's bounds as a stepped one is, by the potions and reach of what it came to; where the
    /// player can play after it, the search looks for a cycle back to a state on the path first, which the memo holds
    /// where it came to that state with a world told the same (`Step::same`).
    fn memo_step(&mut self, m: u32, i: usize, to: Option<i32>, gid: u32) -> Option<bool> {
        let graph = self.memo.clone()?;
        let step = graph.steps[(graph.states[m as usize].first as usize) + i];
        let below = |belt: u32, reach: i32| !self.bounds.is_empty() && self.bounds.get(&graph.belts[belt as usize]).is_some_and(|&b| reach < b);
        let pruned = |belt: u32, reach: i32| (To::Pruned, (0, 0), 0, true, Some((graph.belts[belt as usize].clone(), reach)));
        // What came of it, as `Recorder::step` keeps it.
        let (entered, (to_step, key, exact, same, by)) = match step.to {
            To::End(a) => {
                let arrival = &graph.arrivals[a as usize];
                let valued = &graph.ends[arrival.end as usize];
                if below(valued.belt, valued.reach) {
                    self.pruned += 1;
                    (false, pruned(valued.belt, valued.reach))
                } else {
                    let at = (self.priced.len() + self.ended.len()) as u32;
                    let end = if valued.status != Status::InProgress || self.keys.insert(arrival.key) {
                        let priced = Priced {
                            actions: self.actions.clone(),
                            hashes: self.hashes.clone(),
                            status: valued.status,
                            key: valued.key,
                            bare: valued.bare,
                            potions: valued.potions.clone(),
                            history: valued.history.clone(),
                            life: valued.life,
                            reach: valued.reach,
                            shown: arrival.shown,
                            exact: arrival.exact,
                            shape: valued.shape.clone(),
                        };
                        self.ended.push(Pending::Known(priced, valued.outcome));
                        if let Some(recorder) = &mut self.graph
                            && valued.status == Status::InProgress
                        {
                            recorder.ends_by_key.insert(arrival.key, at);
                        }
                        at
                    } else {
                        // The state another line ended at first.
                        self.graph.as_ref().map_or(0, |recorder| recorder.ends_by_key[&arrival.key])
                    };
                    (false, (self.arrival(arrival.key, arrival.exact, arrival.shown, end), (0, 0), 0, true, None))
                }
            }
            To::State(c) => {
                let child = &graph.states[c as usize];
                if below(child.belt, child.reach) {
                    self.pruned += 1;
                    (false, pruned(child.belt, child.reach))
                } else {
                    let world = child.exact == step.exact;
                    if let Some(now) = &child.recur
                        && (!(world || step.same) || self.recurs_on_path(now))
                    {
                        return None;
                    }
                    let entered = if self.seen.contains(&child.key) {
                        false
                    } else if !world {
                        return None;
                    } else {
                        self.enter_memo(c, if child.recur.is_none() { to } else { None })
                    };
                    (entered, (To::State(c), child.key, step.exact, step.same, None))
                }
            }
            // Pruned by the graph's bounds and by these too, else made again.
            To::Pruned if below(step.belt, step.reach) => {
                self.pruned += 1;
                (false, pruned(step.belt, step.reach))
            }
            To::Untried | To::Pruned | To::Cycle => return None,
        };
        if let Some(recorder) = &mut self.graph {
            recorder.step(gid, i, to_step, key, exact, same, by);
        }
        Some(entered)
    }

    /// C#: Visit. Tries every action of the state on top of the path in turn, each line down depth first, until the
    /// path is empty (true), or until the search has spent `until` nodes (false; the search goes on from there). At a
    /// state given `share_to`, the nodes up to it are shared over its actions evenly (and what the ones before left);
    /// below, a subtree spends its share depth first.
    fn walk(&mut self, cx: &Cx, until: Option<i32>) -> Result<bool, String> {
        while let Some(top) = self.stack.len().checked_sub(1) {
            let frame = &mut self.stack[top];
            let i = frame.next;
            if frame.done || i >= frame.todo.len() {
                self.leave();
                continue;
            }
            if until.is_some_and(|until| self.nodes - self.replayed >= until) {
                return Ok(false);
            }
            let mut to = None;
            if let Some(end) = frame.share_to {
                let share = frame.from as i64 + (end as i64 - frame.from as i64) * (i as i64 + 1) / frame.todo.len() as i64;
                let at = (self.nodes + 1).max(share as i32);
                self.limit = at;
                to = Some(at);
            } else if frame.moved && self.nodes >= self.limit {
                self.cut = true;
                frame.done = true;
                continue;
            }
            frame.moved = true;
            frame.next = i + 1;
            self.nodes += 1;
            let action = frame.todo[i].clone();
            let (gid, memo) = (frame.gid, frame.memo);
            let hash = match (&frame.state, memo) {
                (Some(state), _) => state.hash,
                (None, m) => self.memo.as_ref().expect("a memo").states[m.expect("the memo's state") as usize].hash,
            };
            self.actions.push(action.clone());
            self.hashes.push(hash);
            let entered = match memo.and_then(|m| self.memo_step(m, i, to, gid)) {
                Some(entered) => {
                    self.replayed += 1;
                    entered
                }
                None => self.step(cx, top, i, &action, to, gid)?,
            };
            if !entered {
                self.actions.pop();
                self.hashes.pop();
            }
        }
        Ok(true)
    }

    /// Action `i` of the state `top` on the path, `to` as for `walk`, the world stepped: what came of it, as for
    /// `enter`.
    fn step(&mut self, cx: &Cx, top: usize, i: usize, action: &Action, to: Option<i32>, gid: u32) -> Result<bool, String> {
        let frame = &mut self.stack[top];
        let (mut after, mut ahead) = match i.checked_sub(frame.first).and_then(|at| frame.slots.get(at)) {
            Some(slot) => {
                frame.taken = i - frame.first + 1;
                slot.take(cx.helpers.expect("helpers where steps were queued"))?
            }
            None => (self.world(top)?.step(action)?, None),
        };
        let status = after.status;
        let below = !self.bounds.is_empty()
            && self.bounds.get(&bound::potions(&after.obs.potions)).is_some_and(|&b| bound::reach(&after.obs) < b);
        let exactly = self.graph.is_some() || self.memo.is_some();
        let key = (after.state_key, after.hidden_key);
        // What the step came to, for the graph: a state (found by its key once entered) with its world exactly, and
        // whether a cycle back from it is told as from the graph's state (`Step::same`).
        let pruned = (below && self.graph.is_some()).then(|| (bound::potions(&after.obs.potions), bound::reach(&after.obs)));
        let (entered, to_step, exact, same) = if below {
            self.pruned += 1;
            (false, To::Pruned, 0, true)
        } else if status != Status::InProgress {
            (false, self.ends(cx, &mut after)?, 0, true)
        } else if after.obs.prompt.is_some() {
            let exact = if exactly { after.exact() } else { 0 };
            (self.enter(cx, after, to, None, exact), To::State(0), exact, true)
        } else if action.is_end_turn() || after.round() != self.round || self.crossing {
            (false, self.ends(cx, &mut after)?, 0, true)
        } else {
            let exact = if exactly { after.exact() } else { 0 };
            let same = self.graph.as_ref().is_none_or(|recorder| recorder.same(&after, exact));
            if !self.loops.intercept(&self.path, &after, &self.actions, &self.hashes)? {
                (self.enter(cx, after, None, ahead.take(), exact), To::State(0), exact, same)
            } else {
                (false, To::Cycle, 0, true)
            }
        };
        if let Some(recorder) = &mut self.graph {
            recorder.step(gid, i, to_step, key, exact, same, pruned);
        }
        if let Some(ahead) = ahead {
            ahead.drop_steps();
        }
        Ok(entered)
    }

    /// The state on top of the path has no actions left: off the path, and back to the action that came to it.
    fn leave(&mut self) {
        let frame = self.stack.pop().expect("a state on the path");
        for slot in &frame.slots[frame.taken..] {
            slot.drop_step();
        }
        // A state without a world has no point on the path, and is above all those that do.
        if frame.plays && frame.state.is_some() {
            self.path.pop();
        }
        if !self.stack.is_empty() {
            self.actions.pop();
            self.hashes.pop();
        }
    }

    /// The steps queued for helpers and not taken go, and the search makes them itself when it comes to them.
    fn unslot(&mut self) {
        for frame in &mut self.stack {
            for slot in &frame.slots[frame.taken..] {
                slot.drop_step();
            }
            frame.slots.clear();
            frame.taken = 0;
        }
    }

    /// C#: Ends. The search's line ended here. A state another line already ended at is not asked about again.
    /// What the step came to, for the graph: an end there, by its place among the ends the leaf is asked about.
    fn ends(&mut self, cx: &Cx, state: &mut State) -> Result<To, String> {
        let at = (self.priced.len() + self.ended.len()) as u32;
        if state.status != Status::InProgress {
            let ended = Ended {
                state: self.leaf_state(state),
                actions: self.actions.clone(),
                hashes: self.hashes.clone(),
                key: 0,
                shown: state.hash,
                exact: state.hash,
                bare: 0,
                shape: None,
            };
            self.ended.push(Pending::Ask(ended));
            return Ok(self.arrival(0, state.hash, state.hash, at));
        }
        let hidden = state.hidden_key;
        let key = mix(state.state_key, hidden);
        if !self.keys.insert(key) {
            let to = match &self.graph {
                Some(recorder) => {
                    let first = recorder.ends_by_key[&key];
                    self.arrival(key, state.exact(), state.hash, first)
                }
                None => To::Pruned,
            };
            return Ok(to);
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
            shape: dominance::shape(state),
            state: self.leaf_state(state),
            actions: self.actions.clone(),
            hashes: self.hashes.clone(),
            key,
            shown: state.hash,
            exact: state.exact(),
            bare,
        };
        let to = self.arrival(key, ended.exact, ended.shown, at);
        if let Some(recorder) = &mut self.graph {
            recorder.ends_by_key.insert(key, at);
        }
        self.ended.push(Pending::Ask(ended));
        self.going += 1;
        if self.going == CHUNK {
            self.price(cx.leaf)?;
        }
        Ok(to)
    }

    /// A step to an end, `end` among the ends the leaf is asked about, kept where the search keeps what it goes
    /// through (else nothing is: `To::Pruned` stands for it).
    fn arrival(&mut self, key: u64, exact: u64, shown: u64, end: u32) -> To {
        match &mut self.graph {
            Some(recorder) => {
                recorder.graph.arrivals.push(Arrival { key, exact, shown, end });
                To::End(recorder.graph.arrivals.len() as u32 - 1)
            }
            None => To::Pruned,
        }
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
    fn price(&mut self, leaf: &dyn Leaf) -> Result<(), String> {
        if self.ended.is_empty() {
            return Ok(());
        }
        let states: Vec<&LeafState> = self
            .ended
            .iter()
            .filter_map(|pending| match pending {
                Pending::Ask(ended) => Some(&ended.state),
                Pending::Known(..) => None,
            })
            .collect();
        let mut asked = if states.is_empty() { Vec::new() } else { leaf.outcomes(&states)? }.into_iter();
        for pending in self.ended.drain(..) {
            let (priced, outcome) = match pending {
                Pending::Ask(ended) => (ended.light(), asked.next().expect("an outcome for each end asked about")),
                Pending::Known(priced, outcome) => (priced, outcome),
            };
            self.priced.push(priced);
            self.outcomes.push(outcome);
        }
        self.going = 0;
        Ok(())
    }

    /// C#: Value. The turn's ends, from all the lines the search ended, valued in the order found; those another end
    /// dominates go (`dominance.rs`).
    fn value(&mut self, leaf: &dyn Leaf) {
        // A failure of the leaf would have been met at a chunk already, or is met here.
        if let Err(error) = self.price(leaf) {
            panic!("the leaf failed: {error}");
        }
        self.keep_ends();
        let mut outcomes = std::mem::take(&mut self.outcomes);
        self.thrown(&mut outcomes);
        let shapes: Vec<Option<&Shape>> = self.priced.iter().map(|p| p.shape.as_ref()).collect();
        let dominated = dominance::dominated(&shapes);
        for ((priced, outcome), dominated) in std::mem::take(&mut self.priced).into_iter().zip(outcomes).zip(dominated) {
            if !dominated {
                self.ends.push(priced.valued(outcome));
            }
        }
    }

    /// The ends the leaf has valued go into the graph, where the search keeps one, with the leaf's outcomes before
    /// `thrown`.
    fn keep_ends(&mut self) {
        if let Some(recorder) = &mut self.graph {
            let ends: Vec<Valued> = self
                .priced
                .iter()
                .zip(&self.outcomes)
                .map(|(priced, &outcome)| {
                    let belt = recorder.belt(bound::potions(&priced.potions));
                    Valued::of(priced, outcome, belt)
                })
                .collect();
            recorder.graph.ends = ends;
        }
    }

    /// The nodes stepped in a world: all the nodes but those taken from the memo.
    fn live(&self) -> i32 {
        self.nodes - self.replayed
    }

    /// C#: Thrown. A potion can be thrown away in a fight too, for nothing: each end at the next turn's start takes the
    /// outcome of the best end that differs from it only by holding fewer of its potions, if the leaf values that one
    /// higher.
    fn thrown(&self, outcomes: &mut [LeafOutcome]) {
        let ends: Vec<(u64, &[Option<Str>])> = self.priced.iter().map(|p| (p.bare, p.potions.as_slice())).collect();
        thrown(&ends, outcomes);
    }

    /// C#: Add.
    fn add(&mut self, end: TurnEnd) {
        if end.status == Status::InProgress && !self.keys.insert(end.key) {
            return;
        }
        self.ends.push(end);
    }
}

/// C#: Thrown, over the ends in the order found, each by its key with every potion thrown away (`bare`, 0 once the
/// combat is over) and its belt.
fn thrown(ends: &[(u64, &[Option<Str>])], outcomes: &mut [LeafOutcome]) {
    let mut order: Vec<u64> = Vec::new();
    let mut groups: HashMap<u64, Vec<usize>> = HashMap::new();
    for (i, &(bare, _)) in ends.iter().enumerate() {
        if bare == 0 {
            continue;
        }
        groups
            .entry(bare)
            .or_insert_with(|| {
                order.push(bare);
                Vec::new()
            })
            .push(i);
    }
    let held = |i: usize| ends[i].1.iter().filter(|p| p.is_some()).count();
    for bare in order {
        let mut group = groups.remove(&bare).expect("a group of its own");
        // By how many potions each holds (a stable sort), so that an end's own is final before one holding more is
        // compared with it.
        group.sort_by_key(|&i| held(i));
        for m in 1..group.len() {
            for f in 0..m {
                if outcomes[group[f]].value > outcomes[group[m]].value && holds(ends[group[m]].1, ends[group[f]].1) {
                    outcomes[group[m]] = outcomes[group[f]];
                }
            }
        }
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
