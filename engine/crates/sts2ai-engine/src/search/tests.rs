//! The search's tests on toy combats (`toy`), ported from the C# tests of the C# search when it was deleted (C#:
//! SlPlannerTests and LoopSearchTests, in git's history): the same worlds, the same asserts, the same numbers. The C#
//! tests of what stays in C# (the budgets, the routes, PIMC) are not here.

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use super::beam::{self, Budget, Job, UNLIMITED};
use super::bound::{self, Bounds};
use super::leaf::{Leaf, LeafOutcome, LeafState};
use super::loops;
use super::order;
use super::pimc;
use super::solver::Solver;
use super::state::{self, Action, History, State, Status};
use super::toy::{Asked, Disliking, Loop, Plan, Prompted, Replay, Rules, Toy, ToyLeaf, jobs, picked, round, slugs, toy};
use super::turn::{self, TurnEnd, TurnSearch};
use crate::protocol::{CardView, CombatObservation, PowerView, RelicView};
use crate::rt::Str;

// ---------------------------------------------------------------------------------------------------------------------
// What the tests share (C#: SlPlannerTests' helpers)
// ---------------------------------------------------------------------------------------------------------------------

fn none() -> Arc<History> {
    Arc::new(Vec::new())
}

/// C#: `new Budget(NodesPerWorld: nodes, Beam: beam)`.
fn budget(nodes: i32, beam: i32) -> Budget {
    Budget {
        nodes_per_world: nodes,
        beam,
        easy_beam: 0,
        turn_decay: 1.0,
    }
}

/// C#: xUnit's `Assert.Equal(expected, actual, precision)`: the two the same to `digits` decimals.
fn assert_near(expected: f64, actual: f64, digits: i32) {
    assert_eq!(round(expected, digits), round(actual, digits), "{expected} is not {actual} to {digits} decimals");
}

/// The state after `actions` from `state`.
fn played(mut state: State, actions: &[Action]) -> State {
    for action in actions {
        state = state.step(action).unwrap_or_else(|e| panic!("{e}"));
    }
    state
}

fn text(actions: &[Action]) -> String {
    actions.iter().map(|a| a.text()).collect::<Vec<_>>().join(" ")
}

/// C#: `Leaf.Outcome(world.Status, world.Observe(), world.Run(), [])`.
fn outcome(state: &mut State) -> LeafOutcome {
    let asked = LeafState {
        status: state.status,
        obs: state.obs.clone(),
        run: state.run(),
        history: none(),
    };
    ToyLeaf.outcome(&asked).expect("the toy's leaf")
}

/// C#: SlPlanner.SearchTurn from the state, with no record of enemy moves.
fn search(state: &mut State, leaf: &dyn Leaf, nodes: i32, keep: usize, bounds: &Bounds) -> TurnSearch {
    turn::search(state, &none(), nodes, keep, bounds, leaf).expect("the turn's search")
}

/// C#: SlPlanner.Plan, the plan of an unproved loss stopping after `replan` turns.
fn planned_with(world: &State, leaf: &dyn Leaf, budget: &Budget, replan: i32) -> Plan {
    let searcher = Replay {
        root: world.copy(),
        leaf,
        bounds: true,
    };
    picked(beam::search(budget, none(), &searcher).expect("a plan"), replan)
}

/// C#: SlPlanner.Plan with the budget's default replan, 1.
fn planned(world: &State, leaf: &dyn Leaf, budget: &Budget) -> Plan {
    planned_with(world, leaf, budget, 1)
}

/// C#: BruteForce. The best end of the combat by trying every line; each state once, as the toy shows all of it but the
/// draw.
fn brute_force(state: &mut State, known: &mut HashMap<(u64, u64), f64>) -> f64 {
    if state.status != Status::InProgress {
        return outcome(state).value;
    }
    let key = (state.hash, state.hidden_key);
    if let Some(&value) = known.get(&key) {
        return value;
    }
    let mut best = f64::NEG_INFINITY;
    for action in state.legal() {
        let mut after = state.step(&action).expect("a legal action");
        best = best.max(brute_force(&mut after, known));
    }
    known.insert(key, best);
    best
}

/// C#: Best. A plan's value from its search's toy after `gone`, by trying every line.
fn best(gone: &[Action], enemy_hp: i32, retain: bool) -> f64 {
    let mut world = played(Toy::new(enemy_hp, None, retain).state(), gone);
    brute_force(&mut world, &mut HashMap::new())
}

/// C#: AssertReplays. Plays the plan from the start, or after `gone`: each step at the observation it expects, to the
/// end it claims.
fn assert_replays(plan: &Plan, enemy_hp: i32, gone: &[Action], retain: bool) {
    let mut world = played(Toy::new(enemy_hp, None, retain).state(), gone);
    let steps = plan.steps();
    assert_eq!(steps.len() - 1, plan.plan_hashes.len());
    for (i, step) in steps.iter().enumerate() {
        if i > 0 {
            assert_eq!(plan.plan_hashes[i - 1], world.hash);
        }
        world = world.step(step).expect("a step of the plan");
    }
    // A losing plan is played for its first turn only, unless proved; InProgress checks the steps alone.
    if plan.end != Status::Won && !(plan.end == Status::Lost && plan.proved) {
        return;
    }
    assert_eq!(plan.end, world.status);
    assert_near(plan.value, outcome(&mut world).value, 9);
}

/// C#: ToySearcher. A solver's turn searches, each in a toy of its own where the combat stands after `gone`.
fn toy_searcher(gone: &[Action], enemy_hp: i32, retain: bool) -> Replay<'static> {
    Replay {
        root: played(Toy::new(enemy_hp, None, retain).state(), gone),
        leaf: &ToyLeaf,
        bounds: true,
    }
}

/// C#: Moves. The record of enemy moves after `gone`, as a decision there has it: each round's intents from the second
/// on, as the planner's lines record them from a root with none.
fn moves(gone: &[Action]) -> Arc<History> {
    let mut world = toy(24);
    let mut record = Vec::new();
    for action in gone {
        world = world.step(action).expect("a legal action");
        if world.status == Status::InProgress && world.round() != 1 {
            record = state::witness(&record, &world.obs);
        }
    }
    Arc::new(record)
}

fn solver(batch: usize) -> Solver {
    Solver::new(none(), 1.0, batch)
}

fn result(solver: &Solver) -> Plan {
    picked(solver.result().expect("a pick"), 1)
}

