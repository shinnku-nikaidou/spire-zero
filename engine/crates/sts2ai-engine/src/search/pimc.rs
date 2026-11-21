//! NoSL's search (C#: PimcBrain, plans/2026-11-19-0030-nosl-search-rust.md): PIMC over K worlds, each the real combat
//! with everything the player cannot see drawn again (`determinize`, plans/2026-11-17-1120-nosl-dread-architecture.md,
//! option c). In each world, a depth-first search over this turn's action sequences, with ending the turn an option at
//! every step, one action of each that are the same (`order::ordered`); states are merged by the state key and the
//! world's hidden key, so each transposition is expanded once, and two lines that look the same but drew or rolled
//! differently are not. A line ends when the turn ends (after the enemies' turn) or the combat does, and is valued by
//! the leaf (the combat net, its endings priced by V). Each first action's line is its best in each world; the caller
//! averages them over the worlds (never the max across worlds, which is strategy fusion) and plays only the first
//! action, then asks again.
//!
//! A card prompt inside a world is a state, and its answers are its actions (`order::answers`). A prompt the combat
//! itself waits on is a decision like any other (plans/2026-11-21-1213-nosl-prompt-worlds.md): the root stands at it,
//! each world is the real combat drawn again where the prompt takes its answer (world.rs, `Live::draw_at_prompt`), and
//! the first actions are the prompt's answers.
//!
//! Each world's nodes go to the first actions evenly, so each gets a fair look; inside a subtree the search stops
//! expanding when its share is spent, and what is left is valued as ending the turn there (that option is tried first).
//! A state cut short so is not kept for its transpositions: its line is only a lower bound, and another first action
//! reaching it searches it with its own share.
//!
//! Where the search goes depends only on the nodes and the shares, never on what a line is worth, so a world's search
//! is first walked to its ends and the ends are valued after it: a line found is kept as what it is made of until then
//! (`Found`), and the best of each state's lines is taken once the values are known, the first of the best as C#'s
//! `>` took it. An end the world reaches twice with the same observation and run is valued once: the leaf reads
//! nothing else. The ends are valued in the order found, `leaf::BATCH` to an evaluation of the net, which is far cheaper
//! a state than one each (the user, 2026-11-19: 全做啊). A batch pads every state to its widest, so a value can differ
//! in its last bits from the one it gets alone (C#'s search asked one state at a time, and this one did too until
//! then): a decision between two first actions worth the same to about 1e-7 can go the other way. The batches follow
//! from the world alone, so a decision is still the same on any number of threads. The cycles a world finds are cashed
//! out as they are found, their ends valued at once, one at a time (`Loops`): how far they are repeated hangs on the
//! values, and the search's walk on how far they went.

