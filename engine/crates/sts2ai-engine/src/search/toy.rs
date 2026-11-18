//! Toy combats for the search's tests (C#: SlPlannerTests.Toy and LoopSearchTests.Loop, with the tests' leaves and
//! Fixtures.ToyLeaf), searchers over them, and the plan C# makes of a pick (C#: SlPlanner.Picked, PlanOf since the C#
//! search went). Each is the C# tests' own, so that their expected values carry over: the same observations, the same
//! legal actions in the same order, the same keys and the same values.

use std::cell::{Cell, RefCell};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, OnceLock};

use super::beam::{self, Found, Job, Searcher};
use super::bound::Bounds;
use super::leaf::{Leaf, LeafOutcome, LeafState};
use super::state::{Action, State, Status, ToyWorld};
use super::turn::{self, TurnEnd, TurnSearch};
use crate::protocol::{CardChoice, CardView, ChoiceContext, CombatObservation, IntentView, PowerView, RunChange};
use crate::rt::Str;

/// C#: Fixtures.Slugs. A real decision from self-play: round 1 of CORPSE_SLUGS_WEAK, two slugs (uids 1 and 2), SILENT at
/// 70/70; the C# tests' file.
pub fn slugs() -> CombatObservation {
    static SLUGS: OnceLock<CombatObservation> = OnceLock::new();
    SLUGS
        .get_or_init(|| {
            serde_json::from_str(include_str!("../../../../../src/Sts2Ai.Tests/Fixtures/corpse-slugs-round1.json")).expect("the slugs' observation")
        })
        .clone()
}

/// C#: Math.Round(value, digits), to even.
pub fn round(value: f64, digits: i32) -> f64 {
    if value.abs() >= 1e16 {
        return value;
    }
    let power = 10f64.powi(digits);
    (value * power).round_ties_even() / power
}

// ---------------------------------------------------------------------------------------------------------------------
// Leaves
// ---------------------------------------------------------------------------------------------------------------------

/// C#: Fixtures' ToyLeaf. A leaf for the search tests, which have no combat net or V: a loss nothing, a win 0.2 and more
/// for the HP share the run keeps, and a combat still going below any win, higher for more HP kept and more of the
/// enemies' HP taken.
pub struct ToyLeaf;

impl ToyLeaf {
    pub fn of(status: Status, obs: &CombatObservation, run: &RunChange) -> LeafOutcome {
        let kept = run.hp as f64 / run.max_hp.max(1) as f64;
        match status {
            Status::Lost => LeafOutcome { value: 0.0, win: 0.0, hp: 0.0 },
            Status::Won => LeafOutcome {
                value: 0.2 + 0.8 * kept,
                win: 1.0,
                hp: run.hp as f64,
            },
            Status::InProgress => {
                let hp: i32 = obs.enemies.iter().map(|e| e.hp.max(0)).sum();
                let max_hp: i32 = obs.enemies.iter().map(|e| e.max_hp).sum();
                let taken = 1.0 - hp as f64 / max_hp.max(1) as f64;
                LeafOutcome {
                    value: 0.2 * (kept + taken) / 2.0,
                    win: 0.0,
                    hp: 0.0,
                }
            }
        }
    }
}

impl Leaf for ToyLeaf {
    fn outcome(&self, state: &LeafState) -> Result<LeafOutcome, String> {
        Ok(ToyLeaf::of(state.status, &state.obs, &state.run))
    }

    fn outcomes(&self, states: &[&LeafState]) -> Result<Vec<LeafOutcome>, String> {
        states.iter().map(|s| self.outcome(s)).collect()
    }
}

/// C#: SlPlannerTests.Asked. A leaf that tells how it was asked: one state at a time, or several together.
#[derive(Default)]
pub struct Asked {
    pub alone: Cell<usize>,
    pub together: RefCell<Vec<usize>>,
}

impl Leaf for Asked {
    fn outcome(&self, state: &LeafState) -> Result<LeafOutcome, String> {
        self.alone.set(self.alone.get() + 1);
        ToyLeaf.outcome(state)
    }

