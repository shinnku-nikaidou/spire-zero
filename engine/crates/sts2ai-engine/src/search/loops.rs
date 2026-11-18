//! Cycles inside a turn (C#: LoopSearch, where the reasons are). A move that brings back the cards of a state on the
//! path, with no less energy or stars, no power of the player's lower, no fewer copies of those cards left and
//! something changed, is tried a few more times; if it holds, it is set aside (`intercept`), and after the search it is
//! repeated 1, 2, 4... times, each count cashed out by a small search to the next turn's start (`run_found`).

use std::collections::HashSet;
use std::sync::Arc;

use super::order;
use super::state::{Action, State, Status};
use super::turn::TurnEnd;
use crate::protocol::{self, CardView, CombatObservation, PowerView};
use crate::rt::Str;

/// A state on the search's current path, for a later state to be compared with (C#: LoopPoint).
pub struct LoopPoint {
    pub obs: Arc<CombatObservation>,
    /// How many actions of the path lead to it.
    pub depth: usize,
    /// The world at it, which a cycle found there starts from.
    pub state: State,
}

/// A line's end valued at once, with the world at its end (C#: LoopSearch.EndOf, the turn search's End).
pub type EndOf<'a> = dyn Fn(&mut State, &[Action], &[u64]) -> Result<TurnEnd, String> + 'a;

/// C#: LoopSearch.Moves. The moves one search may play for its cycles, on top of its nodes; cutting a win down may
/// use as many again.
pub const MOVES: i32 = 4000;
const PROBE_REPEATS: i32 = 4;
const CASH_NODES: i32 = 60;
const MAX_REPEATS: i32 = 4096;
const FLAT_ROUNDS: i32 = 2;
const MAX_BREAKS: usize = 8;

struct Cycle {
    start: State,
    prefix: Vec<Action>,
    prefix_hashes: Vec<u64>,
    moves: Vec<Action>,
    ids: Vec<Option<Str>>,
}

#[derive(Clone, Default)]
struct Line {
    actions: Vec<Action>,
    hashes: Vec<u64>,
}

/// C#: LoopSearch.
pub struct Loops {
    round: i32,
    found: Vec<Cycle>,
    used: i32,
    won: bool,
    cutting: bool,
    /// Whether a cycle was found: the search has then not seen everything there is.
    pub any: bool,
}

impl Loops {
    pub fn new(round: i32) -> Loops {
        Loops {
            round,
            found: Vec::new(),
            used: 0,
            won: false,
            cutting: false,
            any: false,
        }
    }

    /// Out of moves, or a cycle already wins: nothing more is looked for.
    fn spent(&self) -> bool {
        self.used >= MOVES || self.won
    }

    /// C#: Intercept. Whether the search should not go down from `now`, the state the path's last action reached: it
    /// comes back to a state on the path with nothing changed, or it is a cycle, which is kept for `run_found`.
    pub fn intercept(&mut self, path: &[LoopPoint], now: &State, actions: &[Action], hashes: &[u64]) -> Result<bool, String> {
        let Some(back) = back(path, &now.obs) else {
            return Ok(false);
        };
        if shape(&back.obs) == shape(&now.obs) {
            return Ok(true);
        }
        if self.spent() {
            return Ok(false);
        }
        let cycle = cycled(path, back, actions, hashes);
        let mut world = copy(now);
        let mut done = 1;
        let mut scratch = Line::default();
        let mut status = Status::InProgress;
        let holds = self.repeat(&mut world, &cycle, &mut scratch, &mut done, PROBE_REPEATS, None, &mut status, Some(&back.obs))?
            && status == Status::InProgress;
        if holds {
            self.found.push(cycle);
            self.any = true;
        }
        Ok(holds)
    }

    /// C#: RunFound. Repeats and cashes out the cycles found since the last call, in the order found.
    pub fn run_found(&mut self, end: &EndOf) -> Result<Vec<TurnEnd>, String> {
        let mut ends = Vec::new();
        let found = std::mem::take(&mut self.found);
        for cycle in &found {
            if self.spent() {
                break;
            }
            ends.extend(self.run(cycle, 0, end)?);
        }
        Ok(ends)
    }

    /// C#: Play, the world going on with the action.
    fn play(&mut self, world: &mut State, action: &Action, line: &mut Line) -> Result<(), String> {
        *world = self.play_from(world, action, line)?;
        Ok(())
    }