use std::collections::HashMap;
use std::panic::{self, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use super::ffi;
use super::leaf::{Leaf, LeafOutcome, LeafState};
use super::loops::{LoopPoint, Loops};
use super::order;
use super::state::{Action, History, State, Status};
use super::turn::TurnEnd;
use crate::ffi::World as _;
use crate::protocol::{self, CardChoice, Fnv};
use crate::world::Live;

/// C#: PlannedLine. A first action's best line in one world: what it is worth, its actions from the decision, the hash
/// of the observation each is played at, and whether it repeats a cycle.
#[derive(Clone, Debug, PartialEq)]
pub struct Line {
    pub value: f64,
    pub actions: Vec<Action>,
    pub hashes: Vec<u64>,
    pub repeats: bool,
}

/// A decision's search, as the C# side asks it: the record of enemy moves up to it, the first actions in the order
/// they are tried (C#: PimcBrain.Ordered of the legal actions), each world's nodes for each of them, how many worlds,
/// and the decision's seed, which draws them.
pub struct Asked {
    pub history: History,
    pub first: Vec<Action>,
    pub nodes_per_first: i32,
    pub worlds: usize,
    pub seed: u64,
}

/// C#: EnginePimc.WorldSeed. World `k`'s seed of a decision's: FNV-1a of "SEED:K", the same in every process.
pub fn world_seed(seed: u64, k: usize) -> u64 {
    let mut hash: u64 = 14695981039346656037;
    for b in format!("{seed}:{k}").bytes() {
        hash = (hash ^ b as u64).wrapping_mul(1099511628211);
    }
    hash
}

/// C#: FirstPicks.Of. A prompt's first options in its canonical order, as few as it takes.
fn first_picks(prompt: &CardChoice) -> Action {
    Action::pick((0..prompt.min.min(prompt.options.len() as i32)).collect())
}

/// The decision's search: each world drawn from `root` (the real combat, between the player's actions or at a card
/// prompt it waits on) and searched in `threads` threads, the leaf of each made by `leaf_of`. The lines by world, in
/// world order, then by first action, in `asked.first`'s order: the same on any number of threads. A world that fails
/// fails the search, the first in world order.
pub fn search<'a, L: Leaf>(root: &Live, asked: &Asked, threads: usize, leaf_of: &(dyn Fn() -> L + Sync + 'a)) -> Result<Vec<Vec<Line>>, String> {
    let history = Arc::new(asked.history.clone());
    let results: Vec<WorldResult> = (0..asked.worlds).map(|_| Mutex::new(None)).collect();
    let next = AtomicUsize::new(0);
    let failed = AtomicBool::new(false);
    let count = threads.clamp(1, asked.worlds.max(1));
    // Each thread its own copy of the root: a world is one thread's at a time.
    let roots: Vec<Live> = (0..count).map(|_| root.clone()).collect();
    std::thread::scope(|scope| {
        for mine in roots {
            let (results, next, failed, history) = (&results, &next, &failed, &history);
            scope.spawn(move || {
                let leaf = leaf_of();
                loop {
                    let k = next.fetch_add(1, Ordering::Relaxed);
                    if k >= asked.worlds || failed.load(Ordering::Relaxed) {
                        break;
                    }
                    let lines = panic::catch_unwind(AssertUnwindSafe(|| {
                        let mut world = mine.clone();
                        let seed = world_seed(asked.seed, k);
                        if world.asked().is_some() {
                            world.draw_at_prompt(seed)?;
                        } else {
                            crate::determinize::determinize(world.combat(), seed)?;
                        }
                        world_lines(State::new(world, true), asked, history, &leaf)
                    }))
                    .unwrap_or_else(|payload| Err(ffi::panic_text(payload)));
                    if lines.is_err() {
                        failed.store(true, Ordering::Relaxed);
                    }
                    *results[k].lock().expect("a world's lines") = Some(lines);
                }
            });
        }
    });
    let mut worlds = Vec::with_capacity(asked.worlds);
    for result in results {
        match result.into_inner().expect("a world's lines") {
            Some(Ok(lines)) => worlds.push(lines),
            Some(Err(error)) => return Err(error),
            // A world not taken because another failed first.
            None => continue,
        }
    }
    if worlds.len() < asked.worlds {
        return Err("a world's search failed".into());
    }
    Ok(worlds)
}

/// A world's lines once its search is done; none for a world not searched because another failed first.
type WorldResult = Mutex<Option<Result<Vec<Line>, String>>>;

/// C#: PimcBrain.SearchWorld. One world's part of a decision: the best line after each first action, in their order.
pub fn world_lines(root: State, asked: &Asked, history: &Arc<History>, leaf: &dyn Leaf) -> Result<Vec<Line>, String> {
    let round = root.round();
    let mut search = WorldSearch {
        round,
        leaf,
        history: history.clone(),
        found: vec![Found::Cycle],
        memo: HashMap::new(),
        ends: Vec::new(),
        slots: HashMap::new(),
        path: Vec::new(),
        actions: Vec::new(),
        hashes: Vec::new(),
        loops: Loops::new(round),
        nodes: 0,
        limit: 0,
        cuts: 0,
    };
    let firsts = search.root(&root, &asked.first, asked.nodes_per_first)?;
    let outcomes = search.valued()?;
    let lines = search.resolved(&outcomes);
    let mut out = Vec::with_capacity(firsts.len());
    for first in firsts {
        let after = &lines[first.after];
        let mut line = Line {
            value: after.value,
            actions: std::iter::once(first.action.clone()).chain(after.actions.iter().cloned()).collect(),
            hashes: std::iter::once(root.hash).chain(after.hashes.iter().copied()).collect(),
            repeats: false,
        };
        for end in first.cycles {
            if end.value > line.value {
                line = Line {
                    value: end.value,
                    actions: end.actions,
                    hashes: end.hashes,
                    repeats: true,
                };
            }
        }
        if line.value != f64::NEG_INFINITY {
            out.push(line);
            continue;
        }
        // The move was a cycle at once and no budget was left for it: what is left is ending the turn, with the first
        // answer to any prompt on the way.
        let mut world = root.copy();
        let mut actions = Vec::new();
        let mut hashes = Vec::new();
        let mut play = |world: &mut State, next: Action| -> Result<(), String> {
            let mut step = Some(next);
            while let Some(taken) = step {
                hashes.push(world.hash);
                *world = world.step(&taken)?;
                actions.push(taken);
                step = match (&world.obs.prompt, world.status) {
                    (Some(prompt), Status::InProgress) => Some(first_picks(prompt)),
                    _ => None,
                };
            }
            Ok(())
        };
        play(&mut world, first.action.clone())?;
        if world.status == Status::InProgress && world.round() == round {
            play(&mut world, Action::EndTurn)?;
        }
        let value = leaf.outcome(&leaf_state(history, &mut world))?.value;
        out.push(Line {
            value,
            actions,
            hashes,
            repeats: false,
        });
    }
    Ok(out)
}