fn is_potion(action: &Action) -> bool {
    matches!(action, Action::Potion { .. })
}

// ---------------------------------------------------------------------------------------------------------------------
// The planner (C#: SlPlannerTests)
// ---------------------------------------------------------------------------------------------------------------------

#[test]
fn a_full_search_finds_the_best_line() {
    let mut world = toy(24);
    let best = brute_force(&mut world, &mut HashMap::new());
    let plan = planned(&world, &ToyLeaf, &budget(100_000, 1000));

    assert!(plan.proved);
    assert_eq!(Status::Won, plan.end);
    assert_near(best, plan.value, 9);
    assert_replays(&plan, 24, &[], false);
}

/// C#: TurnEnds. Every state the toy's current turn can end at, by trying every line from the state: the next turn's
/// states by key, and the ends of the combat by status.
fn turn_ends(state: &mut State, round: i32, seen: &mut HashSet<(u64, u64)>, next: &mut HashSet<(u64, u64)>, over: &mut HashSet<Status>) {
    for action in state.legal() {
        let mut after = state.step(&action).expect("a legal action");
        let key = (after.state_key, after.hidden_key);
        if after.status != Status::InProgress {
            over.insert(after.status);
        } else if after.round() != round {
            next.insert(key);
        } else if seen.insert(key) {
            turn_ends(&mut after, round, seen, next, over);
        }
    }
}

#[test]
fn an_unlimited_turn_search_ends_at_every_state_the_turn_can_reach() {
    let mut expected = HashSet::new();
    let mut over = HashSet::new();
    turn_ends(&mut toy(24), 1, &mut HashSet::new(), &mut expected, &mut over);
    let narrow = search(&mut toy(24), &ToyLeaf, 4, usize::MAX, &Bounds::new());
    let found = search(&mut toy(24), &ToyLeaf, UNLIMITED, usize::MAX, &Bounds::new());

    // Every end, each once, and nothing cut: a budget of 4 nodes reaches fewer.
    assert!(!found.cut);
    assert!(narrow.cut);
    assert!(over.is_empty());
    let reached: Vec<(u64, u64)> = found
        .ends
        .iter()
        .map(|e| {
            let world = played(toy(24), &e.actions);
            (world.state_key, world.hidden_key)
        })
        .collect();
    assert_eq!(reached.len(), reached.iter().collect::<HashSet<_>>().len());
    assert_eq!(expected, reached.into_iter().collect::<HashSet<_>>());
    assert!(narrow.ends.len() < found.ends.len());
}

#[test]
fn the_leaf_is_asked_about_a_turns_ends_together() {
    let asked = Asked::default();
    let found = search(&mut toy(24), &asked, 100_000, usize::MAX, &Bounds::new());

    // One question a turn's search, about every end it found; no end is valued by itself.
    assert_eq!(1, asked.together.borrow().len());
    assert!(asked.together.borrow()[0] >= found.ends.len());
    assert_eq!(0, asked.alone.get());

    // The plan is the one a leaf asked one state at a time gives.
    let plan = planned(&toy(24), &Asked::default(), &budget(100_000, 1000));
    let plain = planned(&toy(24), &ToyLeaf, &budget(100_000, 1000));
    assert_eq!((plain.value, plain.end, plain.nodes, &plain.first), (plan.value, plan.end, plan.nodes, &plan.first));
    assert_eq!(plain.plan, plan.plan);
}

#[test]
fn each_candidate_shows_how_its_best_line_ends() {
    let plan = planned(&toy(24), &ToyLeaf, &budget(100_000, 1000));

    let chosen = &plan.candidates[0];
    assert_eq!(plan.first.text(), chosen.action);
    assert_eq!(1.0, chosen.win);
    let end = played(toy(24), &plan.steps());
    assert_eq!(end.obs.player.hp as f64, chosen.hp);
    // Every line here reaches the combat's end: a win with its HP, or a loss.
    for c in &plan.candidates {
        assert!(if c.win == 1.0 { c.hp > 0.0 } else { c.win == 0.0 && c.hp == 0.0 });
    }
}

#[test]
fn a_narrow_beam_proves_nothing_and_still_plays_its_line() {
    let plan = planned(&toy(24), &ToyLeaf, &budget(4, 1));

    assert!(!plan.proved);
    assert_replays(&plan, 24, &[], false);
}

#[test]
fn a_losing_plan_is_played_for_one_turn() {
    let plan = planned(&toy(500), &ToyLeaf, &budget(100_000, 2));

    assert_eq!(Status::Lost, plan.end);
    assert!(!plan.proved);
    let steps = plan.steps();
    assert!(steps.last().expect("a step").is_end_turn());
    assert_eq!(1, steps.iter().filter(|a| a.is_end_turn()).count());
    assert_eq!(steps.len() - 1, plan.plan_hashes.len());
}

#[test]
fn a_losing_plan_is_played_for_its_replan_turns() {
    for replan in [2, 0] {
        let plan = planned_with(&toy(500), &ToyLeaf, &budget(100_000, 2), replan);

        assert_eq!(Status::Lost, plan.end);
        assert!(!plan.proved);
        let steps = plan.steps();
        let ends = steps.iter().filter(|a| a.is_end_turn()).count() as i32;
        if replan > 0 {
            assert_eq!(replan, ends);
        } else {
            assert!(ends > 2);
        }
        // Played to its end, the plan dies where it claims.
        if replan == 0 {
            assert_eq!(Status::Lost, played(toy(500), &steps).status);
        }
    }
}

#[test]
fn a_turn_searched_in_full_narrows_to_the_search_with_a_keep() {
    for keep in [1, 2, 3, 1000] {
        for path in [Vec::new(), vec![Action::EndTurn]] {
            let searched = |k: usize| search(&mut played(toy(24), &path), &ToyLeaf, 200, k, &Bounds::new());
            let direct = searched(keep);
            let narrowed = beam::narrow(&searched(usize::MAX), keep);
            assert_eq!((direct.nodes, direct.cut), (narrowed.nodes, narrowed.cut));
            let ends = |found: &TurnSearch| -> Vec<(String, Status, f64, u64)> {
                found.ends.iter().map(|e| (text(&e.actions), e.status, e.value, e.key)).collect()
            };
            assert_eq!(ends(&direct), ends(&narrowed));
        }
    }
}