    /// C#: Play, from a state that stays (a snapshot the search comes back to).
    fn play_from(&mut self, world: &mut State, action: &Action, line: &mut Line) -> Result<State, String> {
        self.used += 1;
        line.hashes.push(world.hash);
        let after = world.step(action)?;
        line.actions.push(action.clone());
        Ok(after)
    }

    /// C#: Repeat. Plays the cycle until `done` reaches `to`; false if a move is not there, or the combat or the budget
    /// ended. `breaks` takes the repeats done before one that changed the piles; `back`, for a probe, is the state each
    /// repeat must bring back.
    #[allow(clippy::too_many_arguments)]
    fn repeat(
        &mut self,
        world: &mut State,
        cycle: &Cycle,
        line: &mut Line,
        done: &mut i32,
        to: i32,
        mut breaks: Option<&mut Vec<i32>>,
        status: &mut Status,
        back: Option<&CombatObservation>,
    ) -> Result<bool, String> {
        *status = world.status;
        while *done < to {
            let piles_before = breaks.as_ref().map(|_| piles(&world.obs));
            for (i, mv) in cycle.moves.iter().enumerate() {
                if *status != Status::InProgress || self.used >= 2 * MOVES {
                    return Ok(false);
                }
                let Some(mv) = find(world, mv, cycle.ids[i]) else {
                    return Ok(false);
                };
                self.play(world, &mv, line)?;
                *status = world.status;
            }
            if *status == Status::InProgress
                && let Some(breaks) = breaks.as_deref_mut()
                && breaks.len() < MAX_BREAKS
                && *done >= 1
                && Some(piles(&world.obs)) != piles_before
            {
                breaks.push(*done);
            }
            if let Some(back) = back
                && *status == Status::InProgress
                && !recurs(back, &world.obs)
            {
                return Ok(false);
            }
            *done += 1;
        }
        Ok(true)
    }

    /// C#: Cash. The best the state can do with what the repeats built: a small search to the end of the turn,
    /// without the cycle's moves.
    fn cash(&mut self, mut world: State, mut line: Line, banned: &HashSet<Action>, depth: i32, end: &EndOf) -> Result<Option<TurnEnd>, String> {
        let mut cash = Cash {
            ends: Vec::new(),
            nodes: 0,
            seen: HashSet::new(),
        };
        if world.status != Status::InProgress {
            cash.ends.push(end(&mut world, &line.actions, &line.hashes)?);
        } else {
            self.go(&mut cash, &mut world, &[], &mut line, banned, depth, end)?;
        }
        // C#: OrderByDescending(Value).ThenBy(Actions.Count).FirstOrDefault(), a stable sort.
        let mut ends = cash.ends;
        ends.sort_by(|a, b| b.value.partial_cmp(&a.value).unwrap_or(std::cmp::Ordering::Equal).then(a.actions.len().cmp(&b.actions.len())));
        Ok(ends.into_iter().next())
    }

    #[allow(clippy::too_many_arguments)]
    fn go(
        &mut self,
        cash: &mut Cash,
        world: &mut State,
        stack: &[&LoopPoint],
        line: &mut Line,
        banned: &HashSet<Action>,
        depth: i32,
        end: &EndOf,
    ) -> Result<(), String> {
        if !cash.seen.insert((world.state_key, world.hidden_key)) {
            return Ok(());
        }
        // A prompt stands in the middle of an action: it is no point a cycle comes back to.
        let own = world.obs.prompt.is_none().then(|| LoopPoint {
            obs: world.obs.clone(),
            depth: line.actions.len(),
            state: copy(world),
        });
        let path: Vec<&LoopPoint> = stack.iter().copied().chain(own.iter()).collect();
        let (todo, _) = order::actions(world, (CASH_NODES - cash.nodes) as i64);
        let todo: Vec<Action> = todo.into_iter().filter(|a| !banned.contains(a)).collect();
        let mut moved = false;
        for action in &todo {
            let limit = if self.cutting { 2 * MOVES } else { MOVES };
            if moved && (cash.nodes >= CASH_NODES || self.used >= limit) {
                break;
            }
            moved = true;
            cash.nodes += 1;
            let mut after = self.play_from(world, action, line)?;
            let status = after.status;
            if status != Status::InProgress {
                cash.ends.push(end(&mut after, &line.actions, &line.hashes)?);
            } else if after.obs.prompt.is_some() {
                self.go(cash, &mut after, &path, line, banned, depth, end)?;
            } else if action.is_end_turn() || after.round() != self.round {
                cash.ends.push(end(&mut after, &line.actions, &line.hashes)?);
            } else {
                match back_of(&path, &after.obs) {
                    None => self.go(cash, &mut after, &path, line, banned, depth, end)?,
                    Some(back) => {
                        if shape(&back.obs) != shape(&after.obs) && depth < 2 {
                            let cycle = cycled_of(&path, back, &line.actions, &line.hashes);
                            let ran = self.run(&cycle, depth + 1, end)?;
                            cash.ends.extend(ran);
                        }
                    }
                }
            }
            line.actions.pop();
            line.hashes.pop();
        }
        Ok(())
    }