/// What the leaf is asked about a state: as it stands, with the decision's record of enemy moves.
fn leaf_state(history: &Arc<History>, state: &mut State) -> LeafState {
    LeafState {
        status: state.status,
        obs: state.obs.clone(),
        run: state.run(),
        history: history.clone(),
    }
}

/// A line found, kept as what it is made of until the world's ends are valued.
enum Found {
    /// The line ends where the turn or the combat ended: the value of the world's end `slot`.
    End(usize),
    /// A move back to a state on the path with nothing gained, or a cycle set aside for `Loops`: no line.
    Cycle,
    /// The best of the lines after each action tried at a state, in the order tried; `hash` is the state's
    /// observation's.
    Best { hash: u64, tried: Vec<(Action, usize)> },
}

/// A first action's search: the line after it, and the cycles' lines it found, valued.
struct First {
    action: Action,
    after: usize,
    cycles: Vec<TurnEnd>,
}

/// C#: PimcBrain's WorldSearch, the walk.
struct WorldSearch<'a> {
    round: i32,
    leaf: &'a dyn Leaf,
    history: Arc<History>,
    /// Every line found; one found below another comes before it. The first is the cycle.
    found: Vec<Found>,
    /// The line of each state searched in full, by its state key and hidden key.
    memo: HashMap<(u64, u64), usize>,
    /// The distinct ends, in the order found, and each one's slot by its status, observation and run.
    ends: Vec<LeafState>,
    slots: HashMap<(Status, u64, u64), usize>,
    path: Vec<LoopPoint>,
    actions: Vec<Action>,
    hashes: Vec<u64>,
    loops: Loops,
    /// The nodes spent on the first action so far, and where the line being searched must stop.
    nodes: i32,
    limit: i32,
    /// States cut short by the budget so far: a state whose subtree has none is searched in full.
    cuts: i32,
}

/// The cycle's line, `found[0]`.
const CYCLE: usize = 0;