#[test]
fn the_full_beam_takes_the_dives_turns_instead_of_searching_them_again() {
    let root = toy(24);
    let searched = RefCell::new(Vec::<String>::new());
    let found = beam::search(
        &budget(100_000, 4),
        none(),
        &jobs(|jobs: &[Job], nodes, keep| {
            jobs.iter()
                .map(|job| {
                    searched.borrow_mut().push(text(&job.path));
                    turn::search(&mut played(root.copy(), &job.path), &job.history, nodes, keep, &Bounds::new(), &ToyLeaf)
                })
                .collect()
        }),
    )
    .expect("a plan");
    let plan = picked(found, 1);

    let searched = searched.into_inner();
    assert_eq!(1, searched.iter().filter(|s| s.is_empty()).count());
    assert_eq!(searched.len(), searched.iter().collect::<HashSet<_>>().len());
    assert_near(planned(&toy(24), &ToyLeaf, &budget(100_000, 4)).value, plan.value, 9);
    assert_replays(&plan, 24, &[], false);
}

#[test]
fn when_the_dive_wins_the_full_beam_keeps_the_easy_beam() {
    let easy = planned(&toy(24), &ToyLeaf, &Budget { easy_beam: 2, ..budget(100_000, 1000) });
    let narrow = planned(&toy(24), &ToyLeaf, &budget(100_000, 2));

    assert_eq!(Status::Won, easy.end);
    assert_eq!((narrow.value, narrow.nodes, narrow.turns), (easy.value, easy.nodes, easy.turns));
    assert_eq!(narrow.plan, easy.plan);
}

#[test]
fn when_the_dive_loses_the_full_beam_keeps_the_beam() {
    let losing = planned(&toy(500), &ToyLeaf, &Budget { easy_beam: 2, ..budget(100_000, 1000) });
    let full = planned(&toy(500), &ToyLeaf, &budget(100_000, 1000));

    assert_eq!((full.value, full.nodes, full.turns, full.proved), (losing.value, losing.nodes, losing.turns, losing.proved));
}

/// C#: SlPlanner.PlayerTurnDecay.
const PLAYER_TURN_DECAY: f64 = 0.999;

/// The dive follows play:1, the start the leaf values higher, to a win after two turns; play:2's win comes after one,
/// worth `below` less. Without a turn decay only a win worth exactly the same takes the place of the slower one; with
/// one, a win worth less by no more than the turn takes does too. The plan's value is its end's.
#[test]
fn a_sooner_win_is_the_pick_on_an_exact_tie_or_within_the_turn_decay() {
    for (decay, below, pick) in [(1.0, 0.0, "play:2"), (1.0, 1e-12, "play:1"), (PLAYER_TURN_DECAY, 0.0005, "play:2"), (PLAYER_TURN_DECAY, 0.002, "play:1")] {
        let end = |status: Status, value: f64, key: u64, actions: Vec<Action>| TurnEnd {
            hashes: vec![key; actions.len()],
            actions,
            status,
            value,
            key,
            ..beam::start(none())
        };
        let play = |card: u32| Action::Play { card, target: None };
        let turn = |ends: Vec<TurnEnd>, nodes: i32| TurnSearch {
            ends,
            nodes,
            cut: false,
            pruned: 0,
            start: None,
        };
        let turns: HashMap<&str, TurnSearch> = HashMap::from([
            (
                "",
                turn(
                    vec![
                        end(Status::InProgress, 0.6, 1, vec![play(1), Action::EndTurn]),
                        end(Status::InProgress, 0.5, 2, vec![play(2), Action::EndTurn]),
                    ],
                    2,
                ),
            ),
            ("play:1 end_turn", turn(vec![end(Status::InProgress, 0.7, 3, vec![Action::EndTurn])], 1)),
            ("play:1 end_turn end_turn", turn(vec![end(Status::Won, 0.9, 0, vec![play(1)])], 1)),
            ("play:2 end_turn", turn(vec![end(Status::Won, 0.9 - below, 0, vec![play(2)])], 1)),
        ]);
        let budget = Budget {
            beam: 2,
            turn_decay: decay,
            ..budget(300, 2)
        };
        let found = beam::search(
            &budget,
            none(),
            &jobs(|jobs: &[Job], _, keep| Ok(jobs.iter().map(|job| beam::narrow(&turns[text(&job.path).as_str()], keep)).collect())),
        )
        .expect("a plan");
        let plan = picked(found, 1);

        assert_eq!(Status::Won, plan.end);
        assert_eq!(pick, plan.first.text());
        assert_eq!(if pick == "play:2" { 0.9 - below } else { 0.9 }, plan.value);
        assert_eq!(plan.value, plan.candidates[0].value);
    }
}

#[test]
fn a_potion_that_changes_nothing_is_not_drunk() {
    // The toy's potion does nothing, and the leaf values every state holding it lower: drinking it for nothing would
    // win each turn's end. It is thrown away for nothing as the game allows, so the end that keeps it is worth as
    // much, and comes first.
    let world = Toy::new(24, Some("NOTHING"), false).state();
    let found = search(&mut world.copy(), &Disliking, 100_000, usize::MAX, &Bounds::new());
    let going = |e: &&TurnEnd| e.status == Status::InProgress;
    let kept: Vec<&TurnEnd> = found.ends.iter().filter(going).filter(|e| !e.actions.iter().any(is_potion)).collect();
    let drunk: Vec<&TurnEnd> = found.ends.iter().filter(going).filter(|e| e.actions.iter().any(is_potion)).collect();
    assert!(!drunk.is_empty());
    for d in &drunk {
        let without: Vec<Action> = d.actions.iter().filter(|a| !is_potion(a)).cloned().collect();
        assert!(kept.iter().any(|k| k.value == d.value && k.actions == without));
    }
    let plan = planned(&world, &Disliking, &budget(100_000, 1000));
    assert_eq!(Status::Won, plan.end);
    assert!(!plan.steps().iter().any(is_potion));
}

#[test]
fn a_proved_loss_is_played_to_the_end() {
    let plan = planned(&toy(500), &ToyLeaf, &budget(100_000, 1000));

    assert!(plan.proved);
    assert_eq!(Status::Lost, plan.end);
    assert!(plan.plan.iter().any(|a| a.is_end_turn()));
    assert_replays(&plan, 500, &[], false);
}

#[test]
fn its_candidates_start_with_its_pick() {
    let plan = planned(&toy(24), &ToyLeaf, &budget(100_000, 1000));
    assert_eq!(plan.first.text(), plan.candidates[0].action);
    assert_near(plan.value, plan.candidates[0].value, 9);
}

