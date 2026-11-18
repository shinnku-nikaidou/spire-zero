//! The SL combat brain's beam over turns: deterministic planning over the whole combat in the one exact world. Nothing
//! is hidden there and nothing is random, so the best line is the answer, with no average over worlds; it is planned
//! once and then played as planned (the C# route follows it with a TurnPlan).
//!
//! Each state kept is searched to the end of its turn (`turn.rs`): a depth-first search over the turn's actions, ending
//! the turn an option at every step, one action of each that are the same, and transpositions merged by the state as
//! search tells it (the same cards in another order, or another copy of one, are one state) and the world's full hidden
//! state. Each distinct state where the turn ends, after the enemies' turn, is scored by the leaf (the combat net, which
//! reads the observation alone); the best `beam` of all the states' ends, distinct by key, are the next turn's states. A
//! line that ends the combat is scored by the leaf at its end. The beam stops when no state is left, or after
//! `MAX_TURNS` turns. The leaf prices a win by V on the run the combat leaves, which a line can still raise past winning
//! now with what the state holds (max HP or gold gained, a card grown), so no state's value caps its lines'; one rule
//! written by hand does, for speed (`bound.rs`). A greedy dive, a beam of one, goes first, each of its turns searched in
//! full and kept for the full beam, which takes them from there; when the dive wins, the full beam keeps `easy_beam`
//! states, if set: it only improves a win already found.
//!
//! The pick (`Rank`): every loss is worth the same, so among losing lines the one that died from the turn start the leaf
//! valued highest is the pick. Among wins worth exactly the same, the one that drinks the fewest potions is, then the one
//! with the fewest turns ended: a combat no line loses HP in has many, and the dive, which goes first, often finds a slow
//! one. A turn costs nothing in the game, so a win worth less is never taken for being sooner, unless the budget has a
//! turn decay (C#'s Budget.TurnDecay, a player's 0.999): then the pick compares each line's value times the decay to the
//! number of turns it ended.
//!
//! A potion is not drunk where not drinking it loses no more HP (the user, 2026-11-16). The game lets a potion be thrown
//! away at any time for nothing, so a belt is never worth less than the belt with fewer potions: V prices a win so
//! (FreeDisposal, `leaf.rs`), and a turn's end takes the value of any end of that turn that is the same state with only
//! fewer potions (`turn.rs`). A line that drank a potion for nothing is then worth no more than the line that kept it,
//! which is searched first (the potions come last, `order.rs`) and wins the tie. A losing plan that is not proved is
//! played for its first `replan` turns only, then planned again (C#'s PlanOf), from where a beam reaches deeper.
//!
//! Only one turn's transpositions are merged, so each state kept is the end of a concrete line of actions, and the plan
//! replays exactly in the world it was searched in. A card prompt is a state like any other, and its answers are its
//! actions: every prompt of a line is a branch, and a line ends only where the player can play again. A cycle goes to
//! `loops.rs`. The turns' searches are independent, so a turn is one `Searcher` call that can run them apart
//! (`threads.rs`). The plan made of the pick, with the candidates, is the C# side's (PlanOf).
//!
//! Ported line for line from the C# brain (plans/2026-11-18-2132-sl-search-rust.md, B and C); the C# search was deleted
//! on 2026-11-19 at the user's word (C5), and the `C#:` names below are where each piece came from, in git's history.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use super::bound::{self, Bounds};
use super::state::{Action, History, Status};
use super::turn::{TurnEnd, TurnSearch};

/// C#: SlPlanner.MaxTurns.
pub const MAX_TURNS: i32 = 40;

/// C#: SlPlanner.Unlimited.
pub const UNLIMITED: i32 = i32::MAX;

/// C#: PlanJob. A state of the beam as a turn's search needs it.
pub struct Job {
    pub path: Vec<Action>,
    pub history: Arc<History>,
    pub bounds: Bounds,
}

/// C#: TurnSearcher. Searches one turn from each job's state, in job order, each with `nodes` nodes, keeping `keep`.
pub trait Searcher {
    fn search(&self, jobs: &[Job], nodes: i32, keep: usize) -> Result<Vec<TurnSearch>, String>;
}

/// What of C#'s Budget the beam reads.
pub struct Budget {
    pub nodes_per_world: i32,
    pub beam: i32,
    pub easy_beam: i32,
    pub turn_decay: f64,
}

/// What the beam found, for C#'s PlanOf.Found: the line picked, the value of the turn start a losing pick died
/// from, the best line after each first action in the order first found, and how the search went.
pub struct Found {
    pub pick: TurnEnd,
    pub from: f64,
    pub by_first: Vec<TurnEnd>,
    pub proved: bool,
    pub nodes: i32,
    pub turns: i32,
}

/// C#: Math.Pow.
pub fn pow(x: f64, y: f64) -> f64 {
    x.powf(y)
}

