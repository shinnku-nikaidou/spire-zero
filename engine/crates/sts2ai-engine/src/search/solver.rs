//! The SL planner as a solver (plans/2026-11-18-1412-max-solver.md): the rest of the combat searched with no state
//! dropped, a little at a time (`run`), so that its best line so far can be played whenever it is asked for, and only
//! gets better the longer it searches.
//!
//! Its states are the turns' starts. Each is searched to the end of its turn with no limit (`UNLIMITED`): every state of
//! the turn, every answer of every prompt, every end kept. The ends are new states, and none is dropped: the combat net
//! only orders them, the state it values highest (by worth, as the pick compares lines) searched next, the rest waiting
//! their turn. One table over the combat holds every state reached, by its key: the observation and the full hidden
//! state, and the record of enemy moves, which the leaf reads. A state is searched once however many lines reach it. The
//! states that wait are most of them, so a record of enemy moves is held once, however many states share it: the ends of
//! a turn mostly do. A state keeps its own line, or takes another line to its key only where that line's end is the same
//! world (`exact`: the observation, and every card of the player's piles by its id in its place): a key is the same
//! cards whichever copy is where, and the observation shows a card's uid in hand only, so a line to the same key and
//! observation can hold the copies in the draw pile otherwise, and the turns searched below the state would then replay
//! a card not in hand.
//!
//! The pick is the beam's (`beam.rs`): the best end of the combat by worth, wins worth the same by fewer potions drunk and
//! then fewer turns, and losses, all 0, by the value of the turn start they died from. Until a line ends the combat, the
//! line to the state valued highest. A line another search found (the beam's, `seed`) is a candidate too, so the
//! solver's pick is never worse than it. When no state is left and no turn's search was cut (a cycle, `loops.rs`, is),
//! the pick is proved: there is no better line under these rules.
//!
//! The combat goes on under it (`advance`): the root moves to where it went, the states that can still be reached stay
//! with all that was found of them, and the rest go. The states live in an arena, by index; what the root no longer
//! reaches is let go when the root moves (`start`).
//!
//! The one rule written by hand prunes it too (`bound.rs`): a state that cannot end the combat with the HP plus max HP
//! of a win found with the same potions is dropped, in a turn's search as soon as a line reaches it, and among the states
//! waiting when a win raises its potions' bound. The states searched at a time (`batch`) are a turn's search each, run
//! apart by a `Searcher`; the search is the same for the same nodes and batch, wherever it runs.
//!
//! Ported line for line from the C# brain (plans/2026-11-18-2132-sl-search-rust.md, B and C); the C# search was deleted
//! on 2026-11-19 at the user's word (C5), and the `C#:` names below are where each piece came from, in git's history.

use std::cmp::Ordering;
use std::collections::{BinaryHeap, HashMap, HashSet};
use std::sync::Arc;

use super::beam::{self, ByFirst, Found, Job, Rank, Searcher, UNLIMITED};
use super::bound::{self, Bounds};
use super::state::{self, Action, History, Status};
use super::threads::Worlds;
use super::turn::{self, TurnEnd, TurnSearch};

/// C#: SlSolver.Edge. The actions from a state to where its turn ends, the hash of each one's observation, the key of the
/// state there and the world there exactly (`State::exact`). Told apart by identity, as C# tells its records with their
/// own lists.
struct Edge {
    actions: Vec<Action>,
    hashes: Vec<u64>,
    key: u64,
    exact: u64,
}

/// C#: SlSolver.Node. A state the solver reached: a turn's start, or the root.
struct Node {
    key: u64,
    /// The world at the state exactly, as its own line reaches it.
    exact: u64,
    reach: i32,
    potions: String,
    value: f64,
    turns: i32,
    history: Arc<History>,
    moves: u64,
    order: i64,
    parent: Option<usize>,
    from: Option<Arc<Edge>>,
    edges: Option<Vec<Arc<Edge>>>,
    over: Option<TurnEnd>,
    cut: bool,
    dropped: bool,
}