#[test]
fn a_record_goes_on_from_its_history() {
    let record = state::witness(&Vec::new(), &slugs());
    let copy = state::witness(&state::witness(&record, &slugs()), &CombatObservation { round: 2, ..slugs() });

    let rounds = |e: &crate::protocol::EnemyTurns| e.turns.iter().map(|t| t.round).collect::<Vec<_>>();
    assert!(copy.iter().all(|e| rounds(e) == [1, 2]));
    assert!(record.iter().all(|e| rounds(e) == [1]));
}

// ---------------------------------------------------------------------------------------------------------------------
// The solver (C#: SlPlannerTests' SlSolver tests)
// ---------------------------------------------------------------------------------------------------------------------

#[test]
fn the_solver_searched_to_the_end_picks_the_best_line_and_proves_it() {
    for enemy_hp in [24, 500] {
        let mut solver = solver(16);
        solver.run(&toy_searcher(&[], enemy_hp, false), i64::MAX).expect("a search");
        let plan = result(&solver);

        assert!(plan.proved);
        assert_eq!(0, solver.left());
        assert_eq!(if enemy_hp == 24 { Status::Won } else { Status::Lost }, plan.end);
        assert_near(best(&[], enemy_hp, false), plan.value, 9);
        assert_replays(&plan, enemy_hp, &[], false);
    }
}

#[test]
fn the_solver_stopped_anywhere_is_no_worse_than_the_beam_it_starts_from() {
    let beam = planned(&toy(24), &ToyLeaf, &budget(10, 1));
    let best = best(&[], 24, false);
    // The beam wins, not with the best line: 0.52 of 0.88.
    assert_eq!(Status::Won, beam.end);
    assert!(beam.value < best - 1e-9);

    let mut solver = solver(1);
    solver.seed(beam.line.clone(), beam.from);
    let mut last = beam.value;
    let mut stops = 0;
    while !solver.proved() {
        solver.run(&toy_searcher(&[], 24, false), 10).expect("a search");
        let plan = result(&solver);
        assert_eq!(Status::Won, plan.end);
        assert!(plan.value >= last - 1e-12);
        last = plan.value;
        assert_replays(&plan, 24, &[], false);
        stops += 1;
    }
    assert!(stops > 2);
    assert_near(best, last, 9);
}

#[test]
fn the_same_nodes_give_the_same_plan() {
    let plans: Vec<(Plan, i64, usize)> = (0..2)
        .map(|_| {
            let mut solver = solver(4);
            solver.run(&toy_searcher(&[], 24, false), 100).expect("a search");
            (result(&solver), solver.nodes(), solver.states())
        })
        .collect();

    assert_eq!(
        (&plans[0].0.first, plans[0].0.value, plans[0].1, plans[0].2),
        (&plans[1].0.first, plans[1].0.value, plans[1].1, plans[1].2)
    );
    assert_eq!(plans[0].0.line.actions, plans[1].0.line.actions);
    assert!(!plans[0].0.proved);
}

#[test]
fn the_solvers_root_moves_with_the_combat() {
    let mut first = solver(16);
    first.run(&toy_searcher(&[], 24, false), i64::MAX).expect("a search");
    let plan = result(&first);
    let line = plan.line.actions.clone();
    let turn = line.iter().position(|a| a.is_end_turn()).map_or(0, |i| i + 1);

    // Along the plan, into the turn and then past its end: all below was searched, so only the root's turn is again.
    for count in [1, turn] {
        let gone = &line[..count];
        let mut moved = solver(16);
        moved.run(&toy_searcher(&[], 24, false), i64::MAX).expect("a search");
        let searched = moved.searched();
        moved.advance(gone, moves(gone), &toy_searcher(gone, 24, false)).expect("the root's turn");

        assert_eq!(searched + 1, moved.searched());
        assert!(moved.proved());
        let after = result(&moved);
        assert_near(plan.value, after.value, 9);
        assert_near(best(gone, 24, false), after.value, 9);
        assert_replays(&after, 24, gone, false);
    }

    // Off the plan: the solver searches on from there, to the best line there.
    let mut other = solver(16);
    other.run(&toy_searcher(&[], 24, false), i64::MAX).expect("a search");
    let off = vec![toy(24).legal().into_iter().find(|a| *a != line[0]).expect("another action")];
    other.advance(&off, moves(&off), &toy_searcher(&off, 24, false)).expect("the root's turn");
    other.run(&toy_searcher(&off, 24, false), i64::MAX).expect("a search");
    let there = result(&other);
    assert!(there.proved);
    assert_near(best(&off, 24, false), there.value, 9);
    assert_replays(&there, 24, &off, false);
}

#[test]
fn the_solvers_states_stay_on_lines_that_hold_the_same_copies_of_a_card() {
    // Cards left in hand stay: a turn that plays the first Strike and one that plays the second end in the same state
    // by its key, holding the other copy, under its own uid. After the second, a state its turn was searched at by a
    // line that played the first is searched again, so the plan plays the cards held.
    const HP: i32 = 18;
    let mut solver = solver(16);
    solver.run(&toy_searcher(&[], HP, true), i64::MAX).expect("a search");
    let strikes: Vec<CardView> = Toy::new(HP, None, true).state().obs.hand.iter().filter(|c| c.id.as_str() == "S").cloned().collect();
    assert_eq!(2, strikes.len());
    let gone = vec![Action::Play {
        card: strikes[1].uid.expect("a uid"),
        target: Some(1),
    }];
    solver.advance(&gone, moves(&gone), &toy_searcher(&gone, HP, true)).expect("the root's turn");
    solver.run(&toy_searcher(&gone, HP, true), i64::MAX).expect("a search");
    let plan = result(&solver);

    assert!(plan.proved);
    assert_near(best(&gone, HP, true), plan.value, 9);
    assert_replays(&plan, HP, &gone, true);
}