impl WorldSearch<'_> {
    /// C#: Root. Each first action searched from the root with its own share of the nodes, its cycles then cashed out.
    fn root(&mut self, root: &State, first: &[Action], nodes_per_first: i32) -> Result<Vec<First>, String> {
        self.path.push(LoopPoint {
            obs: root.obs.clone(),
            depth: 0,
            state: root.copy(),
        });
        let mut firsts = Vec::with_capacity(first.len());
        for action in first {
            (self.nodes, self.limit) = (0, nodes_per_first);
            let mut at = root.copy();
            let after = self.after(&mut at, action, root.hash, Some(nodes_per_first))?;
            let leaf = self.leaf;
            let history = self.history.clone();
            let cycles = self.loops.run_found(&|state: &mut State, actions: &[Action], hashes: &[u64]| {
                let outcome = leaf.outcome(&leaf_state(&history, state))?;
                Ok(TurnEnd {
                    actions: actions.to_vec(),
                    hashes: hashes.to_vec(),
                    status: state.status,
                    value: outcome.value,
                    key: 0,
                    history: history.clone(),
                    win: outcome.win,
                    hp: outcome.hp,
                    life: 0,
                    reach: 0,
                    potions: String::new(),
                    shown: state.hash,
                    exact: 0,
                })
            })?;
            firsts.push(First {
                action: action.clone(),
                after,
                cycles,
            });
        }
        Ok(firsts)
    }

    /// C#: After. Plays `action` from `state` and finds the line from there. `share_to`: where the action's share of
    /// the nodes ends, if a prompt it raises is to share it.
    fn after(&mut self, state: &mut State, action: &Action, hash: u64, share_to: Option<i32>) -> Result<usize, String> {
        self.nodes += 1;
        let mut after = state.step(action)?;
        self.actions.push(action.clone());
        self.hashes.push(hash);
        let found = self.reached(&mut after, action, share_to);
        self.actions.pop();
        self.hashes.pop();
        found
    }

    /// C#: Reached. A line ends with the combat, or where the player can play again after the turn's end; a prompt on
    /// the way is answered first.
    fn reached(&mut self, state: &mut State, action: &Action, share_to: Option<i32>) -> Result<usize, String> {
        if state.status == Status::InProgress && state.obs.prompt.is_some() {
            return self.best(state, share_to);
        }
        if state.status != Status::InProgress || action.is_end_turn() || state.round() != self.round {
            return Ok(self.end(state));
        }
        if self.loops.intercept(&self.path, state, &self.actions, &self.hashes)? {
            return Ok(CYCLE);
        }
        self.best(state, None)
    }

    /// C#: Best. The best line from `state`: where the player is still on this turn, or at a card prompt. `share_to`:
    /// where the state's share of the nodes ends, when its actions share it evenly; none where they are tried depth
    /// first.
    fn best(&mut self, state: &mut State, share_to: Option<i32>) -> Result<usize, String> {
        let hash = state.hash;
        let key = (state.state_key, state.hidden_key);
        if let Some(&known) = self.memo.get(&key) {
            return Ok(known);
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
        let (cuts, from) = (self.cuts, self.nodes);
        let (todo, left) = order::actions(state, (share_to.unwrap_or(self.limit) - self.nodes) as i64);
        if left {
            self.cuts += 1;
        }
        let mut tried = Vec::with_capacity(todo.len());
        let mut moved = false;
        for (i, action) in todo.iter().enumerate() {
            let mut to = None;
            if let Some(end) = share_to {
                let share = from as i64 + (end - from) as i64 * (i as i64 + 1) / todo.len() as i64;
                self.limit = (self.nodes + 1).max(share as i32);
                to = Some(self.limit);
            } else if moved && self.nodes >= self.limit {
                self.cuts += 1;
                break;
            }
            moved = true;
            let line = self.after(state, action, hash, to)?;
            tried.push((action.clone(), line));
        }
        if plays {
            self.path.pop();
        }
        self.found.push(Found::Best { hash, tried });
        let id = self.found.len() - 1;
        if self.cuts == cuts {
            self.memo.insert(key, id);
        }
        Ok(id)
    }

    /// The line that ends at `state`, whose end is valued once the world is walked; an end met before with the same
    /// status, observation and run is the same slot.
    fn end(&mut self, state: &mut State) -> usize {
        let run = state.run();
        let mut hash = Fnv::new();
        protocol::run_json(&run, &mut hash);
        let key = (state.status, state.hash, hash.0);
        let slot = match self.slots.get(&key) {
            Some(&slot) => slot,
            None => {
                self.ends.push(LeafState {
                    status: state.status,
                    obs: state.obs.clone(),
                    run,
                    history: self.history.clone(),
                });
                self.slots.insert(key, self.ends.len() - 1);
                self.ends.len() - 1
            }
        };
        self.found.push(Found::End(slot));
        self.found.len() - 1
    }

    /// The world's ends valued, in the order found, `leaf::BATCH` to an evaluation.
    fn valued(&self) -> Result<Vec<LeafOutcome>, String> {
        let ends: Vec<&LeafState> = self.ends.iter().collect();
        self.leaf.outcomes(&ends)
    }

    /// Every line found, with the ends' values: an end is worth its slot's; a state, its best action's line, the first
    /// of the best (C#: `after.Value > best.Value`), none worth more than no line at all.
    fn resolved(&self, outcomes: &[LeafOutcome]) -> Vec<Line> {
        let none = || Line {
            value: f64::NEG_INFINITY,
            actions: Vec::new(),
            hashes: Vec::new(),
            repeats: false,
        };
        let mut lines: Vec<Line> = Vec::with_capacity(self.found.len());
        for found in &self.found {
            let line = match found {
                Found::Cycle => none(),
                Found::End(slot) => Line {
                    value: outcomes[*slot].value,
                    ..none()
                },
                Found::Best { hash, tried } => {
                    let mut best: Option<(&Action, &Line)> = None;
                    let mut value = f64::NEG_INFINITY;
                    for (action, id) in tried {
                        let after = &lines[*id];
                        if after.value > value {
                            value = after.value;
                            best = Some((action, after));
                        }
                    }
                    match best {
                        None => none(),
                        Some((action, after)) => Line {
                            value,
                            actions: std::iter::once(action.clone()).chain(after.actions.iter().cloned()).collect(),
                            hashes: std::iter::once(*hash).chain(after.hashes.iter().copied()).collect(),
                            repeats: false,
                        },
                    }
                }
            };
            lines.push(line);
        }
        lines
    }
}
