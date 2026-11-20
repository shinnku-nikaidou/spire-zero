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
//! line to the state valued highest, waiting or paused. A line another search found (the beam's, `seed`) is a candidate
//! too, and so is the pick the combat went on along (`playing`), each as far as the combat went its way: a line found
//! from the root ends the combat as it did, so the pick is never worse than either, whatever the states that held it
//! became. When no state is left and no turn's search was cut (a cycle, `loops.rs`, is), the pick is proved: there is no
//! better line under these rules.
//!
//! The combat goes on under it (`advance`): the root moves to where it went, the states that can still be reached stay
//! with all that was found of them, and the rest go. Where it went is a turn's start searched already, or a prompt
//! between a turn's end and the next turn's start, whose answers reach turns' starts the solver holds; elsewhere, the
//! root's turn is searched to its end again, into a turn whose search's graph the solver holds (`turn::Graph`) with it
//! as the search's memo. The root's turn is searched in the runs like any state's, first and in pieces: until its search
//! is done, the pick is the line the combat is playing (the pick it went on along, at the start the seed's), and where
//! the combat goes on into the turn meanwhile, the search of the rest of it takes what the paused one went through as
//! its memo. With no line to play, the root's turn is searched at once, whole. The states live in an arena, by index;
//! what the root no longer reaches is let go once the root's turn is searched (`settle`).
//!
//! The one rule written by hand prunes it too (`bound.rs`): a state that cannot end the combat with the HP plus max HP
//! of a win found with the same potions is dropped, in a turn's search as soon as a line reaches it, and among the states
//! waiting when a win raises its potions' bound. Where a bound falls as the root moves (the win that made it is out of
//! reach), a turn it pruned is to be searched again (`again`): until it is, all it found stays, its ends and the states
//! below it, and its new search takes the place of the old. The states searched at a time (`batch`) are a turn's search
//! each, run apart by a `Searcher`; the search is the same for the same nodes and batch, wherever it runs. A run shares
//! the nodes it is asked for over each batch's states (`PIECE` at the least): a turn with more pauses there
//! (`turn::Turn`) and goes on in the next batch before any state waiting starts, so a run ends near its nodes however
//! big a turn is (one batch of whole turns ran 30 to 100 thousand nodes on a run of 16,384 in big Regent turns).
//!
//! Ported line for line from the C# brain (plans/2026-11-18-2132-sl-search-rust.md, B and C); the C# search was deleted
//! on 2026-11-19 at the user's word (C5), and the `C#:` names below are where each piece came from, in git's history.

use std::cmp::Ordering;
use std::collections::{BinaryHeap, HashMap, HashSet};
use std::sync::{Arc, Mutex};

use super::beam::{self, ByFirst, Found, Job, Rank, Searcher, UNLIMITED};
use super::bound::{self, Bounds};
use super::state::{self, Action, History, Status};
use super::threads::Worlds;
use super::turn::{self, Graph, Memo, Turn, TurnEnd, TurnSearch};

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
    /// Its turn is to be searched again, a bound it was searched under having fallen (`Solver::settle`): it waits as a
    /// state not searched does, and keeps what it found until its new search takes its place (`Solver::expand`).
    again: bool,
    /// Its turn's search, paused where its piece of a run ended (`Solver::run`).
    turn: Option<Box<Turn>>,
    /// What its turn's search went through, where it was big enough to keep (`GRAPH_NODES`).
    graph: Option<Arc<Graph>>,
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
            again: false,
            turn: None,
            graph: None,
        }
    }
}