#[test]
fn the_solvers_seed_stays_only_where_the_combat_went_its_way() {
    let beam = planned(&toy(24), &ToyLeaf, &budget(10, 1));
    let line = beam.line.actions.clone();
    let along = vec![line[0].clone()];
    let off = vec![toy(24).legal().into_iter().find(|a| *a != line[0]).expect("another action")];

    // Searched no further than the root's turn, the pick is the seed's where it went, and the solver's own elsewhere.
    let mut solver = solver(1);
    solver.seed(beam.line.clone(), beam.from);
    solver.advance(&along, moves(&along), &toy_searcher(&along, 24, false)).expect("the root's turn");
    assert_eq!(line[1..].to_vec(), result(&solver).line.actions);

    let mut elsewhere = self::solver(1);
    elsewhere.seed(beam.line.clone(), beam.from);
    elsewhere.advance(&off, moves(&off), &toy_searcher(&off, 24, false)).expect("the root's turn");
    let plan = result(&elsewhere);
    assert_ne!(line[1..].to_vec(), plan.line.actions);
    assert_replays(&plan, 24, &off, false);
}

// ---------------------------------------------------------------------------------------------------------------------
// The bound (C#: SlPlannerTests' LifeBound tests)
// ---------------------------------------------------------------------------------------------------------------------

#[test]
fn reach_adds_every_heal_and_max_hp_the_state_still_holds() {
    // 30 of 80 HP, nothing that heals: the HP and max HP now.
    let mut bare = slugs();
    bare.player.hp = 30;
    bare.player.max_hp = 80;
    bare.player.powers = Vec::new();
    (bare.hand, bare.draw, bare.discard, bare.exhaust) = (Vec::new(), Vec::new(), Vec::new(), Vec::new());
    bare.potions = vec![None, None, None];
    bare.relics = Vec::new();
    let relic = |id: &'static str, status: Option<&'static str>| RelicView {
        id: Str::lit(id),
        status: status.map(Str::lit),
        ..RelicView::default()
    };
    let card = |id: &'static str, exhausts: bool, up: Option<i32>| CardView {
        id: Str::lit(id),
        up,
        keywords: exhausts.then(|| vec![Str::lit("Exhaust")]),
        ..CardView::default()
    };
    let reach = |change: &dyn Fn(&mut CombatObservation)| {
        let mut obs = bare.clone();
        change(&mut obs);
        bound::reach(&obs)
    };
    let potion = |id: &'static str| vec![Some(Str::lit(id)), None, None];
    assert_eq!(110, bound::reach(&bare));
    assert_eq!(116, reach(&|o| o.relics = vec![relic("BURNING_BLOOD", None)]));
    // A heal fills the HP up to the max HP and no further.
    assert_eq!(
        160,
        reach(&|o| {
            o.player.hp = 75;
            o.relics = vec![relic("BLACK_BLOOD", None)];
        })
    );
    assert_eq!(122, reach(&|o| o.relics = vec![relic("MEAT_ON_THE_BONE", None)]));
    assert_eq!(111, reach(&|o| o.relics = vec![relic("CHOSEN_CHEESE", None)]));
    assert_eq!(150, reach(&|o| o.relics = vec![relic("LIZARD_TAIL", None)]));
    assert_eq!(110, reach(&|o| o.relics = vec![relic("LIZARD_TAIL", Some("Disabled"))]));
    // A potion heals when drunk, and Regen goes on healing after.
    assert_eq!(125, reach(&|o| o.potions = potion("REGEN_POTION")));
    assert_eq!(126, reach(&|o| o.potions = potion("BLOOD_POTION")));
    assert_eq!(150, reach(&|o| o.potions = potion("AMBERGRIS")));
    assert_eq!(134, reach(&|o| o.potions = potion("FAIRY_IN_A_BOTTLE")));
    assert_eq!(115, reach(&|o| o.potions = potion("FRUIT_JUICE")));
    assert_eq!(
        116,
        reach(&|o| {
            o.player.powers = vec![PowerView {
                id: Str::lit("REGEN_POWER"),
                amount: 3,
                ..PowerView::default()
            }]
        })
    );
    // A card that heals or gives max HP, in any pile, by its amount while it exhausts; no bound once it does not.
    assert_eq!(120, reach(&|o| o.draw = vec![card("NOT_YET", true, None)]));
    assert_eq!(123, reach(&|o| o.exhaust = vec![card("NOT_YET", true, Some(1))]));
    assert_eq!(113, reach(&|o| o.discard = vec![card("FEED", true, None)]));
    assert_eq!(i32::MAX, reach(&|o| o.hand = vec![card("NOT_YET", false, None)]));
    assert_eq!(i32::MAX, reach(&|o| o.hand = vec![card("FEED", false, None)]));
    // A gain with no amount to count: no bound.
    assert_eq!(i32::MAX, reach(&|o| o.relics = vec![relic("DRAGON_FRUIT", None)]));
}

#[test]
fn a_turns_search_stops_the_lines_below_the_bound_of_their_potions() {
    // The toy starts at 20 of 20 and heals nothing: with a bound of 40 for no potions, only the ends that took no
    // damage stay.
    let bounds: Bounds = HashMap::from([(String::new(), 40)]);
    let all = search(&mut toy(24), &ToyLeaf, UNLIMITED, usize::MAX, &Bounds::new());
    let bound = search(&mut toy(24), &ToyLeaf, UNLIMITED, usize::MAX, &bounds);

    assert_eq!(0, all.pruned);
    assert!(bound.pruned > 0);
    assert!(!bound.ends.is_empty());
    assert!(bound.ends.iter().all(|e| e.reach >= 40));
    assert!(all.ends.iter().any(|e| e.reach < 40));
    assert!(all.ends.iter().all(|e| e.life == e.reach));

    // A state that still holds a potion is held to its own potions' bound, none here.
    let held = search(&mut Toy::new(24, Some("SWIFT_POTION"), false).state(), &ToyLeaf, UNLIMITED, usize::MAX, &bounds);
    assert!(held.ends.iter().any(|e| e.potions == "SWIFT_POTION" && e.reach < 40));
    assert!(held.ends.iter().all(|e| !e.potions.is_empty() || e.reach >= 40));

    // The solver's bound is its wins' HP plus max HP by their potions, and its pick still the best there is.
    let mut solver = solver(16);
    solver.run(&toy_searcher(&[], 24, false), i64::MAX).expect("a search");
    let plan = result(&solver);
    assert!(plan.proved);
    assert_eq!(Some(&plan.line.life), solver.bounds().get(&plan.line.potions));
    assert_near(best(&[], 24, false), plan.value, 9);
}

// ---------------------------------------------------------------------------------------------------------------------
// Cycles and prompts in the planner (C#: LoopSearchTests)
// ---------------------------------------------------------------------------------------------------------------------