    /// C#: Run. A cycle's lines: each count it was cashed out at, best first and, among equals, the shortest. Only a
    /// cycle the search found goes back to the counts where the piles changed.
    fn run(&mut self, cycle: &Cycle, depth: i32, end: &EndOf) -> Result<Vec<TurnEnd>, String> {
        // The cycle's plays are left out of the cash-out; its answers are answers to whatever prompt waits, and stay.
        let banned: HashSet<Action> = cycle
            .moves
            .iter()
            .filter(|m| matches!(m, Action::Play { .. } | Action::Potion { .. }))
            .cloned()
            .collect();
        let mut kept: Vec<TurnEnd> = Vec::new();
        let mut breaks: Vec<i32> = Vec::new();
        let (mut best, mut likeliest) = (f64::NEG_INFINITY, f64::NEG_INFINITY);
        let (mut flat, mut lose, mut win, mut done) = (0, 0, -1, 0);
        let mut world = copy(&cycle.start);
        let mut line = Line {
            actions: cycle.prefix.clone(),
            hashes: cycle.prefix_hashes.clone(),
        };
        let mut count = 1;
        while count <= MAX_REPEATS && !self.spent() {
            let mut status = Status::InProgress;
            let held = self.repeat(
                &mut world,
                cycle,
                &mut line,
                &mut done,
                count,
                if depth == 0 { Some(&mut breaks) } else { None },
                &mut status,
                None,
            )?;
            if status == Status::Won {
                kept.push(end(&mut world, &line.actions, &line.hashes)?);
                win = done;
                break;
            }
            if status == Status::Lost {
                break;
            }
            let cashed = self.cash(copy(&world), line.clone(), &banned, depth, end)?;
            let cashed_won = cashed.as_ref().is_some_and(|c| c.status == Status::Won);
            let (value, chance) = cashed.as_ref().map(|c| (c.value, c.win)).unwrap_or((f64::NEG_INFINITY, f64::NEG_INFINITY));
            if let Some(cashed) = cashed {
                kept.push(cashed);
            }
            if cashed_won {
                win = done;
                break;
            }
            // On a fight the leaf calls nearly hopeless the value barely moves until the repeats near lethal; the chance
            // to win shows they are getting there.
            if value > best || chance > likeliest {
                best = best.max(value);
                likeliest = likeliest.max(chance);
                flat = 0;
            } else {
                flat += 1;
                if flat >= FLAT_ROUNDS {
                    break;
                }
            }
            lose = done;
            if !held {
                break;
            }
            count *= 2;
        }
        if win < 0 {
            let mut distinct: Vec<i32> = Vec::new();
            for b in &breaks {
                if !distinct.contains(b) {
                    distinct.push(*b);
                }
            }
            for count in distinct {
                if self.used >= 2 * MOVES {
                    break;
                }
                let (at, status, world) = self.replayed(cycle, count)?;
                if status == Status::InProgress
                    && let Some(cashed) = self.cash(world, at, &banned, depth, end)?
                {
                    kept.push(cashed);
                }
            }
        }
        // Won lines are worth the same at any count that wins with the same HP, and a shorter one is less to play and to log.
        let outer = self.cutting;
        self.cutting = true;
        while win > 0 && win - lose > 1 && self.used < 2 * MOVES {
            let count = (lose + win) / 2;
            let (at, status, mut world) = self.replayed(cycle, count)?;
            if status == Status::Won {
                kept.push(end(&mut world, &at.actions, &at.hashes)?);
                win = count;
                continue;
            }
            if status == Status::Lost {
                break;
            }
            let cashed = self.cash(world, at, &banned, depth, end)?;
            let cashed_won = cashed.as_ref().is_some_and(|c| c.status == Status::Won);
            if let Some(cashed) = cashed {
                kept.push(cashed);
            }
            if cashed_won {
                win = count;
            } else {
                lose = count;
            }
        }
        self.cutting = outer;
        if kept.iter().any(|e| e.status == Status::Won) {
            self.won = true;
        }
        kept.sort_by(|a, b| b.value.partial_cmp(&a.value).unwrap_or(std::cmp::Ordering::Equal).then(a.actions.len().cmp(&b.actions.len())));
        Ok(kept)
    }