impl Node {
    fn new(
        key: u64, value: f64, turns: i32, history: Arc<History>, moves: u64, order: i64, reach: i32, potions: String, exact: u64,
    ) -> Node {
        Node {
            key,
            exact,
            reach,
            potions,
            value,
            turns,
            history,
            moves,
            order,
            parent: None,
            from: None,
            edges: None,
            over: None,
            cut: false,
            dropped: false,
        }
    }
}

/// A state waiting in the queue: the least (-worth, order) first (C#: PriorityQueue by (-Worth, Order)).
struct Waiting {
    worth: f64,
    order: i64,
    node: usize,
}

impl PartialEq for Waiting {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}

impl Eq for Waiting {}

impl PartialOrd for Waiting {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for Waiting {
    /// The heap's greatest is the one searched next: the least (-worth, order).
    fn cmp(&self, other: &Self) -> Ordering {
        beam::compare(-self.worth, -other.worth).then(self.order.cmp(&other.order)).reverse()
    }
}

/// C#: SlSolver.
pub struct Solver {
    decay: f64,
    batch: usize,
    nodes_of: Vec<Node>,
    table: HashMap<u64, usize>,
    /// The records of enemy moves the states hold, each once, by hash.
    moves: HashMap<u64, Arc<History>>,
    queue: BinaryHeap<Waiting>,
    root: usize,
    order: i64,
    nodes: i64,
    searched: i32,
    cuts: i32,
    best: Option<(TurnEnd, Rank)>,
    seed: Option<(TurnEnd, f64)>,
    by_first: ByFirst,
    started: bool,
    bounds: Bounds,
    raised: i32,
    purged: i32,
    /// The worlds of the states expanded last, by their paths from the root: a state's turn starts from the nearest one
    /// on its path instead of replaying the path from the root.
    worlds: Worlds,
}

/// How many expanded states' worlds the solver keeps, the latest. On max's route over 45 fights of whole runs (8
/// threads) it took the CPU from 724 s, every path replayed from the root, to 354 s, for 153 MB more at the peak;
/// keeping 4,096 saved a tenth more for 568 MB.
const WORLDS: usize = 1024;

fn end_turns(actions: &[Action]) -> i32 {
    actions.iter().filter(|a| a.is_end_turn()).count() as i32
}

impl Solver {
    pub fn new(history: Arc<History>, decay: f64, batch: usize) -> Solver {
        Solver {
            decay,
            batch,
            nodes_of: vec![Node::new(0, 0.0, 0, history, 0, 0, i32::MAX, String::new(), 0)],
            table: HashMap::new(),
            moves: HashMap::new(),
            queue: BinaryHeap::new(),
            root: 0,
            order: 0,
            nodes: 0,
            searched: 0,
            cuts: 0,
            best: None,
            seed: None,
            by_first: ByFirst::default(),
            started: false,
            bounds: Bounds::new(),
            raised: 0,
            purged: 0,
            worlds: Worlds::new(WORLDS),
        }
    }

    pub fn nodes(&self) -> i64 {
        self.nodes
    }

    pub fn searched(&self) -> i32 {
        self.searched
    }

    pub fn states(&self) -> usize {
        self.table.len()
    }

    pub fn left(&self) -> usize {
        if self.started { self.queue.len() } else { 1 }
    }

    /// C#: Bounds. By the potions held at a win's end, the most HP plus max HP a win found ends the combat with.
    pub fn bounds(&self) -> &Bounds {
        &self.bounds
    }

    pub fn proved(&self) -> bool {
        self.started && self.queue.is_empty() && self.cuts == 0 && self.best.is_some()
    }

    /// C#: Seed. A line another search found from the root: a candidate for the pick, if it ends the combat.
    pub fn seed(&mut self, line: TurnEnd, from: f64) {
        if line.status == Status::InProgress {
            return;
        }
        self.seed = Some((line.clone(), from));
        self.consider(line, from);
    }