const R: Action = Action::Play { card: 1, target: None };
const H: Action = Action::Play { card: 2, target: Some(1) };
const POTION: Action = Action::Potion { slot: 0, target: None };

/// C#: LoopSearchTests.Plan.
fn loop_plan(rules: Rules) -> Plan {
    planned(&Loop::new(rules).state(), &ToyLeaf, &budget(300, 8))
}

/// C#: LoopSearchTests.Replayed. Plays the line on a new world, each step at the observation it expects.
fn replayed(plan: &Plan, rules: Rules) -> State {
    let mut world = Loop::new(rules).state();
    for (i, step) in plan.steps().iter().enumerate() {
        if i > 0 {
            assert_eq!(plan.plan_hashes[i - 1], world.hash);
        }
        world = world.step(step).expect("a step of the plan");
    }
    world
}

fn count(line: &[Action], action: &Action) -> usize {
    line.iter().filter(|a| *a == action).count()
}

#[test]
fn the_cycle_is_repeated_until_it_kills_in_one_turn() {
    // 44 strength and H: 50 damage. A plain search spends its 300 nodes on R and never gets to H.
    let plan = loop_plan(Rules::new(50));

    assert_eq!(Status::Won, plan.end);
    assert!(!plan.steps().contains(&Action::EndTurn));
    let mut line = vec![R; 44];
    line.push(H);
    assert_eq!(line, plan.steps());
    assert_eq!(Status::Won, replayed(&plan, Rules::new(50)).status);
}

#[test]
fn what_the_cycle_does_to_the_piles_does_not_stop_it() {
    let plan = loop_plan(Rules { junk_every: 6, ..Rules::new(50) });

    assert_eq!(Status::Won, plan.end);
    assert!(!plan.steps().contains(&Action::EndTurn));
    assert_eq!(44, count(&plan.steps(), &R));
}

#[test]
fn a_cycle_ends_where_the_game_ends_it() {
    // A full hand sends R to the discard pile: 4 junk in a hand of 6 is 12 plays. Lethal before that, and not after it.
    let plan = loop_plan(Rules {
        junk_every: 3,
        hand_cap: 6,
        ..Rules::new(15)
    });
    assert_eq!(Status::Won, plan.end);
    assert!(!plan.steps().contains(&Action::EndTurn));

    let line = loop_plan(Rules {
        junk_every: 3,
        hand_cap: 6,
        ..Rules::new(10_000)
    })
    .steps();
    let end = line.iter().position(|a| a.is_end_turn()).expect("the turn's end");
    assert!(line[..end].contains(&H));
    assert_eq!(15, count(&line[..end], &R));
}

#[test]
fn a_card_that_runs_out_is_an_ordinary_line() {
    // R stops coming back after its third play, too soon to be taken for a cycle, and the search plays it out as always.
    let plan = loop_plan(Rules { runs_out: 3, ..Rules::new(9) });

    assert_eq!(Status::Won, plan.end);
    assert!(!plan.steps().contains(&Action::EndTurn));
    assert_eq!(vec![R, R, R, H], plan.steps());
}

#[test]
fn cards_that_use_themselves_up_are_an_ordinary_line() {
    // Each R is exhausted and draws the next of 8, as a Soul does: one fewer is left each time, so it is no cycle.
    let souls = Rules { souls: 8, ..Rules::new(15) };
    let mut cycle = Loop::new(Rules::new(15)).state();
    let mut used = Loop::new(souls).state();
    let (cycled, drawn) = (cycle.step(&R).expect("R"), used.step(&R).expect("R"));
    assert!(loops::recurs(&cycle.obs, &cycled.obs));
    assert!(!loops::recurs(&used.obs, &drawn.obs));

    // The search plays the nine R and H out on its nodes.
    let plan = loop_plan(souls);
    assert_eq!(Status::Won, plan.end);
    assert!(!plan.steps().contains(&Action::EndTurn));
}

#[test]
fn a_power_that_counts_down_the_returns_is_an_ordinary_line() {
    // R comes back eight times, more than the probe repeats, and each return takes one off the count its power shows:
    // a chain that spends the power, not a cycle.
    let rules = Rules { returns: Some(8), ..Rules::new(15) };
    let mut world = Loop::new(rules).state();
    let after = world.step(&R).expect("R");
    assert!(after.obs.hand.iter().any(|c| c.id.as_str() == "R"));
    assert!(!loops::recurs(&world.obs, &after.obs));

    // The search plays the nine R and H out on its nodes: 6 plus 9 strength.
    let plan = loop_plan(rules);
    assert_eq!(Status::Won, plan.end);
    let mut line = vec![R; 9];
    line.push(H);
    assert_eq!(line, plan.steps());
}

#[test]
fn a_cycle_with_no_lethal_stays_on_its_budget() {
    let world = Loop::new(Rules::new(1_000_000));
    let count = world.played();
    let plan = planned(&world.state(), &ToyLeaf, &budget(300, 1));

    assert_ne!(Status::Won, plan.end);
    let most = plan.turns as usize * (2 * loops::MOVES as usize + 400);
    assert!((1..=most).contains(&count.get()), "{} actions played, at most {most}", count.get());
}

#[test]
fn another_answer_to_a_prompt_that_opens_a_cycle_is_taken() {
    let rules = Rules {
        prompted: Prompted::GivesR,
        ..Rules::new(50)
    };
    let plan = loop_plan(rules);

    // Plan.Turns counts the turns the beam looks at past the win too.
    assert_eq!(Status::Won, plan.end);
    assert!(!plan.steps().contains(&Action::EndTurn));
    assert_eq!(vec![POTION, Action::pick(vec![2])], plan.steps()[..2].to_vec());
    assert_eq!(Status::Won, replayed(&plan, rules).status);
}

#[test]
fn every_answer_to_a_prompt_is_searched() {
    // The third answer wins at once; the first loses the fight.
    let rules = Rules {
        prompted: Prompted::GivesStrike,
        ..Rules::new(50)
    };
    let plan = loop_plan(rules);

    assert_eq!(Status::Won, plan.end);
    assert!(plan.steps().contains(&Action::pick(vec![2])));
    assert_eq!(Status::Won, replayed(&plan, rules).status);
}