    fn replayed(&mut self, cycle: &Cycle, count: i32) -> Result<(Line, Status, State), String> {
        let mut world = copy(&cycle.start);
        let mut line = Line {
            actions: cycle.prefix.clone(),
            hashes: cycle.prefix_hashes.clone(),
        };
        let mut done = 0;
        let mut status = Status::InProgress;
        self.repeat(&mut world, cycle, &mut line, &mut done, count, None, &mut status, None)?;
        Ok((line, status, world))
    }
}

struct Cash {
    ends: Vec<TurnEnd>,
    nodes: i32,
    seen: HashSet<(u64, u64)>,
}

/// A state as it stands, apart from the one it was copied from (the world is plain data; C#: a snapshot taken there).
pub fn copy(state: &State) -> State {
    state.copy()
}

/// C#: Back. The latest state on the path that `now` brings back, if any.
fn back<'a>(path: &'a [LoopPoint], now: &CombatObservation) -> Option<&'a LoopPoint> {
    path.iter().rev().find(|p| recurs(&p.obs, now))
}

fn back_of<'a>(path: &[&'a LoopPoint], now: &CombatObservation) -> Option<&'a LoopPoint> {
    path.iter().rev().find(|p| recurs(&p.obs, now)).copied()
}

/// C#: Recurs. Whether the moves from `from` to `now` can be played again: the same round, potions and living enemies,
/// every card of its hand back (a copy will do) and no dearer, no less energy or stars, every power of the player's
/// still there and no lower, and no fewer copies of its hand's cards left to draw or play.
pub fn recurs(from: &CombatObservation, now: &CombatObservation) -> bool {
    if from.round != now.round || now.player.energy < from.player.energy || now.player.stars < from.player.stars {
        return false;
    }
    if !from
        .player
        .powers
        .iter()
        .all(|p| now.player.powers.iter().any(|q| q.id == p.id && q.amount >= p.amount))
    {
        return false;
    }
    if from.potions != now.potions {
        return false;
    }
    let alive = |obs: &CombatObservation| -> Vec<Option<u32>> { obs.enemies.iter().filter(|e| e.hp > 0).map(|e| e.uid).collect() };
    if alive(from) != alive(now) {
        return false;
    }
    let mut hand: Vec<&CardView> = now.hand.iter().collect();
    for card in &from.hand {
        let found = hand.iter().position(|c| {
            c.id == card.id
                && c.up == card.up
                && c.cost.unwrap_or(i32::MAX) <= card.cost.unwrap_or(i32::MAX)
                && c.stars.unwrap_or(0) <= card.stars.unwrap_or(0)
        });
        match found {
            Some(i) => {
                hand.remove(i);
            }
            None => return false,
        }
    }
    let left = |obs: &CombatObservation, id: Str| obs.hand.iter().chain(&obs.draw).chain(&obs.discard).filter(|c| c.id == id).count();
    let mut ids: Vec<Str> = Vec::new();
    for card in &from.hand {
        if !ids.contains(&card.id) {
            ids.push(card.id);
        }
    }
    ids.into_iter().all(|id| left(now, id) >= left(from, id))
}

fn cycled(path: &[LoopPoint], back: &LoopPoint, actions: &[Action], hashes: &[u64]) -> Cycle {
    let points: Vec<&LoopPoint> = path.iter().collect();
    cycled_of(&points, back, actions, hashes)
}

/// C#: Cycled.
fn cycled_of(path: &[&LoopPoint], back: &LoopPoint, actions: &[Action], hashes: &[u64]) -> Cycle {
    let moves: Vec<Action> = actions[back.depth..].to_vec();
    let ids = moves
        .iter()
        .enumerate()
        .map(|(i, mv)| {
            let before = path.iter().find(|p| p.depth == back.depth + i)?;
            let card = match mv {
                Action::Play { card, .. } => Some(*card),
                _ => None,
            };
            before.obs.hand.iter().find(|c| c.uid == card).map(|c| c.id)
        })
        .collect();
    Cycle {
        start: copy(&back.state),
        prefix: actions[..back.depth].to_vec(),
        prefix_hashes: hashes[..back.depth].to_vec(),
        moves,
        ids,
    }
}