    /// C#: Run. Searches on for at least `nodes` more actions, or until nothing is left.
    pub fn run(&mut self, search: &dyn Searcher, nodes: i64) -> Result<(), String> {
        if !self.started {
            self.start(search)?;
        }
        let until = if nodes >= i64::MAX - self.nodes { i64::MAX } else { self.nodes + nodes };
        while !self.queue.is_empty() && self.nodes < until {
            let mut states = Vec::new();
            while states.len() < self.batch {
                let Some(waiting) = self.queue.pop() else {
                    break;
                };
                let node = &self.nodes_of[waiting.node];
                if !bound::below(&self.bounds, &node.potions, node.reach) {
                    states.push(waiting.node);
                } else {
                    self.table.remove(&node.key);
                }
            }
            if states.is_empty() {
                continue;
            }
            let jobs: Vec<Job> = states
                .iter()
                .map(|&n| {
                    let path = self.path(n).0;
                    Job {
                        from: self.worlds.nearest(&path),
                        path,
                        history: self.nodes_of[n].history.clone(),
                        bounds: self.bounds.clone(),
                    }
                })
                .collect();
            let found = search.search(&jobs, UNLIMITED, usize::MAX)?;
            for ((n, mut found), job) in states.into_iter().zip(found).zip(jobs) {
                if let Some(world) = found.start.take() {
                    self.worlds.keep(job.path, world);
                }
                self.expand(n, found);
            }
            if self.raised > self.purged {
                self.purge();
            }
        }
        Ok(())
    }

    /// C#: Purge. The waiting states below their potions' bound go.
    fn purge(&mut self) {
        self.purged = self.raised;
        let left = std::mem::take(&mut self.queue).into_vec();
        for waiting in left {
            let node = &self.nodes_of[waiting.node];
            if !bound::below(&self.bounds, &node.potions, node.reach) {
                self.queue.push(waiting);
            } else {
                self.table.remove(&node.key);
            }
        }
    }

    /// C#: Advance. The combat went on from the root by `played`, to where its record of enemy moves is `moves`; `search`
    /// searches from there.
    pub fn advance(&mut self, played: &[Action], moves: Arc<History>, search: &dyn Searcher) -> Result<(), String> {
        if played.is_empty() {
            return Ok(());
        }
        let turns = self.nodes_of[self.root].turns + end_turns(played);
        self.order += 1;
        self.nodes_of.push(Node::new(0, 0.0, turns, moves, 0, self.order, i32::MAX, String::new(), 0));
        self.root = self.nodes_of.len() - 1;
        self.worlds.advance(played);
        if let Some((line, from)) = self.seed.take() {
            self.seed = if line.actions.len() > played.len() && line.actions[..played.len()] == *played {
                Some((
                    TurnEnd {
                        actions: line.actions[played.len()..].to_vec(),
                        hashes: line.hashes[played.len()..].to_vec(),
                        ..line
                    },
                    from,
                ))
            } else {
                None
            };
        }
        self.start(search)
    }

    /// C#: Result, up to the plan C#'s PlanOf.Found makes of it.
    pub fn result(&self) -> Result<Found, String> {
        if !self.started {
            return Err("the solver has not searched the root's turn".into());
        }
        let (pick, from) = if let Some((line, rank)) = &self.best {
            (line.clone(), rank.from)
        } else if let Some(top) = self.queue.peek() {
            let (actions, hashes) = self.path(top.node);
            let node = &self.nodes_of[top.node];
            let mut pick = beam::start(node.history.clone());
            pick.actions = actions;
            pick.hashes = hashes;
            pick.value = node.value;
            pick.key = node.key;
            (pick, 0.0)
        } else {
            return Err("the solver found no line".into());
        };
        Ok(Found {
            pick,
            from,
            by_first: self.by_first.lines().to_vec(),
            proved: self.proved(),
            nodes: self.nodes.min(i32::MAX as i64) as i32,
            turns: self.searched,
        })
    }