#[test]
fn every_prompt_of_an_action_is_searched() {
    // The potion asks twice, and only the third answer to its second prompt wins.
    let rules = Rules {
        prompted: Prompted::AsksTwice,
        ..Rules::new(50)
    };
    let plan = loop_plan(rules);

    assert_eq!(Status::Won, plan.end);
    let line = plan.steps();
    let potion = line.iter().position(|a| *a == POTION).expect("the potion");
    assert!(matches!(line[potion + 1], Action::Pick(_)));
    assert_eq!(Action::pick(vec![2]), line[potion + 2]);
    assert_eq!(Status::Won, replayed(&plan, rules).status);
}

#[test]
fn an_unlimited_turn_search_at_a_prompt_tries_every_answer() {
    // The turn's search starts at the first of two prompts, three answers each; only the second prompt's third answer
    // wins (the others change nothing, so they are one state). Unlimited, the search tries every answer of both and
    // finds the win; 2 nodes split over the first prompt leave the second one answer, its first.
    let world = Loop::new(Rules {
        prompted: Prompted::AsksTwice,
        ..Rules::new(50)
    })
    .state()
    .step(&POTION)
    .expect("the potion");
    let (answers, cut) = order::answers(world.obs.prompt.as_ref().expect("a prompt"), UNLIMITED as i64);
    assert_eq!(3, answers.len());
    assert!(!cut);
    let found = search(&mut world.copy(), &ToyLeaf, UNLIMITED, usize::MAX, &Bounds::new());
    assert!(!found.cut);
    assert!(found.ends.iter().any(|e| e.status == Status::Won && e.actions[1] == Action::pick(vec![2])));
    let narrow = search(&mut world.copy(), &ToyLeaf, 2, usize::MAX, &Bounds::new());
    assert!(narrow.cut);
    assert!(!narrow.ends.iter().any(|e| e.status == Status::Won));
}

// ---------------------------------------------------------------------------------------------------------------------
// NoSL's search (C#: LoopSearchTests' PIMC tests and PimcMemoTests, in git's history)
// ---------------------------------------------------------------------------------------------------------------------

/// C#: PimcBrain.Root and Merge, as EnginePimc merges the engine's lines: the first actions by their mean over the
/// worlds, the first of the best; the plan is its line in the first world, with the hashes where every world agrees.
struct Merged {
    first: Action,
    plan: Vec<Action>,
    plan_hashes: Option<Vec<u64>>,
    repeats: bool,
}

/// C#: LoopSearchTests.Pimc. `worlds` worlds of the toy `make` makes (a toy draws nothing), each searched with 100
/// nodes shared over the first actions.
fn pimc_of(worlds: usize, make: &dyn Fn() -> State, leaf: &dyn Leaf) -> Merged {
    let mut world = make();
    let first = order::ordered(&world.legal(), &world.obs);
    let asked = pimc::Asked {
        history: Vec::new(),
        nodes_per_first: (100 / first.len() as i32).max(1),
        first: first.clone(),
        worlds,
        seed: 0,
    };
    let lines: Vec<Vec<pimc::Line>> = (0..worlds).map(|_| pimc::world_lines(make(), &asked, &none(), leaf).expect("a world's lines")).collect();
    let mean = |a: usize| lines.iter().map(|l| l[a].value).sum::<f64>() / worlds as f64;
    let mut order: Vec<usize> = (0..first.len()).collect();
    order.sort_by(|&a, &b| mean(b).partial_cmp(&mean(a)).unwrap_or(std::cmp::Ordering::Equal));
    let line = &lines[0][order[0]];
    let agreed = lines.iter().all(|l| l[order[0]].actions == line.actions && l[order[0]].hashes == line.hashes);
    Merged {
        first: first[order[0]].clone(),
        plan: line.actions[1..].to_vec(),
        plan_hashes: agreed.then(|| line.hashes[1..].to_vec()),
        repeats: line.repeats,
    }
}

fn pimc_loop(worlds: usize, rules: Rules) -> Merged {
    pimc_of(worlds, &|| Loop::new(rules).state(), &ToyLeaf)
}

/// C#: LoopSearchTests.Replayed. Plays the merged line on a new world, each step at the observation it expects.
fn pimc_replayed(merged: &Merged, rules: Rules) -> State {
    let mut world = Loop::new(rules).state();
    let steps: Vec<Action> = std::iter::once(merged.first.clone()).chain(merged.plan.iter().cloned()).collect();
    for (i, step) in steps.iter().enumerate() {
        if i > 0 {
            assert_eq!(merged.plan_hashes.as_ref().expect("the worlds agree")[i - 1], world.hash);
        }
        world = world.step(step).expect("a step of the line");
    }
    world
}

#[test]
fn every_answer_to_a_prompt_is_searched_by_pimc() {
    // The third answer wins at once; the first loses the fight.
    let merged = pimc_loop(2, Rules { prompted: Prompted::GivesStrike, ..Rules::new(50) });
    assert!(std::iter::once(&merged.first).chain(merged.plan.iter()).any(|a| *a == Action::pick(vec![2])));
}

#[test]
fn every_prompt_of_an_action_is_searched_by_pimc() {
    // The potion asks twice, and only the third answer to its second prompt wins.
    let rules = Rules { prompted: Prompted::AsksTwice, ..Rules::new(50) };
    let merged = pimc_loop(2, rules);
    assert_eq!(Status::Won, pimc_replayed(&merged, rules).status);
}

#[test]
fn pimc_repeats_the_cycle_as_well() {
    let merged = pimc_loop(2, Rules::new(50));

    assert!(merged.repeats);
    assert_eq!(R, merged.first);
    assert_eq!(44, count(&merged.plan, &R) + 1);
    assert_eq!(Status::Won, pimc_replayed(&merged, Rules::new(50)).status);
}

#[test]
fn pimc_takes_another_answer_that_opens_a_cycle() {
    let rules = Rules { prompted: Prompted::GivesR, ..Rules::new(50) };
    let merged = pimc_loop(2, rules);

    assert!(merged.repeats);
    assert_eq!(POTION, merged.first);
    assert_eq!(Action::pick(vec![2]), merged.plan[0]);
    assert_eq!(Status::Won, pimc_replayed(&merged, rules).status);
}

/// C#: PimcMemoTests.Rolls. A turn of two cards, A and B, where A rolls once if played first and three times if
/// second, and the enemies' turn then hits for 10 per roll. Played in either order, the two cards show the same state,
/// but not the same future.
#[derive(Clone)]
struct Rolls {
    steps: Vec<&'static str>,
    /// What the enemies' turn hits for each roll.
    hit: i32,
}