/// C#: Find. The move, or the same card under a new uid: a copy the game made is the same step of the cycle.
fn find(world: &mut State, mv: &Action, id: Option<Str>) -> Option<Action> {
    let obs = world.obs.clone();
    let legal = world.legal();
    if takes(&obs, mv, &legal) {
        return Some(mv.clone());
    }
    let id = id?;
    let target = match mv {
        Action::Play { target, .. } | Action::Potion { target, .. } => *target,
        _ => None,
    };
    legal.into_iter().find(|action| match action {
        Action::Play { card, target: t } => *t == target && obs.hand.iter().find(|c| c.uid == Some(*card)).is_some_and(|c| c.id == id),
        _ => false,
    })
}

/// C#: CombatObservation.Takes.
fn takes(obs: &CombatObservation, action: &Action, legal: &[Action]) -> bool {
    match action {
        Action::Pick(picks) => obs.prompt.as_ref().is_some_and(|p| p.accepts(picks)),
        other => obs.prompt.is_none() && legal.contains(other),
    }
}

/// C#: Shape. The piles and what is fixed about the enemies, then every number: whether two states differ at all.
fn shape(obs: &CombatObservation) -> String {
    format!("{}|{}", piles(obs), numbers(obs))
}

/// C#: Piles.
fn piles(obs: &CombatObservation) -> String {
    fn cards(cards: &[CardView]) -> String {
        let mut each: Vec<String> = cards
            .iter()
            .map(|c| {
                format!(
                    "{}|{}|{}|{}|{}|{}|{}",
                    c.id.as_str(),
                    opt(c.up),
                    opt(c.cost),
                    c.x_cost.map(|b| if b { "True" } else { "False" }).unwrap_or(""),
                    opt(c.stars),
                    c.enchant.as_ref().map(|m| m.id.as_str()).unwrap_or(""),
                    c.affliction.as_ref().map(|m| m.id.as_str()).unwrap_or("")
                )
            })
            .collect();
        each.sort();
        each.join(",")
    }
    let mut text = String::new();
    for pile in [&obs.hand, &obs.draw, &obs.discard, &obs.exhaust] {
        text.push_str(&cards(pile));
        text.push('#');
    }
    let potions: Vec<&str> = obs.potions.iter().map(|p| p.map(|p| p.as_str()).unwrap_or("")).collect();
    text.push_str(&potions.join(","));
    text.push('#');
    let orbs: Vec<&str> = obs.player.orbs.iter().map(|o| o.id.as_str()).collect();
    text.push_str(&orbs.join(","));
    text.push('#');
    for enemy in obs.enemies.iter().filter(|e| e.hp > 0) {
        let intents: Vec<&str> = enemy.intents.iter().flatten().map(|i| i.type_.as_str()).collect();
        text.push_str(&format!("{}:{};", opt(enemy.uid), intents.join("+")));
    }
    text
}

/// C#: Numbers.
fn numbers(obs: &CombatObservation) -> String {
    fn powers(text: &mut String, powers: &[PowerView]) {
        let mut sorted: Vec<&PowerView> = powers.iter().collect();
        // C#: OrderBy(p => p.Id, StringComparer.Ordinal), a stable sort.
        sorted.sort_by(|a, b| a.id.as_str().cmp(b.id.as_str()));
        for power in sorted {
            text.push_str(&format!("{}={},", power.id.as_str(), power.amount));
        }
    }
    let player = &obs.player;
    let mut text = format!("{},{},{},{};", player.hp, player.block, player.energy, player.stars);
    powers(&mut text, &player.powers);
    for enemy in &obs.enemies {
        text.push_str(&format!("|{},{};", enemy.hp, enemy.block));
        powers(&mut text, &enemy.powers);
    }
    for card in &obs.hand {
        let mut vars: Vec<&(Str, crate::rt::Dec)> = card.vars.iter().flatten().collect();
        vars.sort_by(|a, b| a.0.as_str().cmp(b.0.as_str()));
        for (k, v) in vars {
            let mut value = Vec::new();
            protocol::decimal(*v, &mut value);
            text.push('/');
            text.push_str(k.as_str());
            text.push('=');
            text.push_str(std::str::from_utf8(&value).expect("a decimal is ASCII"));
        }
    }
    text
}

fn opt<T: std::fmt::Display>(v: Option<T>) -> String {
    v.map(|v| v.to_string()).unwrap_or_default()
}