    /// C#: Start. Searches the root's turn, then keeps the states it reaches, by the table, and lets the rest go.
    fn start(&mut self, search: &dyn Searcher) -> Result<(), String> {
        self.started = true;
        let before = std::mem::take(&mut self.bounds);
        self.best = None;
        self.by_first = ByFirst::default();
        if let Some((line, from)) = self.seed.clone() {
            self.consider(line, from);
        }
        let root = self.root;
        let job = Job {
            path: Vec::new(),
            history: self.nodes_of[root].history.clone(),
            bounds: self.bounds.clone(),
            from: None,
        };
        let found = search.search(&[job], UNLIMITED, usize::MAX)?.pop().expect("the root's turn");
        self.expand(root, found);
        // What the root reaches, each state on its own line or one to the same world, in the order reached.
        let mut kept: HashMap<u64, usize> = HashMap::new();
        let mut order = vec![root];
        let mut reached: HashSet<usize> = HashSet::from([root]);
        let mut i = 0;
        while i < order.len() {
            let edges: Vec<Arc<Edge>> = self.nodes_of[order[i]].edges.clone().unwrap_or_default();
            for edge in edges {
                if kept.contains_key(&edge.key) {
                    continue;
                }
                let Some(&found) = self.table.get(&edge.key) else {
                    continue;
                };
                let mut next = found;
                let own = {
                    let n = &self.nodes_of[next];
                    match (n.parent, &n.from) {
                        (Some(parent), Some(from)) => {
                            reached.contains(&parent)
                                && self.nodes_of[parent].edges.as_ref().is_some_and(|edges| edges.iter().any(|e| Arc::ptr_eq(e, from)))
                        }
                        _ => false,
                    }
                };
                if !own && self.nodes_of[next].exact != edge.exact {
                    let n = &self.nodes_of[next];
                    self.order += 1;
                    let fresh = Node::new(n.key, n.value, n.turns, n.history.clone(), n.moves, self.order, n.reach, n.potions.clone(), edge.exact);
                    self.nodes_of.push(fresh);
                    next = self.nodes_of.len() - 1;
                }
                if !own {
                    self.nodes_of[next].parent = Some(order[i]);
                    self.nodes_of[next].from = Some(edge.clone());
                }
                kept.insert(edge.key, next);
                order.push(next);
                reached.insert(next);
            }
            i += 1;
        }
        // The arena holds what the root reaches, in the order reached; the rest goes.
        let mut moved: HashMap<usize, usize> = HashMap::with_capacity(order.len());
        for (at, &n) in order.iter().enumerate() {
            moved.insert(n, at);
        }
        let mut arena: Vec<Option<Node>> = std::mem::take(&mut self.nodes_of).into_iter().map(Some).collect();
        self.nodes_of = order
            .iter()
            .map(|&n| {
                let mut node = arena[n].take().expect("a state once");
                node.parent = node.parent.map(|p| moved[&p]);
                node
            })
            .collect();
        drop(arena);
        self.root = 0;
        self.table = kept.iter().map(|(&key, n)| (key, moved[n])).collect();
        let count = self.nodes_of.len();
        self.moves = HashMap::new();
        for n in 1..count {
            let node = &self.nodes_of[n];
            self.moves.entry(node.moves).or_insert_with(|| node.history.clone());
        }
        self.queue.clear();
        self.cuts = 0;
        self.best = None;
        self.by_first = ByFirst::default();
        self.bounds = Bounds::new();
        if let Some((line, from)) = self.seed.clone() {
            self.consider(line, from);
        }
        for n in 0..count {
            if let Some(over) = self.nodes_of[n].over.clone() {
                let line = self.line(n, &over);
                let from = self.nodes_of[n].value;
                self.consider(line, from);
            }
        }
        // Where a bound fell or went, a turn the pruning took lines or ends from is searched again.
        if before.iter().any(|(potions, &was)| self.bounds.get(potions).is_none_or(|&now| now < was)) {
            for n in 1..count {
                let node = &self.nodes_of[n];
                let again = node.edges.as_ref().is_some_and(|edges| node.dropped || edges.iter().any(|e| !self.table.contains_key(&e.key)));
                if again {
                    let node = &mut self.nodes_of[n];
                    node.edges = None;
                    node.over = None;
                    node.cut = false;
                    node.dropped = false;
                }
            }
        }
        for n in 0..count {
            if self.nodes_of[n].cut {
                self.cuts += 1;
            }
            if self.nodes_of[n].edges.is_none() {
                let waiting = Waiting {
                    worth: self.worth(n),
                    order: self.nodes_of[n].order,
                    node: n,
                };
                self.queue.push(waiting);
            }
        }
        self.purge();
        Ok(())
    }