    fn outcomes(&self, states: &[&LeafState]) -> Result<Vec<LeafOutcome>, String> {
        self.together.borrow_mut().push(states.len());
        ToyLeaf.outcomes(states)
    }
}

/// C#: SlPlannerTests.Disliking. A leaf that values each potion held below an empty slot while the combat goes on, as a
/// combat net can; a win is priced with free disposal, as the combat net's are, and is not.
pub struct Disliking;

impl Leaf for Disliking {
    fn outcome(&self, state: &LeafState) -> Result<LeafOutcome, String> {
        let outcome = ToyLeaf::of(state.status, &state.obs, &state.run);
        if state.status != Status::InProgress {
            return Ok(outcome);
        }
        let held = state.obs.potions.iter().filter(|p| p.is_some()).count();
        Ok(LeafOutcome {
            value: outcome.value - 0.01 * held as f64,
            ..outcome
        })
    }

    fn outcomes(&self, states: &[&LeafState]) -> Result<Vec<LeafOutcome>, String> {
        states.iter().map(|s| self.outcome(s)).collect()
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Worlds
// ---------------------------------------------------------------------------------------------------------------------

/// How many actions a toy's copies played (C#: Loop.Played): its copies share it.
#[derive(Clone, Default)]
pub struct Played(pub Arc<AtomicUsize>);

impl Played {
    pub fn get(&self) -> usize {
        self.0.load(Ordering::SeqCst)
    }

    fn add(&self) {
        self.0.fetch_add(1, Ordering::SeqCst);
    }
}

/// C#: Fixtures.Around, the run around a toy's combat: the combat's HP, max HP, gold and potions. The rest of the
/// floor-1 run is left out, as no toy leaf reads it.
fn around(obs: &CombatObservation) -> RunChange {
    RunChange {
        hp: obs.player.hp,
        max_hp: obs.player.max_hp,
        gold: obs.player.gold,
        deck: Vec::new(),
        relics: Vec::new(),
        potions: obs.potions.clone(),
    }
}

/// The slugs' first slug, as a toy's one enemy: uid 1, with no block or powers, attacking for `damage`.
fn enemy(start: &CombatObservation, hp: i32, max_hp: i32, damage: i32) -> crate::protocol::CreatureView {
    let mut enemy = start.enemies[0].clone();
    enemy.uid = Some(1);
    enemy.hp = hp.max(0);
    enemy.max_hp = max_hp;
    enemy.block = 0;
    enemy.powers = Vec::new();
    enemy.intents = Some(vec![IntentView {
        type_: Str::lit("Attack"),
        damage: Some(damage),
        hits: Some(1),
    }]);
    enemy
}

fn status(hp: i32, enemy: i32) -> Status {
    if hp <= 0 {
        Status::Lost
    } else if enemy <= 0 {
        Status::Won
    } else {
        Status::InProgress
    }
}

const DECK: [&str; 15] = ["S", "D", "S", "S", "D", "D", "S", "S", "D", "S", "D", "S", "S", "D", "S"];
const ATTACKS: [i32; 3] = [4, 13, 4];

/// C#: SlPlannerTests.Toy. A small exact combat: 2 energy and 3 cards a turn from a fixed draw order, Strikes (1 energy,
/// 6 damage to the one enemy) and Defends (1 energy, 5 block this turn), against an enemy with 24 HP whose attacks are
/// known for every round, 4 then 13 then 4 again. HP never comes back, so the planner's bound holds and a full search is
/// exact.
#[derive(Clone)]
pub struct Toy {
    enemy_hp: i32,
    /// A potion in the first slot that does nothing when drunk; none if None.
    potion: Option<Str>,
    /// Whether the cards left in hand stay there at the turn's end, under their uids.
    retain: bool,
    hp: i32,
    enemy: i32,
    block: i32,
    energy: i32,
    round: i32,
    drawn: i32,
    hand: Vec<(u32, Str)>,
    held: bool,
}

impl Toy {
    pub fn new(enemy_hp: i32, potion: Option<&'static str>, retain: bool) -> Toy {
        let mut toy = Toy {
            enemy_hp,
            potion: potion.map(Str::lit),
            retain,
            hp: 20,
            enemy: enemy_hp,
            block: 0,
            energy: 2,
            round: 1,
            drawn: 0,
            hand: Vec::new(),
            held: potion.is_some(),
        };
        toy.draw();
        toy
    }

    /// The search's state at the toy.
    pub fn state(self) -> State {
        State::toy(Box::new(self))
    }

    fn draw(&mut self) {
        for _ in 0..3 {
            self.hand.push((self.drawn as u32 + 10, Str::lit(DECK[self.drawn as usize % DECK.len()])));
            self.drawn += 1;
        }
    }

    fn attack(&self) -> i32 {
        ATTACKS[(self.round - 1) as usize % ATTACKS.len()]
    }
}

/// The toy at the start: `Toy::new(enemy_hp)` with no potion, and no card retained.
pub fn toy(enemy_hp: i32) -> State {
    Toy::new(enemy_hp, None, false).state()
}

impl ToyWorld for Toy {
    fn observe(&self) -> CombatObservation {
        let start = slugs();
        let mut obs = start.clone();
        obs.round = self.round;
        obs.player.hp = self.hp.max(0);
        obs.player.max_hp = 20;
        obs.player.block = self.block;
        obs.player.energy = self.energy;
        obs.hand = self
            .hand
            .iter()
            .map(|&(uid, id)| CardView {
                id,
                uid: Some(uid),
                ..CardView::default()
            })
            .collect();
        obs.draw = Vec::new();
        obs.discard = Vec::new();
        obs.potions = vec![if self.held { self.potion } else { None }, None, None];
        obs.enemies = vec![enemy(&start, self.enemy, self.enemy_hp, self.attack())];
        obs
    }

    fn status(&self) -> Status {
        status(self.hp, self.enemy)
    }

    fn hidden_key(&self) -> u64 {
        self.drawn as u64
    }

    fn legal(&self) -> Vec<Action> {
        if self.status() != Status::InProgress {
            return Vec::new();
        }
        let mut legal = vec![Action::EndTurn];
        if self.energy > 0 {
            legal.extend(self.hand.iter().map(|&(uid, id)| Action::Play {
                card: uid,
                target: (id.as_str() == "S").then_some(1),
            }));
        }
        if self.held {
            legal.push(Action::Potion { slot: 0, target: None });
        }
        legal
    }

    fn execute(&mut self, action: &Action) -> Result<(), String> {
        if !self.legal().contains(action) {
            return Err(format!("{} is not legal", action.text()));
        }
        match action {
            Action::Potion { .. } => self.held = false,
            Action::EndTurn => {
                self.hp -= (self.attack() - self.block).max(0);
                (self.block, self.energy, self.round) = (0, 2, self.round + 1);
                if !self.retain {
                    self.hand.clear();
                }
                self.draw();
            }
            Action::Play { card, .. } => {
                let at = self.hand.iter().position(|c| c.0 == *card).expect("a card in the hand");
                let (_, id) = self.hand.remove(at);
                self.energy -= 1;
                if id.as_str() == "S" {
                    self.enemy -= 6;
                } else {
                    self.block += 5;
                }
            }
            Action::Pick(_) => unreachable!("the toy asks nothing"),
        }
        Ok(())
    }

    fn run(&self) -> RunChange {
        around(&self.observe())
    }

    fn boxed(&self) -> Box<dyn ToyWorld> {
        Box::new(self.clone())
    }
}

/// C#: LoopSearchTests.Loop.Prompted. A potion that offers three cards, one to take: the last gives R, or a strong
/// one-off card; or it asks twice, and only the last card of the second prompt gives the strong card.
#[derive(Copy, Clone, PartialEq)]
pub enum Prompted {
    No,
    GivesR,
    GivesStrike,
    AsksTwice,
}

/// C#: LoopSearchTests.Loop's parameters.
#[derive(Copy, Clone)]
pub struct Rules {
    pub enemy_hp: i32,
    pub junk_every: i32,
    pub hand_cap: usize,
    pub runs_out: i32,
    pub prompted: Prompted,
    pub souls: i32,
    pub copies: u32,
    pub returns: Option<i32>,
}

impl Rules {
    /// C#: `new Loop(enemyHp)`, the rest at their defaults.
    pub fn new(enemy_hp: i32) -> Rules {
        Rules {
            enemy_hp,
            junk_every: 0,
            hand_cap: 0,
            runs_out: i32::MAX,
            prompted: Prompted::No,
            souls: 0,
            copies: 1,
            returns: None,
        }
    }
}

/// C#: LoopSearchTests.Loop. A turn with a card that comes back: R (free) gives 1 strength for the turn and returns to
/// the hand, H (1 energy) deals 6 plus the strength. The enemies' turn hits for 10. Every `junk_every` plays a junk card
/// comes, to the discard pile, or to the hand if `hand_cap` is set, and then a full hand sends R to the discard pile,
/// which ends the cycle. `runs_out` ends it after that many plays. With `souls`, R is exhausted instead and draws the
/// next of that many R in the draw pile, as a Soul does. With `returns`, R comes back only that many times, as Feral
/// returns free attacks, and the player's power shows how many are left. The hand starts with `copies` R.
#[derive(Clone)]
pub struct Loop {
    rules: Rules,
    hand: Vec<(u32, Str)>,
    discard: Vec<Str>,
    enemy: i32,
    hp: i32,
    energy: i32,
    strength: i32,
    round: i32,
    plays: i32,
    drawn: i32,
    returns: i32,
    potion: bool,
    /// The prompts the potion still asks: the world waits on the first of them.
    asking: i32,
    played: Played,
}

impl Loop {
    pub fn new(rules: Rules) -> Loop {
        let hand = if rules.prompted == Prompted::No {
            let mut hand = vec![(1, Str::lit("R"))];
            hand.extend((1..rules.copies).map(|i| (10 + i, Str::lit("R"))));
            hand.push((2, Str::lit("H")));
            hand
        } else {
            vec![(2, Str::lit("H"))]
        };
        Loop {
            rules,
            hand,
            discard: Vec::new(),
            enemy: rules.enemy_hp,
            hp: 30,
            energy: 1,
            strength: 0,
            round: 1,
            plays: 0,
            drawn: 0,
            returns: rules.returns.unwrap_or(0),
            potion: rules.prompted != Prompted::No,
            asking: 0,
            played: Played::default(),
        }
    }

    pub fn played(&self) -> Played {
        self.played.clone()
    }

    /// The search's state at the toy.
    pub fn state(self) -> State {
        State::toy(Box::new(self))
    }

    /// C#: Loop.Ask, with the source that tells the potion's prompts apart.
    fn ask(asking: i32) -> CardChoice {
        CardChoice {
            options: ["A", "B", "C"]
                .iter()
                .map(|id| CardView {
                    id: Str::lit(id),
                    ..CardView::default()
                })
                .collect(),
            min: 1,
            max: 1,
            context: ChoiceContext {
                purpose: Str::lit("choose_a_card_screen"),
                source: Some(Str::from_string(format!("P{asking}"))),
                cancelable: false,
                in_combat: true,
                ..ChoiceContext::default()
            },
        }
    }
}

impl ToyWorld for Loop {
    fn observe(&self) -> CombatObservation {
        let start = slugs();
        let mut obs = start.clone();
        let power = |id: &'static str, amount: i32| PowerView {
            id: Str::lit(id),
            amount,
            ..PowerView::default()
        };
        obs.round = self.round;
        obs.player.hp = self.hp.max(0);
        obs.player.max_hp = 30;
        obs.player.block = 0;
        obs.player.energy = self.energy;
        obs.player.powers = Vec::new();
        if self.strength > 0 {
            obs.player.powers.push(power("STRENGTH_POWER", self.strength));
        }
        if self.rules.returns.is_some() {
            obs.player.powers.push(power("FERAL_POWER", self.returns));
        }
        obs.hand = self
            .hand
            .iter()
            .map(|&(uid, id)| CardView {
                id,
                uid: Some(uid),
                cost: Some(if id.as_str() == "H" { 1 } else { 0 }),
                ..CardView::default()
            })
            .collect();
        let card = |id: Str| CardView { id, ..CardView::default() };
        obs.draw = (0..self.rules.souls - self.drawn).map(|_| card(Str::lit("R"))).collect();
        obs.discard = self.discard.iter().map(|&id| card(id)).collect();
        obs.potions = vec![self.potion.then_some(Str::lit("P")), None, None];
        obs.enemies = vec![enemy(&start, self.enemy, self.rules.enemy_hp, 10)];
        // The second prompt of two is another prompt: its source tells them apart.
        obs.prompt = (self.asking > 0).then(|| Loop::ask(self.asking));
        obs
    }

    fn status(&self) -> Status {
        status(self.hp, self.enemy)
    }

    fn hidden_key(&self) -> u64 {
        0
    }

    fn legal(&self) -> Vec<Action> {
        if self.status() != Status::InProgress || self.asking > 0 {
            return Vec::new();
        }
        let mut legal = vec![Action::EndTurn];
        legal.extend(
            self.hand
                .iter()
                .filter(|(_, id)| matches!(id.as_str(), "R" | "S") || (id.as_str() == "H" && self.energy > 0))
                .map(|&(uid, id)| Action::Play {
                    card: uid,
                    target: (id.as_str() != "R").then_some(1),
                }),
        );
        if self.potion {
            legal.push(Action::Potion { slot: 0, target: None });
        }
        legal
    }

    fn execute(&mut self, action: &Action) -> Result<(), String> {
        let obs = self.observe();
        // C#: CombatObservation.Takes.
        let takes = match action {
            Action::Pick(picks) => obs.prompt.as_ref().is_some_and(|p| p.accepts(picks)),
            other => obs.prompt.is_none() && self.legal().contains(other),
        };
        if !takes {
            return Err(format!("{} is not legal", action.text()));
        }
        self.played.add();
        let rules = self.rules;
        match action {
            Action::Pick(picks) => {
                // Only the last prompt's last card gives anything.
                self.asking -= 1;
                if self.asking == 0 && picks.as_slice() == [2] {
                    self.hand.push(if rules.prompted == Prompted::GivesR { (1, Str::lit("R")) } else { (3, Str::lit("S")) });
                }
            }
            Action::EndTurn => {
                self.hp -= 10;
                (self.energy, self.strength, self.round) = (1, 0, self.round + 1);
            }
            Action::Potion { .. } => {
                self.potion = false;
                self.asking = if rules.prompted == Prompted::AsksTwice { 2 } else { 1 };
            }
            Action::Play { card, .. } => {
                let at = self.hand.iter().position(|c| c.0 == *card).expect("a card in the hand");
                let (uid, id) = self.hand.remove(at);
                self.plays += 1;
                if rules.junk_every > 0 && self.plays % rules.junk_every == 0 {
                    if rules.hand_cap == 0 {
                        self.discard.push(Str::lit("J"));
                    } else if self.hand.len() < rules.hand_cap {
                        self.hand.push((100 + self.plays as u32, Str::lit("J")));
                    }
                }
                match id.as_str() {
                    "R" => {
                        self.strength += 1;
                        if rules.souls > 0 {
                            if self.drawn < rules.souls {
                                self.hand.push((200 + self.drawn as u32, Str::lit("R")));
                                self.drawn += 1;
                            }
                        } else if (rules.hand_cap == 0 || self.hand.len() < rules.hand_cap)
                            && self.plays < rules.runs_out
                            && (rules.returns.is_none() || self.returns > 0)
                        {
                            self.hand.push((uid, id));
                            if rules.returns.is_some() {
                                self.returns -= 1;
                            }
                        } else {
                            self.discard.push(Str::lit("R"));
                        }
                    }
                    "H" => {
                        self.enemy -= 6 + self.strength;
                        self.energy -= 1;
                        self.discard.push(Str::lit("H"));
                    }
                    _ => self.enemy -= 100,
                }
            }
        }
        Ok(())
    }

    fn run(&self) -> RunChange {
        around(&self.observe())
    }

    fn boxed(&self) -> Box<dyn ToyWorld> {
        Box::new(self.clone())
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Searchers and plans
// ---------------------------------------------------------------------------------------------------------------------

/// C#: SlPlanner.Plan's searcher, and the tests' ToySearcher: each job's state is `root` with the job's path played,
/// its turn searched with `leaf`, its lines held to the job's bounds unless `bounds` is false.
pub struct Replay<'a> {
    pub root: State,
    pub leaf: &'a dyn Leaf,
    pub bounds: bool,
}

impl Searcher for Replay<'_> {
    fn search(&self, jobs: &[Job], nodes: i32, keep: usize) -> Result<Vec<TurnSearch>, String> {
        let none = Bounds::new();
        jobs.iter()
            .map(|job| {
                let mut state = self.root.copy();
                for action in &job.path {
                    state = state.step(action)?;
                }
                turn::search(&mut state, &job.history, nodes, keep, if self.bounds { &job.bounds } else { &none }, self.leaf)
            })
            .collect()
    }
}

/// C#: a TurnSearcher written as a lambda.
pub struct Jobs<F>(F);

/// A searcher of a closure over the jobs, the nodes and the keep.
pub fn jobs<F: Fn(&[Job], i32, usize) -> Result<Vec<TurnSearch>, String>>(search: F) -> Jobs<F> {
    Jobs(search)
}

impl<F: Fn(&[Job], i32, usize) -> Result<Vec<TurnSearch>, String>> Searcher for Jobs<F> {
    fn search(&self, jobs: &[Job], nodes: i32, keep: usize) -> Result<Vec<TurnSearch>, String> {
        (self.0)(jobs, nodes, keep)
    }
}

/// C#: Candidate, as SlPlanner.Picked makes it of the best line after a first action.
pub struct Candidate {
    pub action: String,
    pub value: f64,
    pub win: f64,
    pub hp: f64,
}

/// C#: SlPlan with its CombatDecisionResult.
pub struct Plan {
    pub first: Action,
    pub plan: Vec<Action>,
    pub plan_hashes: Vec<u64>,
    pub candidates: Vec<Candidate>,
    pub value: f64,
    pub end: Status,
    pub nodes: i32,
    pub turns: i32,
    pub proved: bool,
    /// The whole line picked, from the decision, past the turns its plan stops at.
    pub line: TurnEnd,
    /// For a losing line, the value of the turn start it died from.
    pub from: f64,
}

impl Plan {
    /// The plan's steps: its first action, then the rest.
    pub fn steps(&self) -> Vec<Action> {
        std::iter::once(self.first.clone()).chain(self.plan.iter().cloned()).collect()
    }
}

/// C#: SlPlanner.Picked (PlanOf.Found). The plan of the line picked, with the best line after each first action as the
/// candidates. A loss the search may have been wrong about (not proved) is played `replan` turns at a time.
pub fn picked(found: Found, replan: i32) -> Plan {
    let pick = found.pick;
    let first = pick.actions[0].clone();
    let mut lines = found.by_first;
    // C#: OrderByDescending(the pick's first).ThenByDescending(Value), stable sorts.
    lines.sort_by(|a, b| (b.actions[0] == first).cmp(&(a.actions[0] == first)).then_with(|| beam::compare(b.value, a.value)));
    let candidates = lines
        .iter()
        .map(|line| Candidate {
            action: line.actions[0].text(),
            value: line.value,
            win: round(line.win, 4),
            hp: round(line.hp, 1),
        })
        .collect();
    let mut played = pick.actions.len();
    if pick.status == Status::Lost && !found.proved && replan > 0 {
        let mut ends = 0;
        for (i, action) in pick.actions.iter().enumerate() {
            if action.is_end_turn() {
                ends += 1;
                if ends == replan {
                    played = i + 1;
                    break;
                }
            }
        }
    }
    Plan {
        first,
        plan: pick.actions[1..played].to_vec(),
        plan_hashes: pick.hashes[1..played].to_vec(),
        candidates,
        value: pick.value,
        end: pick.status,
        nodes: found.nodes,
        turns: found.turns,
        proved: found.proved,
        from: if pick.status == Status::Lost { found.from } else { 0.0 },
        line: pick,
    }
}