/// C#: double.CompareTo: NaN below every number, -0 the same as +0.
pub fn compare(a: f64, b: f64) -> std::cmp::Ordering {
    use std::cmp::Ordering::*;
    match a.partial_cmp(&b) {
        Some(order) => order,
        None if a.is_nan() && b.is_nan() => Equal,
        None if a.is_nan() => Less,
        None => Greater,
    }
}

fn end_turns(actions: &[Action]) -> i32 {
    actions.iter().filter(|a| a.is_end_turn()).count() as i32
}

/// C#: SlPlanner.Rank. A line's place in the pick.
#[derive(Copy, Clone, Debug)]
pub struct Rank {
    pub status: Status,
    pub worth: f64,
    pub from: f64,
    pub drunk: i32,
    pub turns: i32,
}

impl Rank {
    /// `from`: for a loss, the value of the turn start it died from.
    pub fn of(line: &TurnEnd, decay: f64, from: f64) -> Rank {
        let turns = end_turns(&line.actions);
        Rank {
            status: line.status,
            worth: line.value * pow(decay, turns as f64),
            from,
            drunk: line.actions.iter().filter(|a| matches!(a, Action::Potion { .. })).count() as i32,
            turns,
        }
    }

    /// Whether a line so ranked is picked over `best`, the best so far.
    pub fn beats(&self, best: &Rank) -> bool {
        self.worth > best.worth
            || (self.worth == best.worth
                && if self.status == Status::Lost {
                    self.from > best.from
                } else {
                    self.status == Status::Won && (self.drunk, self.turns) < (best.drunk, best.turns)
                })
    }
}

/// The best line after each first action, in the order the first actions were first found (C#: a Dictionary's).
#[derive(Default)]
pub struct ByFirst {
    lines: Vec<TurnEnd>,
    at: HashMap<Action, usize>,
}

impl ByFirst {
    pub fn get(&self, first: &Action) -> Option<&TurnEnd> {
        self.at.get(first).map(|&i| &self.lines[i])
    }

    /// Sets the line of its first action, which keeps its place if it had one.
    pub fn set(&mut self, line: TurnEnd) {
        let first = line.actions[0].clone();
        match self.at.get(&first) {
            Some(&i) => self.lines[i] = line,
            None => {
                self.at.insert(first, self.lines.len());
                self.lines.push(line);
            }
        }
    }

    pub fn lines(&self) -> &[TurnEnd] {
        &self.lines
    }
}

/// C#: SlPlanner.Narrow. What a turn's search finds with `keep`, from what it found with every end kept.
pub fn narrow(full: &TurnSearch, keep: usize) -> TurnSearch {
    let over = full.ends.iter().filter(|e| e.status != Status::InProgress).cloned();
    let going: Vec<&TurnEnd> = full.ends.iter().filter(|e| e.status == Status::InProgress).collect();
    let mut ends: Vec<TurnEnd> = over.collect();
    ends.extend(going.iter().take(keep).map(|e| (*e).clone()));
    TurnSearch {
        ends,
        nodes: full.nodes,
        cut: full.cut || going.len() > keep,
        pruned: full.pruned,
    }
}

/// A line's start: the decision, with nothing played yet.
pub fn start(history: Arc<History>) -> TurnEnd {
    TurnEnd {
        actions: Vec::new(),
        hashes: Vec::new(),
        status: Status::InProgress,
        value: 0.0,
        key: 0,
        history,
        win: 0.0,
        hp: 0.0,
        life: 0,
        reach: 0,
        potions: String::new(),
        shown: 0,
    }
}

/// `line` after `before`: the actions and hashes from the decision.
pub fn after(before: &TurnEnd, line: &TurnEnd) -> TurnEnd {
    let mut actions = before.actions.clone();
    actions.extend_from_slice(&line.actions);
    let mut hashes = before.hashes.clone();
    hashes.extend_from_slice(&line.hashes);
    TurnEnd {
        actions,
        hashes,
        ..line.clone()
    }
}

/// C#: SlPlanner.Search. The beam over turns from the decision's state.
pub fn search(budget: &Budget, history: Arc<History>, searcher: &dyn Searcher) -> Result<Found, String> {
    let mut beam = Beam {
        nodes_per_state: budget.nodes_per_world,
        decay: budget.turn_decay,
        history,
        searcher,
        searched: HashMap::new(),
        by_first: ByFirst::default(),
        best: None,
        bounds: Bounds::new(),
        left: Vec::new(),
        nodes: 0,
        turns: 0,
        complete: false,
    };
    if budget.beam > 1 {
        beam.run(1)?;
    }
    if !beam.proved() {
        let width = if beam.wins() && budget.easy_beam > 0 { budget.easy_beam } else { budget.beam };
        beam.run(width)?;
    }
    beam.result()
}

struct Beam<'a> {
    nodes_per_state: i32,
    decay: f64,
    history: Arc<History>,
    searcher: &'a dyn Searcher,
    /// The dive's turns, each searched in full (every end), by the path to the turn's state.
    searched: HashMap<String, TurnSearch>,
    by_first: ByFirst,
    best: Option<(TurnEnd, Rank)>,
    bounds: Bounds,
    left: Vec<TurnEnd>,
    nodes: i32,
    turns: i32,
    complete: bool,
}