/// Where the combat went from the root (`Solver::reached`).
enum Reached {
    /// A turn's start whose turn was searched in full already: the state, which becomes the root as it is.
    Searched(usize),
    /// A prompt between a turn's end and the next turn's start, or the prompt the solver began at: the root's turn ends
    /// where the player can play.
    Crossing,
    /// Into the turn of state `.0`: the root's turn is searched with that turn's graph as its memo, where the solver
    /// holds one (`turn::Turn::new`).
    Within(usize),
    /// Anywhere else: the root's turn is searched to its end.
    Turn,
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

/// The root's turn while its search goes on (`Solver::run`): the bounds the states held were searched under, which the
/// new ones are compared with once it is done (`settle`), whether the root is a prompt between a turn's end and the
/// next turn's start, and the memo its search takes, until it begins.
struct Rooting {
    before: Bounds,
    crossing: bool,
    memo: Option<Memo>,
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
    /// The states whose turn's search is paused, in the order paused: they go on before any state waiting starts.
    going: Vec<usize>,
    /// The fewest nodes a turn spends in a batch (`PIECE`).
    piece: i64,
    /// The root's turn, while its search goes on.
    rooting: Option<Rooting>,
    /// The line played while the root's turn is searched, from the root: the pick when the combat went on into it,
    /// along what was played, or at the start the seed's.
    playing: Option<(TurnEnd, f64)>,
    /// The pick's first action when last looked at (`look`), and the nodes when it last changed: how soon an answer
    /// settles, for choosing an early answer at max (plans/2026-11-20-1231-silent-shiv-poison.md, 2.4). A record only:
    /// nothing the search does reads it.
    first: Option<Action>,
    settled: i64,
}

/// How many expanded states' worlds the solver keeps, the latest. On max's route over 45 fights of whole runs (8
/// threads) it took the CPU from 724 s, every path replayed from the root, to 354 s, for 153 MB more at the peak;
/// keeping 4,096 saved a tenth more for 568 MB.
const WORLDS: usize = 1024;

/// The fewest nodes a state's turn spends in one batch of a run before it pauses. A run of N nodes shares N over a
/// batch's states, so that it ends near N however big a turn is (a turn has had 1.9 million nodes); a batch of states
/// is one call of the searcher, whose cost is its threads', so a piece is not made smaller than this.
const PIECE: i64 = 256;

/// The fewest nodes a turn's search has for the solver to keep its graph (`turn::Graph`): a smaller turn is searched
/// again in a few milliseconds, and its graph would only hold memory (on 45 early-act fights, 118 MB more at the peak
/// with every turn's graph kept).
const GRAPH_NODES: usize = 1024;

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
            going: Vec::new(),
            piece: PIECE,
            rooting: None,
            playing: None,
            first: None,
            settled: 0,
        }
    }

    /// The solver with turns paused after `piece` nodes at the least, instead of `PIECE`; `i64::MAX`, never.
    pub fn with_piece(mut self, piece: i64) -> Solver {
        self.piece = piece;
        self
    }

    pub fn nodes(&self) -> i64 {
        self.nodes
    }

    pub fn searched(&self) -> i32 {
        self.searched
    }

    /// The nodes when the pick's first action last changed, looked at after each batch of a run and after each move of
    /// the root.
    pub fn settled(&self) -> i64 {
        self.settled
    }

    fn look(&mut self) {
        let first = self.pick().and_then(|(line, _)| line.actions.first().cloned());
        if first != self.first {
            self.first = first;
            self.settled = self.nodes;
        }
    }

    pub fn states(&self) -> usize {
        self.table.len()
    }

    /// Whether the root's turn is still being searched: the pick is then the line played meanwhile.
    pub fn rooting(&self) -> bool {
        self.rooting.is_some()
    }

    pub fn left(&self) -> usize {
        if self.started { self.queue.len() + self.going.len() + self.rooting.is_some() as usize } else { 1 }
    }

    /// C#: Bounds. By the potions held at a win's end, the most HP plus max HP a win found ends the combat with.
    pub fn bounds(&self) -> &Bounds {
        &self.bounds
    }

    pub fn proved(&self) -> bool {
        self.started && self.rooting.is_none() && self.queue.is_empty() && self.going.is_empty() && self.cuts == 0 && self.best.is_some()
    }

    /// C#: Seed. A line another search found from the root: a candidate for the pick, if it ends the combat, and the
    /// line played until the root's turn is searched (`run`).
    pub fn seed(&mut self, line: TurnEnd, from: f64) {
        self.seed = Some((line, from));
        self.consider_seed();
    }

    fn consider_seed(&mut self) {
        if let Some((line, from)) = self.seed.clone()
            && line.status != Status::InProgress
        {
            self.consider(line, from);
        }
    }

    /// C#: Run. Searches on for at least `nodes` more actions, or until nothing is left: the root's turn first, until
    /// its search is done, then the states it reaches. The nodes left are shared over each batch's states, at least
    /// `PIECE` each: a state's turn that has more pauses there, and goes on in the next batch, in this run or the next,
    /// before any state waiting starts. The root's turn alone pauses at the run's end, while the solver has a line to
    /// play meanwhile (`playing`).
    pub fn run(&mut self, search: &dyn Searcher, nodes: i64) -> Result<(), String> {
        let until = if nodes >= i64::MAX - self.nodes { i64::MAX } else { self.nodes + nodes };
        if !self.started {
            self.playing = self.seed.clone();
            // A solver that begins at a card prompt (one crossed on the node's clone, as Gambling Chip's discard that
            // opens the combat; the user, 2026-11-20: 「ai完全不會用賭博籌碼」) crosses it first: its root's turn is the
            // prompt's answers, to where the player can play again, and each state there waits to be searched as the
            // solver's states do. Every answer is valued, not only the few a turn's search of all of them reaches.
            let reached = if search.root_waits() { Reached::Crossing } else { Reached::Turn };
            self.begin(search, reached, None, None)?;
            self.look();
        }
        while (self.rooting.is_some() || !(self.queue.is_empty() && self.going.is_empty())) && self.nodes < until {
            if self.rooting.is_some() {
                self.root_turn(search, until)?;
                self.look();
                continue;
            }
            let mut states = std::mem::take(&mut self.going);
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
            let piece = ((until - self.nodes) / states.len() as i64).max(self.piece).min(i32::MAX as i64) as i32;
            let jobs: Vec<Job> = states
                .iter()
                .map(|&n| {
                    let turn = self.nodes_of[n].turn.take();
                    let path = if turn.is_some() { Vec::new() } else { self.path(n).0 };
                    Job {
                        from: if turn.is_some() { None } else { self.worlds.nearest(&path) },
                        path,
                        history: self.nodes_of[n].history.clone(),
                        bounds: self.bounds.clone(),
                        crossing: false,
                        most: Some(piece),
                        resume: Mutex::new(turn),
                        record: true,
                        memo: Mutex::new(None),
                    }
                })
                .collect();
            let found = search.search(&jobs, UNLIMITED, usize::MAX)?;
            for ((n, mut found), job) in states.into_iter().zip(found).zip(jobs) {
                if let Some(world) = found.start.take() {
                    self.worlds.keep(job.path, world);
                }
                match found.paused.take() {
                    Some(turn) => {
                        self.nodes += found.nodes as i64;
                        self.nodes_of[n].turn = Some(turn);
                        self.going.push(n);
                    }
                    None => self.expand(n, found),
                }
            }
            if self.raised > self.purged {
                self.purge();
            }
            self.look();
        }
        Ok(())
    }

    /// C#: Purge. The waiting states below their potions' bound go, and the paused ones.
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
        for n in std::mem::take(&mut self.going) {
            let node = &mut self.nodes_of[n];
            if !bound::below(&self.bounds, &node.potions, node.reach) {
                self.going.push(n);
            } else {
                node.turn = None;
                self.table.remove(&node.key);
            }
        }
    }

    /// C#: Advance. The combat went on from the root by `played`, to where its record of enemy moves is `moves`. Where
    /// `played` ends at a turn's start whose turn was searched in full already (`searched_at`), that state becomes the
    /// root as it is, its turn not searched again. Elsewhere the root's turn is searched in the next runs, and the pick
    /// meanwhile is the pick until now, along `played`; where that line went elsewhere or ended, the root's turn is
    /// searched here, by `search`, to its end.
    pub fn advance(&mut self, played: &[Action], moves: Arc<History>, search: &dyn Searcher) -> Result<(), String> {
        if played.is_empty() {
            return Ok(());
        }
        let turns = self.nodes_of[self.root].turns + end_turns(played);
        let playing = self.pick().and_then(|(line, from)| Some((along(line, played)?, from)));
        let reached = self.reached(played);
        // Into a turn whose search the solver holds the graph of, the root's turn is searched with it as its memo;
        // where the root's turn was being searched, with what that search went through so far, or the memo it was to
        // take. The states held were searched under the bounds they had before it.
        let (memo, before) = match self.rooting.take() {
            Some(rooting) => (self.nodes_of[self.root].turn.take().map(Memo::Paused).or(rooting.memo), Some(rooting.before)),
            None => match reached {
                Reached::Within(at) => (self.nodes_of[at].graph.clone().map(Memo::Graph), None),
                _ => (None, None),
            },
        };
        self.order += 1;
        let root = Node::new(0, 0.0, turns, moves, 0, self.order, i32::MAX, String::new(), 0);
        match reached {
            // The root as a search of its turn would make it, with that turn's search.
            Reached::Searched(at) => {
                let node = &mut self.nodes_of[at];
                *node = Node {
                    edges: node.edges.take(),
                    over: node.over.take(),
                    ..root
                };
                self.root = at;
            }
            Reached::Crossing | Reached::Within(..) | Reached::Turn => {
                self.nodes_of.push(root);
                self.root = self.nodes_of.len() - 1;
            }
        }
        self.worlds.advance(played);
        self.seed = self.seed.take().and_then(|(line, from)| Some((along(line, played)?, from)));
        self.playing = playing;
        self.begin(search, reached, memo, before)?;
        self.look();
        Ok(())
    }

    /// Where `played` leads from the root, along the root's edges, each to its state's own world (`exact`).
    ///
    /// To a turn's start whose turn was searched in full and would be searched the same again (`Searched`): its turn
    /// not cut, no line or end of it pruned (so the bounds a search now has prune none either), and every state its
    /// turn reached still kept. Searched again, the root's turn would give these same edges, to these same states.
    ///
    /// Into an edge elsewhere (`Within`): into the turn of node `at`, which the edge leaves from.
    ///
    /// Into an edge where only answers to prompts are left of it (`Crossing`): an edge ends where the player can play
    /// after the turn's end, so the combat stands at a prompt the turn's end or the next turn's start raised (Toasty
    /// Mittens' exhaust, Tools of the Trade's discard, a curse the Knowledge Demon offers), and the states its answers
    /// reach are turns' starts the solver holds already, by their keys.
    fn reached(&self, played: &[Action]) -> Reached {
        let (mut at, mut done) = (self.root, 0);
        while done < played.len() {
            let Some(edges) = self.nodes_of[at].edges.as_ref() else {
                return Reached::Turn;
            };
            let Some(edge) = edges.iter().find(|e| played[done..].starts_with(&e.actions)) else {
                let into: Vec<&Arc<Edge>> = edges.iter().filter(|e| e.actions.len() > played.len() - done && e.actions.starts_with(&played[done..])).collect();
                return if into.iter().any(|e| e.actions[played.len() - done..].iter().all(|a| matches!(a, Action::Pick(_)))) {
                    Reached::Crossing
                } else if !into.is_empty() {
                    Reached::Within(at)
                } else {
                    Reached::Turn
                };
            };
            let Some(&next) = self.table.get(&edge.key) else {
                return Reached::Turn;
            };
            if self.nodes_of[next].exact != edge.exact {
                return Reached::Turn;
            }
            (at, done) = (next, done + edge.actions.len());
        }
        let node = &self.nodes_of[at];
        match node.edges.as_ref() {
            Some(edges) if !node.cut && !node.dropped && !node.again && edges.iter().all(|e| self.table.contains_key(&e.key)) => {
                Reached::Searched(at)
            }
            _ => Reached::Turn,
        }
    }

    /// C#: Result, up to the plan C#'s PlanOf.Found makes of it.
    pub fn result(&self) -> Result<Found, String> {
        if !self.started {
            return Err("the solver has not searched the root's turn".into());
        }
        let (pick, from) = self.pick().ok_or("the solver found no line")?;
        Ok(Found {
            pick,
            from,
            by_first: self.by_first.lines().to_vec(),
            proved: self.proved(),
            nodes: self.nodes.min(i32::MAX as i64) as i32,
            turns: self.searched,
        })
    }

    /// The pick so far, with the value of the turn start a losing pick died from: the line played while the root's turn
    /// is searched (before it begins, the seed's), else the best end of the combat found, else the line to the state
    /// valued highest of those waiting, in the queue or paused (`going`).
    fn pick(&self) -> Option<(TurnEnd, f64)> {
        if !self.started {
            return self.seed.clone();
        }
        if self.rooting.is_some() {
            return self.playing.clone();
        }
        if let Some((line, rank)) = &self.best {
            return Some((line.clone(), rank.from));
        }
        let top = self
            .going
            .iter()
            .map(|&n| Waiting {
                worth: self.worth(n),
                order: self.nodes_of[n].order,
                node: n,
            })
            .chain(self.queue.peek().map(|w| Waiting {
                worth: w.worth,
                order: w.order,
                node: w.node,
            }))
            .max()?;
        let (actions, hashes) = self.path(top.node);
        let node = &self.nodes_of[top.node];
        let mut pick = beam::start(node.history.clone());
        pick.actions = actions;
        pick.hashes = hashes;
        pick.value = node.value;
        pick.key = node.key;
        Some((pick, 0.0))
    }

    /// C#: Start. The root's turn is to be searched again (unless it was searched already, `advance`): in the runs, or
    /// here to its end where the solver has no line to play meanwhile. Once it is, the states it reaches are kept
    /// (`settle`). `memo`: what the root's search takes as its memo; `before`, the bounds the states held were searched
    /// under, where the root's turn was being searched already.
    fn begin(&mut self, search: &dyn Searcher, reached: Reached, memo: Option<Memo>, before: Option<Bounds>) -> Result<(), String> {
        self.started = true;
        let before = before.unwrap_or_else(|| std::mem::take(&mut self.bounds));
        self.bounds = Bounds::new();
        self.best = None;
        self.by_first = ByFirst::default();
        self.consider_seed();
        if matches!(reached, Reached::Searched(_)) {
            self.settle(before);
            return Ok(());
        }
        self.rooting = Some(Rooting {
            before,
            crossing: matches!(reached, Reached::Crossing),
            memo,
        });
        if self.playing.is_none() {
            self.root_turn(search, i64::MAX)?;
        }
        Ok(())
    }

    /// The root's turn searched on: to the nodes up to `until` where the solver has a line to play meanwhile
    /// (`playing`), else to its end; alone in a call of the searcher, at least the nodes of a batch's pieces. Once its
    /// search is done, the states it reaches are kept (`settle`).
    fn root_turn(&mut self, search: &dyn Searcher, until: i64) -> Result<(), String> {
        let root = self.root;
        let rooting = self.rooting.as_mut().expect("the root's turn being searched");
        let least = self.piece.saturating_mul(self.batch as i64);
        let most = self.playing.as_ref().map(|_| (until - self.nodes).max(least).min(i32::MAX as i64) as i32);
        let job = Job {
            path: Vec::new(),
            history: self.nodes_of[root].history.clone(),
            bounds: self.bounds.clone(),
            from: None,
            crossing: rooting.crossing,
            most,
            resume: Mutex::new(self.nodes_of[root].turn.take()),
            record: true,
            memo: Mutex::new(rooting.memo.take()),
        };
        let mut found = search.search(&[job], UNLIMITED, usize::MAX)?.pop().expect("the root's turn");
        match found.paused.take() {
            Some(turn) => {
                self.nodes += found.nodes as i64;
                self.nodes_of[root].turn = Some(turn);
            }
            None => {
                self.expand(root, found);
                let rooting = self.rooting.take().expect("the root's turn being searched");
                self.settle(rooting.before);
            }
        }
        Ok(())
    }

    /// Once the root's turn is searched: keeps the states the root reaches, by the table, and lets the rest go; the
    /// pick's candidates and the bounds made again of the ends they hold and of the pick played along, and where a bound
    /// fell from `before`, a turn it pruned to be searched again.
    fn settle(&mut self, before: Bounds) {
        let root = self.root;
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
        // The paused turns the root still reaches go on, in the same order.
        self.going = std::mem::take(&mut self.going).into_iter().filter_map(|n| moved.get(&n).copied()).collect();
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
        self.consider_seed();
        if let Some((line, from)) = self.playing.clone()
            && line.status != Status::InProgress
        {
            self.consider(line, from);
        }
        for n in 0..count {
            if let Some(over) = self.nodes_of[n].over.clone() {
                let line = self.line(n, &over);
                let from = self.nodes_of[n].value;
                self.consider(line, from);
            }
        }
        // Where a bound fell or went, a turn the pruning took lines or ends from is to be searched again, keeping what
        // it found until then; a paused one goes on from its start.
        if before.iter().any(|(potions, &was)| self.bounds.get(potions).is_none_or(|&now| now < was)) {
            for n in 1..count {
                let node = &self.nodes_of[n];
                if node.edges.as_ref().is_some_and(|edges| node.dropped || edges.iter().any(|e| !self.table.contains_key(&e.key))) {
                    self.nodes_of[n].again = true;
                }
            }
            let nodes = &mut self.nodes_of;
            self.going.retain(|&n| {
                let pruned = nodes[n].turn.as_ref().is_some_and(|turn| turn.pruned() > 0);
                if pruned {
                    nodes[n].turn = None;
                }
                !pruned
            });
        }
        for n in 0..count {
            // A turn to be searched again is cut or not as its new search is.
            let node = &self.nodes_of[n];
            if node.cut && !node.again {
                self.cuts += 1;
            }
            if (node.edges.is_none() || node.again) && node.turn.is_none() {
                let waiting = Waiting {
                    worth: self.worth(n),
                    order: self.nodes_of[n].order,
                    node: n,
                };
                self.queue.push(waiting);
            }
        }
        self.purge();
    }

    /// C#: Expand. A state's turn, as searched; searched again (`Node::again`), in the place of what it found before.
    fn expand(&mut self, n: usize, found: TurnSearch) {
        self.nodes += found.nodes as i64;
        self.searched += 1;
        {
            let node = &mut self.nodes_of[n];
            node.again = false;
            node.over = None;
            node.edges = Some(Vec::new());
            node.cut = found.cut;
            node.dropped = found.pruned > 0;
            node.graph = found.graph.filter(|graph| graph.nodes() >= GRAPH_NODES);
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

/// `line` after its first actions `played`, where it goes on past them.
fn along(line: TurnEnd, played: &[Action]) -> Option<TurnEnd> {
    (line.actions.len() > played.len() && line.actions.starts_with(played)).then(|| TurnEnd {
        actions: line.actions[played.len()..].to_vec(),
        hashes: line.hashes[played.len()..].to_vec(),
        ..line
    })
}