impl Default for Rolls {
    fn default() -> Rolls {
        Rolls { steps: Vec::new(), hit: 10 }
    }
}

impl Rolls {
    const A: Action = Action::Play { card: 1, target: None };
    const B: Action = Action::Play { card: 2, target: None };

    fn count(&self) -> i32 {
        match self.steps.iter().position(|s| *s == "A") {
            Some(0) => 1,
            Some(1) => 3,
            _ => 0,
        }
    }

    fn ended(&self) -> bool {
        self.steps.contains(&"end")
    }
}

impl state::ToyWorld for Rolls {
    fn observe(&self) -> CombatObservation {
        let mut obs = slugs();
        obs.round = if self.ended() { 2 } else { 1 };
        obs.player.hp = if self.ended() { 100 - self.hit * self.count() } else { 100 };
        obs.player.max_hp = 100;
        obs.hand = [("A", 1), ("B", 2)]
            .iter()
            .filter(|(id, _)| !self.steps.contains(id))
            .map(|&(id, uid)| CardView {
                id: Str::lit(id),
                uid: Some(uid),
                ..CardView::default()
            })
            .collect();
        obs
    }

    fn status(&self) -> Status {
        Status::InProgress
    }

    fn hidden_key(&self) -> u64 {
        self.count() as u64
    }

    fn legal(&self) -> Vec<Action> {
        if self.ended() {
            return Vec::new();
        }
        std::iter::once(Action::EndTurn)
            .chain(self.observe().hand.iter().map(|c| Action::Play { card: c.uid.expect("a uid"), target: None }))
            .collect()
    }

    fn execute(&mut self, action: &Action) -> Result<(), String> {
        self.steps.push(match action {
            Action::EndTurn => "end",
            Action::Play { card: 1, .. } => "A",
            Action::Play { card: 2, .. } => "B",
            other => return Err(format!("{} is not a play of the toy", other.text())),
        });
        Ok(())
    }

    fn run(&self) -> crate::protocol::RunChange {
        super::toy::around(&self.observe())
    }

    fn boxed(&self) -> Box<dyn state::ToyWorld> {
        Box::new(self.clone())
    }
}

/// C#: PimcMemoTests.Leaf. The HP after the enemies' turn, and 50 for each card played.
struct RollsLeaf;

impl Leaf for RollsLeaf {
    fn outcome(&self, state: &LeafState) -> Result<LeafOutcome, String> {
        Ok(LeafOutcome {
            value: (state.obs.player.hp + 50 * (2 - state.obs.hand.len() as i32)) as f64,
            win: 0.0,
            hp: 0.0,
        })
    }

    fn outcomes(&self, states: &[&LeafState]) -> Result<Vec<LeafOutcome>, String> {
        states.iter().map(|s| self.outcome(s)).collect()
    }
}

#[test]
fn lines_that_look_the_same_but_rolled_differently_are_not_merged() {
    let values = |first: Vec<Action>| -> Vec<f64> {
        let asked = pimc::Asked {
            history: Vec::new(),
            first,
            nodes_per_first: 1000,
            worlds: 1,
            seed: 0,
        };
        let root = State::toy(Box::new(Rolls::default()));
        pimc::world_lines(root, &asked, &none(), &RollsLeaf).expect("the lines").iter().map(|l| l.value).collect()
    };
    // A then B: one roll, 190. B then A: three rolls, 170. Searched in either order.
    assert_eq!(vec![190.0, 170.0], values(vec![Rolls::A, Rolls::B]));
    assert_eq!(vec![170.0, 190.0], values(vec![Rolls::B, Rolls::A]));
}

#[test]
fn an_end_reached_again_is_valued_once() {
    // With rolls that hit for nothing, A then B and B then A end the turn at the same observation, from states that the
    // hidden key tells apart (one roll against three): the leaf reads only the observation and the run, so it is asked
    // about that end once. The four lines end at three observations.
    struct Counting(std::cell::Cell<usize>);
    impl Leaf for Counting {
        fn outcome(&self, state: &LeafState) -> Result<LeafOutcome, String> {
            self.0.set(self.0.get() + 1);
            RollsLeaf.outcome(state)
        }
        fn outcomes(&self, states: &[&LeafState]) -> Result<Vec<LeafOutcome>, String> {
            states.iter().map(|s| self.outcome(s)).collect()
        }
    }
    let leaf = Counting(std::cell::Cell::new(0));
    let asked = pimc::Asked {
        history: Vec::new(),
        first: vec![Rolls::A, Rolls::B],
        nodes_per_first: 1000,
        worlds: 1,
        seed: 0,
    };
    let root = State::toy(Box::new(Rolls { hit: 0, ..Rolls::default() }));
    let lines = pimc::world_lines(root, &asked, &none(), &leaf).expect("the lines");
    assert_eq!(vec![200.0, 200.0], lines.iter().map(|l| l.value).collect::<Vec<_>>());
    assert_eq!(3, leaf.0.get());
}

#[test]
fn copies_in_the_hand_do_not_make_a_cycle() {
    // The first R comes back once, as Music Box copies the first attack of a turn; three more R in the hand would let
    // the probe repeat it, but the hand is not back after the second play.
    let start = Loop::new(Rules { runs_out: 2, copies: 4, ..Rules::new(1_000) }).state();
    let path = vec![loops::LoopPoint {
        obs: start.obs.clone(),
        depth: 0,
        state: start.copy(),
    }];
    let after = played(start.copy(), &[R]);
    let mut found = loops::Loops::new(1);

    assert!(loops::recurs(&start.obs, &after.obs));
    assert!(!found.intercept(&path, &after, &[R], &[start.hash]).expect("a probe"));
    assert!(!found.any);
}

#[test]
fn a_state_at_a_prompt_has_no_plays_and_takes_only_its_answers() {
    let mut world = played(Loop::new(Rules { prompted: Prompted::GivesR, ..Rules::new(50) }).state(), &[POTION]);

    assert!(world.obs.prompt.is_some());
    assert!(world.legal().is_empty());
    let (answers, cut) = order::actions(&mut world, 10);
    assert_eq!(vec![Action::pick(vec![0]), Action::pick(vec![1]), Action::pick(vec![2])], answers);
    assert!(!cut);
    // A prompt has to be answered: with no node left, its first answer is still tried.
    let (answers, cut) = order::actions(&mut world, 0);
    assert_eq!(vec![Action::pick(vec![0])], answers);
    assert!(cut);
}