impl Beam<'_> {
    fn proved(&self) -> bool {
        self.best.is_some() && self.complete
    }

    fn wins(&self) -> bool {
        self.best.as_ref().is_some_and(|(line, _)| line.status == Status::Won)
    }

    /// A beam of `width` states from the decision.
    fn run(&mut self, width: i32) -> Result<(), String> {
        let mut states = vec![start(self.history.clone())];
        self.complete = true;
        let mut turn = 0;
        while !states.is_empty() && turn < MAX_TURNS {
            let found = self.search(&states, width)?;
            self.turns += 1;
            let mut next = Vec::new();
            for (state, found) in states.iter().zip(found) {
                self.nodes = self.nodes.wrapping_add(found.nodes);
                self.complete &= !found.cut;
                for end in &found.ends {
                    let line = after(state, end);
                    if line.status == Status::InProgress {
                        next.push(line);
                        continue;
                    }
                    self.note(&line);
                    bound::note(&mut self.bounds, line.status == Status::Won, &line.potions, line.life);
                    let rank = Rank::of(&line, self.decay, state.value);
                    if self.best.as_ref().is_none_or(|(_, best)| rank.beats(best)) {
                        self.best = Some((line, rank));
                    }
                }
            }
            let mut seen = HashSet::new();
            states = Vec::new();
            // A stable sort: ties stay in the order the states were searched in.
            next.sort_by(|a, b| compare(b.value, a.value));
            for line in next {
                if !seen.insert(line.key) || bound::below(&self.bounds, &line.potions, line.reach) {
                    continue;
                }
                if states.len() as i32 == width {
                    self.complete = false;
                    break;
                }
                states.push(line);
            }
            turn += 1;
        }
        self.complete &= states.is_empty();
        self.left = states;
        Ok(())
    }

    /// C#: Beam.Search. The turn's search from each state, as a search with this width finds it.
    fn search(&mut self, states: &[TurnEnd], width: i32) -> Result<Vec<TurnSearch>, String> {
        let dive = width == 1;
        let keep = width.max(0) as usize;
        let mut found: Vec<Option<TurnSearch>> = Vec::with_capacity(states.len());
        let mut asked = Vec::new();
        for (i, state) in states.iter().enumerate() {
            match self.searched.get(&path_key(&state.actions)) {
                Some(full) => found.push(Some(TurnSearch { nodes: 0, ..narrow(full, keep) })),
                None => {
                    found.push(None);
                    asked.push(i);
                }
            }
        }
        if !asked.is_empty() {
            let jobs: Vec<Job> = asked
                .iter()
                .map(|&i| Job {
                    path: states[i].actions.clone(),
                    history: states[i].history.clone(),
                    bounds: self.bounds.clone(),
                })
                .collect();
            let replies = self.searcher.search(&jobs, self.nodes_per_state, if dive { usize::MAX } else { keep })?;
            for (k, reply) in replies.into_iter().enumerate() {
                let i = asked[k];
                found[i] = Some(if dive {
                    let narrowed = narrow(&reply, keep);
                    self.searched.insert(path_key(&states[i].actions), reply);
                    narrowed
                } else {
                    reply
                });
            }
        }
        Ok(found.into_iter().map(|f| f.expect("every state searched")).collect())
    }

    /// C#: Beam.Result, up to the plan C#'s PlanOf.Found makes of it.
    fn result(mut self) -> Result<Found, String> {
        let left = std::mem::take(&mut self.left);
        for state in &left {
            self.note(state);
        }
        // The best end of the combat, unless the search stopped first at a state worth more.
        let stopped = !left.is_empty() && self.best.as_ref().is_none_or(|(_, rank)| self.worth(&left[0]) > rank.worth);
        let proved = self.proved();
        let (pick, from) = if stopped {
            (left.into_iter().next().expect("a state left"), 0.0)
        } else {
            match self.best.take() {
                Some((line, rank)) => (line, rank.from),
                None => return Err("the planner found no line".into()),
            }
        };
        Ok(Found {
            pick,
            from,
            by_first: self.by_first.lines,
            proved,
            nodes: self.nodes,
            turns: self.turns,
        })
    }

    /// C#: Beam.Worth. A line's value, times the turn decay for each turn it ended.
    fn worth(&self, line: &TurnEnd) -> f64 {
        line.value * pow(self.decay, end_turns(&line.actions) as f64)
    }

    /// C#: Beam.Note. Keeps the best of the lines that start with each first action, for the candidates.
    fn note(&mut self, line: &TurnEnd) {
        let better = match self.by_first.get(&line.actions[0]) {
            None => true,
            Some(best) => self.worth(line) > self.worth(best),
        };
        if better {
            self.by_first.set(line.clone());
        }
    }
}

/// C#: Beam.PathKey.
fn path_key(path: &[Action]) -> String {
    path.iter().map(|a| a.text()).collect::<Vec<_>>().join(" ")
}