    /// C#: Expand. A state's turn, as searched.
    fn expand(&mut self, n: usize, found: TurnSearch) {
        self.nodes += found.nodes as i64;
        self.searched += 1;
        {
            let node = &mut self.nodes_of[n];
            node.edges = Some(Vec::new());
            node.cut = found.cut;
            node.dropped = found.pruned > 0;
        }
        if found.cut {
            self.cuts += 1;
        }
        for end in found.ends {
            if end.status != Status::InProgress {
                let line = self.line(n, &end);
                let from = self.nodes_of[n].value;
                self.nodes_of[n].over = Some(end);
                self.consider(line, from);
                continue;
            }
            if bound::below(&self.bounds, &end.potions, end.reach) {
                self.nodes_of[n].dropped = true;
                continue;
            }
            let moves = state::history_hash(&end.history);
            let edge = Arc::new(Edge {
                actions: end.actions.clone(),
                hashes: end.hashes.clone(),
                key: turn::mix(end.key, moves),
                exact: end.exact,
            });
            self.nodes_of[n].edges.as_mut().expect("just set").push(edge.clone());
            if self.table.contains_key(&edge.key) {
                continue;
            }
            let shared = self.moves.entry(moves).or_insert_with(|| end.history.clone()).clone();
            self.order += 1;
            let turns = self.nodes_of[n].turns + end_turns(&end.actions);
            let mut next = Node::new(edge.key, end.value, turns, shared, moves, self.order, end.reach, end.potions, end.exact);
            next.parent = Some(n);
            next.from = Some(edge.clone());
            self.nodes_of.push(next);
            let at = self.nodes_of.len() - 1;
            self.table.insert(edge.key, at);
            let waiting = Waiting {
                worth: self.worth(at),
                order: self.nodes_of[at].order,
                node: at,
            };
            self.queue.push(waiting);
        }
    }

    /// C#: Consider.
    fn consider(&mut self, line: TurnEnd, from: f64) {
        let rank = Rank::of(&line, self.decay, from);
        if bound::note(&mut self.bounds, line.status == Status::Won, &line.potions, line.life) {
            self.raised += 1;
        }
        let better = match self.by_first.get(&line.actions[0]) {
            None => true,
            Some(first) => rank.worth > Rank::of(first, self.decay, 0.0).worth,
        };
        if better {
            self.by_first.set(line.clone());
        }
        if self.best.as_ref().is_none_or(|(_, best)| rank.beats(best)) {
            self.best = Some((line, rank));
        }
    }

    fn worth(&self, n: usize) -> f64 {
        let node = &self.nodes_of[n];
        node.value * beam::pow(self.decay, node.turns as f64)
    }

    /// C#: Line. The line from the root through state `n` to an end of its turn.
    fn line(&self, n: usize, end: &TurnEnd) -> TurnEnd {
        let (mut actions, mut hashes) = self.path(n);
        actions.extend_from_slice(&end.actions);
        hashes.extend_from_slice(&end.hashes);
        TurnEnd {
            actions,
            hashes,
            ..end.clone()
        }
    }

    /// C#: Path. The actions from the root to a state, and the hash of each one's observation.
    fn path(&self, n: usize) -> (Vec<Action>, Vec<u64>) {
        let mut edges = Vec::new();
        let mut at = n;
        while let Some(parent) = self.nodes_of[at].parent {
            edges.push(self.nodes_of[at].from.clone().expect("a state with a parent has its edge"));
            at = parent;
        }
        edges.reverse();
        let actions = edges.iter().flat_map(|e| e.actions.iter().cloned()).collect();
        let hashes = edges.iter().flat_map(|e| e.hashes.iter().copied()).collect();
        (actions, hashes)
    }
}
