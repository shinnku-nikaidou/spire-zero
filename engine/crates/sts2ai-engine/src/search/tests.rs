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
use crate::protocol::{CardView, CombatObservation, CreatureView, PowerView, RelicView};
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
    turn::search(state, &none(), nodes, keep, bounds, false, leaf, None).expect("the turn's search")
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
fn an_unlimited_turn_search_ends_at_every_state_the_turn_can_reach_that_no_other_dominates() {
    let mut expected = HashSet::new();
    let mut over = HashSet::new();
    turn_ends(&mut toy(24), 1, &mut HashSet::new(), &mut expected, &mut over);
    let narrow = search(&mut toy(24), &ToyLeaf, 4, usize::MAX, &Bounds::new());
    let found = search(&mut toy(24), &ToyLeaf, UNLIMITED, usize::MAX, &Bounds::new());

    // Every end the rule keeps, each once, and nothing cut.
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
    // The toy's next turn starts the same after any line but for the enemy's HP and the player's (dominance.rs): what
    // stays is every end that no other leaves with the enemy's HP no higher and the player's no lower.
    let numbers = |key: &(u64, u64)| -> (i32, i32) {
        let mut world = toy(24);
        let mut seen = HashSet::new();
        fn find(state: &mut State, key: &(u64, u64), seen: &mut HashSet<(u64, u64)>) -> Option<(i32, i32)> {
            for action in state.legal() {
                let after = state.step(&action).expect("a legal action");
                if (after.state_key, after.hidden_key) == *key && after.round() != 1 {
                    return Some((after.obs.enemies[0].hp, after.obs.player.hp));
                }
                let mut after = after;
                if after.round() == 1
                    && seen.insert((after.state_key, after.hidden_key))
                    && let Some(found) = find(&mut after, key, seen)
                {
                    return Some(found);
                }
            }
            None
        }
        find(&mut world, key, &mut seen).expect("an end the toy reaches")
    };
    let kept: HashSet<(u64, u64)> = expected
        .iter()
        .filter(|a| {
            let (enemy, hp) = numbers(a);
            !expected.iter().any(|b| {
                let (e, h) = numbers(b);
                b != *a && e <= enemy && h >= hp && (e < enemy || h > hp)
            })
        })
        .copied()
        .collect();
    assert!(kept.len() < expected.len());
    assert_eq!(kept, reached.into_iter().collect::<HashSet<_>>());
    // A budget of 4 nodes ends elsewhere: one card a line, each end dominated by an end the whole search keeps.
    let keys = |ends: &[TurnEnd]| ends.iter().map(|e| e.key).collect::<HashSet<u64>>();
    assert_ne!(keys(&narrow.ends), keys(&found.ends));
}

#[test]
fn helpers_stepping_ahead_change_nothing_a_turns_search_finds() {
    let helpers = crate::search::helpers::Helpers::new();
    std::thread::scope(|scope| {
        for _ in 0..3 {
            scope.spawn(|| helpers.work());
        }
        for nodes in [1, 4, 30, 300, UNLIMITED] {
            let alone = search(&mut toy(24), &ToyLeaf, nodes, usize::MAX, &Bounds::new());
            let helped = turn::search(&mut toy(24), &none(), nodes, usize::MAX, &Bounds::new(), false, &ToyLeaf, Some(&helpers))
                .expect("the turn's search");
            assert_eq!((alone.nodes, alone.cut, alone.pruned), (helped.nodes, helped.cut, helped.pruned), "{nodes} nodes");
            assert_eq!(format!("{:?}", alone.ends), format!("{:?}", helped.ends), "{nodes} nodes");
        }
        helpers.finish();
    });
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
                    turn::search(&mut played(root.copy(), &job.path), &job.history, nodes, keep, &Bounds::new(), job.crossing, &ToyLeaf, None)
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
            paused: None,
            graph: None,
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
fn the_solver_says_when_its_picks_first_action_last_changed() {
    // No seed: no pick until the root's turn is searched, then the best found so far, run by run. The nodes before and
    // after the last run whose pick had another first action than the run before.
    let mut solver = solver(1);
    let (mut first, mut changed, mut changes) = (None, (0, 0), 0);
    while !solver.proved() {
        let before = solver.nodes();
        solver.run(&toy_searcher(&[], 24, false), 10).expect("a search");
        let now = solver.result().ok().and_then(|found| found.pick.actions.first().cloned());
        if now != first {
            (first, changed) = (now, (before, solver.nodes()));
            changes += 1;
        }
    }
    let settled = solver.settled();
    assert!(changes > 0);
    assert!(changed.0 < settled && settled <= changed.1, "{settled} not in {changed:?}");

    // Searched on once proved, nothing changes it.
    solver.run(&toy_searcher(&[], 24, false), 10).expect("a search");
    assert_eq!(settled, solver.settled());
}

/// max and xhigh (`Solver::set_stop`) from the same won seed, the beam's line, run by run.
fn max_and_xhigh(stop: i64) -> (Solver, Solver) {
    let beam = planned(&toy(24), &ToyLeaf, &budget(10, 1));
    assert_eq!(Status::Won, beam.end);
    let (mut max, mut xhigh) = (solver(1), solver(1));
    xhigh.set_stop(stop);
    for s in [&mut max, &mut xhigh] {
        s.seed(beam.line.clone(), beam.from);
    }
    (max, xhigh)
}

#[test]
fn xhigh_searches_its_first_run_as_max() {
    // The stop holds from the start (a won seed), but a solver's first run is searched whole.
    let (mut max, mut xhigh) = max_and_xhigh(1);
    for s in [&mut max, &mut xhigh] {
        s.run(&toy_searcher(&[], 24, false), 40).expect("a search");
    }
    assert_eq!(max.nodes(), xhigh.nodes());
    assert_eq!(result(&max).steps(), result(&xhigh).steps());
}

#[test]
fn xhigh_ends_a_later_run_once_its_won_picks_first_action_held() {
    let (mut max, mut xhigh) = max_and_xhigh(4);
    for s in [&mut max, &mut xhigh] {
        s.run(&toy_searcher(&[], 24, false), 10).expect("a search");
    }
    let begun = xhigh.nodes();
    for s in [&mut max, &mut xhigh] {
        s.run(&toy_searcher(&[], 24, false), 100_000).expect("a search");
    }
    // max searches on to the proof; xhigh answers before, with a won line whose first action held its 4 nodes.
    assert!(max.proved());
    assert!(!xhigh.proved());
    assert!(xhigh.nodes() - begun >= 4, "{} nodes", xhigh.nodes() - begun);
    assert!(xhigh.nodes() < max.nodes());
    assert!(xhigh.nodes() - xhigh.settled().max(begun) >= 4);
    let plan = result(&xhigh);
    assert_eq!(Status::Won, plan.end);
    assert_replays(&plan, 24, &[], false);
}

#[test]
fn xhigh_searches_as_max_while_no_line_wins() {
    // The enemy beyond reach: every pick loses, so the stop never holds and the runs are max's, node for node.
    let (mut max, mut xhigh) = (solver(1), solver(1));
    xhigh.set_stop(1);
    for _ in 0..4 {
        for s in [&mut max, &mut xhigh] {
            s.run(&toy_searcher(&[], 500, false), 20).expect("a search");
        }
        assert_eq!(max.nodes(), xhigh.nodes());
    }
    assert_eq!(result(&max).steps(), result(&xhigh).steps());
}

#[test]
fn the_same_nodes_give_the_same_plan() {
    let plans: Vec<(Plan, i64, usize)> = (0..2)
        .map(|_| {
            let mut solver = solver(4);
            // 40 nodes stop short of the proof, which the dominance among the turns' ends brings at 82.
            solver.run(&toy_searcher(&[], 24, false), 40).expect("a search");
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

    // Along the plan, into the turn and then past its end: all below was searched, so only the root's turn is again,
    // into the turn; past its end, the next turn's start was searched in full, and is the root as it is.
    for count in [1, turn] {
        let gone = &line[..count];
        let mut moved = solver(16);
        moved.run(&toy_searcher(&[], 24, false), i64::MAX).expect("a search");
        let searched = moved.searched();
        moved.advance(gone, moves(gone), &toy_searcher(gone, 24, false)).expect("the root's turn");
        if count != turn {
            // Into the turn, the root's turn is searched in the next run, and the pick meanwhile is the line played.
            assert_eq!(searched, moved.searched());
            assert!(!moved.proved());
            assert_eq!(line[count..].to_vec(), result(&moved).line.actions);
            moved.run(&toy_searcher(gone, 24, false), 1).expect("the root's turn");
        }

        assert_eq!(searched + if count == turn { 0 } else { 1 }, moved.searched());
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
fn a_turns_search_paused_after_every_node_finds_what_it_finds_whole() {
    // Paused after each node and gone on with in the next call: the same states in the same order, the same ends
    // asked of the leaf, the same cycles cashed out.
    let worlds = [
        toy(24),
        Toy::new(24, Some("P"), true).mittens().state(),
        Loop::new(Rules::new(15)).state(),
        Loop::new(Rules { prompted: Prompted::GivesR, ..Rules::new(50) }).state(),
        Loop::new(Rules { junk_every: 6, ..Rules::new(50) }).state(),
    ];
    for root in worlds {
        let whole = turn::search(&mut root.copy(), &none(), UNLIMITED, usize::MAX, &Bounds::new(), false, &ToyLeaf, None).expect("a search");
        let job = Job {
            path: Vec::new(),
            history: none(),
            bounds: Bounds::new(),
            from: None,
            crossing: false,
            most: Some(1),
            resume: std::sync::Mutex::new(None),
            record: false,
            memo: std::sync::Mutex::new(None),
        };
        let (mut calls, mut nodes) = (0, 0);
        let pieces = loop {
            let mut found = turn::job(&job, || Ok(root.copy()), UNLIMITED, usize::MAX, &Bounds::new(), &ToyLeaf, None).expect("a piece");
            calls += 1;
            nodes += found.nodes;
            match found.paused.take() {
                Some(turn) => *job.resume.lock().expect("the job") = Some(turn),
                None => break found,
            }
        };
        assert!(calls > 1, "the search paused");
        assert_eq!((whole.nodes, whole.cut, whole.pruned), (nodes, pieces.cut, pieces.pruned));
        let ends = |found: &TurnSearch| -> Vec<(String, u64, u64, u64)> {
            found.ends.iter().map(|e| (text(&e.actions), e.key, e.value.to_bits(), e.exact)).collect()
        };
        assert_eq!(ends(&whole), ends(&pieces));
    }
}

#[test]
fn a_turns_search_with_the_graph_of_an_earlier_one_finds_what_it_finds_alone() {
    // From each state a line of the root's turn goes through, a search with no bounds taking the root's search's
    // graph where it holds the same worlds finds what a search alone finds there, and steps fewer worlds.
    // A Loop counts the actions its copies played: the steps the search made.
    let counted = |world: Loop| {
        let played = world.played();
        (world.state(), Some(played))
    };
    let worlds = [
        (toy(24), None),
        (Toy::new(24, Some("P"), true).mittens().state(), None),
        (Toy::new(40, Some("P"), false).state(), None),
        counted(Loop::new(Rules { prompted: Prompted::GivesR, ..Rules::new(50) })),
        counted(Loop::new(Rules { prompted: Prompted::AsksTwice, ..Rules::new(9) })),
        counted(Loop::new(Rules { junk_every: 6, ..Rules::new(50) })),
    ];
    let job = |history: &Arc<History>, record: bool, memo: Option<Arc<turn::Graph>>| Job {
        path: Vec::new(),
        history: history.clone(),
        bounds: Bounds::new(),
        from: None,
        crossing: false,
        most: None,
        resume: std::sync::Mutex::new(None),
        record,
        memo: std::sync::Mutex::new(memo.map(turn::Memo::Graph)),
    };
    let (mut held, mut compared, mut saved) = (0, 0, 0);
    for (root, played) in worlds {
        let steps = || played.as_ref().map_or(0, |p| p.get());
        let history = Arc::new(state::witness(&none(), &root.obs));
        let whole = turn::job(&job(&history, true, None), || Ok(root.copy()), UNLIMITED, usize::MAX, &Bounds::new(), &ToyLeaf, None).expect("a search");
        let Some(graph) = whole.graph.clone() else {
            continue;
        };
        held += 1;
        // The bounds of the wins the turn reaches, which prune a search that has them: a memo kept without them taken
        // by a search with them, and one kept with them by a search without.
        let mut won = Bounds::new();
        for end in whole.ends.iter().filter(|e| e.status == Status::Won) {
            bound::note(&mut won, true, &end.potions, end.life);
        }
        let pruned = turn::job(&job(&history, true, None), || Ok(root.copy()), UNLIMITED, usize::MAX, &won, &ToyLeaf, None)
            .expect("a search")
            .graph;
        let mut prefixes: Vec<Vec<Action>> = Vec::new();
        for end in &whole.ends {
            let turn = end.actions.iter().position(|a| a.is_end_turn()).unwrap_or(end.actions.len());
            for k in 1..turn {
                if !prefixes.contains(&end.actions[..k].to_vec()) {
                    prefixes.push(end.actions[..k].to_vec());
                }
            }
        }
        let cases: Vec<(Arc<turn::Graph>, Bounds)> = [(Some(graph.clone()), Bounds::new()), (Some(graph.clone()), won.clone()), (pruned, Bounds::new())]
            .into_iter()
            .filter_map(|(memo, bounds)| Some((memo?, bounds)))
            .collect();
        for (prefix, (memo, bounds)) in prefixes.iter().flat_map(|p| cases.iter().map(move |c| (p, c))) {
            let prefix = prefix.clone();
            let there = self::played(root.copy(), &prefix);
            let before = steps();
            let alone = turn::search(&mut there.copy(), &history, UNLIMITED, usize::MAX, bounds, false, &ToyLeaf, None).expect("a search");
            let steps_alone = steps() - before;
            let taken = turn::job(&job(&history, false, Some(memo.clone())), || Ok(there.copy()), UNLIMITED, usize::MAX, bounds, &ToyLeaf, None)
                .expect("a search");
            let steps_taken = steps() - before - steps_alone;
            compared += 1;
            saved += steps_alone.saturating_sub(steps_taken);
            let ends = |found: &TurnSearch| -> Vec<String> {
                found
                    .ends
                    .iter()
                    .map(|e| {
                        format!(
                            "{} {:?} {} {} {} {} {} {} {} {} {} {} {:?}",
                            text(&e.actions),
                            e.status,
                            e.key,
                            e.exact,
                            e.shown,
                            e.value.to_bits(),
                            e.win.to_bits(),
                            e.hp.to_bits(),
                            e.life,
                            e.reach,
                            e.potions,
                            e.hashes.iter().map(|h| h.to_string()).collect::<Vec<_>>().join(","),
                            e.history
                        )
                    })
                    .collect()
            };
            assert_eq!(ends(&alone), ends(&taken), "after {}", text(&prefix));
            assert_eq!((alone.cut, alone.pruned), (taken.cut, taken.pruned), "after {}", text(&prefix));
            // The steps taken from the memo are not counted: only those it made itself.
            assert!(taken.nodes <= alone.nodes, "after {}", text(&prefix));
        }
    }
    assert!(held >= 3 && compared >= 30, "{held} graphs held, {compared} searches compared");
    assert!(saved > 0, "the memo saved no step");
}

#[test]
fn a_turns_search_with_the_graph_of_one_paused_before_its_end_finds_what_it_finds_alone() {
    // The root's turn searched a few nodes and paused: from each state a line of the turn goes through, a search taking
    // what that one went through so far finds what a search alone finds. And that search, paused in turn, is the memo
    // of a search one action further on: what it took from the first memo is in its own graph too.
    let worlds = [
        toy(24),
        Toy::new(24, Some("P"), true).mittens().state(),
        Toy::new(40, Some("P"), false).state(),
        Loop::new(Rules { prompted: Prompted::GivesR, ..Rules::new(50) }).state(),
        Loop::new(Rules { junk_every: 6, ..Rules::new(50) }).state(),
    ];
    let job = |history: &Arc<History>, most: Option<i32>, memo: Option<turn::Memo>| Job {
        path: Vec::new(),
        history: history.clone(),
        bounds: Bounds::new(),
        from: None,
        crossing: false,
        most,
        resume: std::sync::Mutex::new(None),
        record: true,
        memo: std::sync::Mutex::new(memo),
    };
    let ends = |found: &TurnSearch| -> Vec<String> {
        found
            .ends
            .iter()
            .map(|e| format!("{} {:?} {} {} {} {} {} {}", text(&e.actions), e.status, e.key, e.exact, e.shown, e.value.to_bits(), e.reach, e.potions))
            .collect()
    };
    let (mut compared, mut chained) = (0, 0);
    for root in worlds {
        let history = Arc::new(state::witness(&none(), &root.obs));
        let whole = turn::search(&mut root.copy(), &history, UNLIMITED, usize::MAX, &Bounds::new(), false, &ToyLeaf, None).expect("a search");
        let mut prefixes: Vec<Vec<Action>> = Vec::new();
        for end in &whole.ends {
            let turn = end.actions.iter().position(|a| a.is_end_turn()).unwrap_or(end.actions.len());
            for k in 1..turn {
                if !prefixes.contains(&end.actions[..k].to_vec()) {
                    prefixes.push(end.actions[..k].to_vec());
                }
            }
        }
        let alone = |prefix: &[Action]| {
            let mut there = played(root.copy(), prefix);
            turn::search(&mut there, &history, UNLIMITED, usize::MAX, &Bounds::new(), false, &ToyLeaf, None).expect("a search")
        };
        // The search from `from`, paused after `most` nodes, with `memo`.
        let paused = |from: &[Action], most: i32, memo: Option<turn::Memo>| {
            let there = played(root.copy(), from);
            turn::job(&job(&history, Some(most), memo), || Ok(there.copy()), UNLIMITED, usize::MAX, &Bounds::new(), &ToyLeaf, None)
                .expect("a search")
                .paused
        };
        let taken = |prefix: &[Action], memo: turn::Memo| {
            let there = played(root.copy(), prefix);
            turn::job(&job(&history, None, Some(memo)), || Ok(there.copy()), UNLIMITED, usize::MAX, &Bounds::new(), &ToyLeaf, None)
                .expect("a search")
        };
        for most in [1, 3, 10, 40] {
            for prefix in &prefixes {
                let Some(first) = paused(&[], most, None) else {
                    continue;
                };
                assert_eq!(ends(&alone(prefix)), ends(&taken(prefix, turn::Memo::Paused(first))), "{most} nodes, after {}", text(prefix));
                compared += 1;
                for next in prefixes.iter().filter(|p| p.len() == prefix.len() + 1 && p.starts_with(prefix)) {
                    let first = paused(&[], most, None).expect("paused as before");
                    let Some(second) = paused(prefix, 2, Some(turn::Memo::Paused(first))) else {
                        continue;
                    };
                    assert_eq!(ends(&alone(next)), ends(&taken(next, turn::Memo::Paused(second))), "{most} then 2 nodes, after {}", text(next));
                    chained += 1;
                }
            }
        }
    }
    assert!(compared >= 30 && chained >= 30, "{compared} compared, {chained} with a memo of a memo");
}

#[test]
fn the_solver_plays_the_seeds_line_until_its_roots_turn_is_searched() {
    // The root's turn a node at a time: until it is searched, the pick is the seed's line; the combat goes on along it
    // into the turn, where the root's turn is searched on with what its search went through so far. In the end, the
    // same proved value as a solver that searched the root's turn whole there.
    let beam = planned(&toy(24), &ToyLeaf, &budget(10, 1));
    let mut solver = solver(1).with_piece(1);
    solver.seed(beam.line.clone(), beam.from);
    solver.run(&toy_searcher(&[], 24, false), 1).expect("a search");
    assert_eq!(1, solver.left());
    assert!(!solver.proved());
    assert_eq!(beam.line.actions, result(&solver).line.actions);

    let gone = vec![beam.line.actions[0].clone()];
    solver.advance(&gone, moves(&gone), &toy_searcher(&gone, 24, false)).expect("the root's turn");
    assert_eq!(beam.line.actions[1..].to_vec(), result(&solver).line.actions);
    let mut runs = 0;
    while !solver.proved() {
        solver.run(&toy_searcher(&gone, 24, false), 1).expect("a search");
        runs += 1;
        assert!(runs < 100_000, "the solver ends");
    }
    let plan = result(&solver);
    assert_near(best(&gone, 24, false), plan.value, 9);
    assert_replays(&plan, 24, &gone, false);
}

#[test]
fn the_solver_with_its_turns_in_pieces_proves_the_same_line() {
    // Each state's turn searched a node at a time, its search paused and gone on with in the next batch, a run of one
    // node after another: the same proved value as turns searched whole.
    let mut whole = solver(16);
    whole.run(&toy_searcher(&[], 24, false), i64::MAX).expect("a search");
    let mut pieces = solver(16).with_piece(1);
    let mut runs = 0;
    while !pieces.proved() {
        pieces.run(&toy_searcher(&[], 24, false), 1).expect("a search");
        runs += 1;
        assert!(runs < 100_000, "the solver ends");
    }
    let plan = result(&pieces);
    assert_near(result(&whole).value, plan.value, 9);
    assert_near(best(&[], 24, false), plan.value, 9);
    assert_replays(&plan, 24, &[], false);
    assert!(runs > pieces.searched() as usize, "turns paused");
}

#[test]
fn the_solvers_root_crosses_a_prompt_at_a_turns_start_to_the_states_it_holds() {
    // Each turn's start after the first asks for a card to exhaust (Toasty Mittens). Past the turn's end the combat
    // stands at that prompt, inside the edges to the next turn's starts: the root's turn ends where the player can
    // play, at the states the solver holds already, and the answer moves the root onto one of them as it is. One batch
    // is searched first: no win is found in two turns, so no bound has pruned a state below the one the line goes
    // through.
    const HP: i32 = 30;
    let root = || Toy::new(HP, None, false).mittens().state();
    let searcher = |gone: &[Action]| Replay {
        root: played(root(), gone),
        leaf: &ToyLeaf,
        bounds: true,
    };
    let moves = |gone: &[Action]| {
        let (mut world, mut record) = (root(), Vec::new());
        for action in gone {
            world = world.step(action).expect("a legal action");
            if world.status == Status::InProgress && world.round() != 1 {
                record = state::witness(&record, &world.obs);
            }
        }
        Arc::new(record)
    };
    let mut solver = solver(16);
    // The root's turn, then one batch.
    solver.run(&searcher(&[]), 1).expect("a search");
    solver.run(&searcher(&[]), 1).expect("a search");
    let line = result(&solver).line.actions.clone();
    let end = line.iter().position(|a| a.is_end_turn()).expect("a turn's end") + 1;
    assert!(matches!(line[end], Action::Pick(_)));
    let (searched, nodes) = (solver.searched(), solver.nodes());

    let crossed = &line[..end];
    solver.advance(crossed, moves(crossed), &searcher(crossed)).expect("the root's turn");
    solver.run(&searcher(crossed), 1).expect("the root's turn");
    assert_eq!(searched + 1, solver.searched());
    let prompt = played(root(), crossed).obs.prompt.clone().expect("the prompt");
    assert_eq!(order::answers(&prompt, i64::MAX).0.len() as i64, solver.nodes() - nodes, "a node for each answer");

    let answered = &line[..end + 1];
    solver.advance(&line[end..end + 1], moves(answered), &searcher(answered)).expect("the root's turn");
    assert_eq!(searched + 1, solver.searched());
    solver.run(&searcher(answered), i64::MAX).expect("a search");
    let plan = result(&solver);
    assert!(plan.proved);
    let mut world = played(root(), answered);
    let mut known = HashMap::new();
    assert_near(answers_too(&mut world, &mut known), plan.value, 9);
    for step in plan.steps() {
        world = world.step(&step).expect("a step of the plan");
    }
    assert_eq!(plan.end, world.status);
}

#[test]
fn a_solver_that_begins_at_a_prompt_crosses_it_first_and_values_every_answer() {
    // The combat stands at the second turn's exhaust (Toasty Mittens), as at a prompt the node's clone stands at (Gambling
    // Chip's discard, which opens a combat): the root's turn is the prompt's answers alone, a node each, and the states
    // they reach are searched as the solver's states are, to the best line over every answer.
    const HP: i32 = 30;
    let root = played(Toy::new(HP, None, false).mittens().state(), &[Action::EndTurn]);
    let prompt = root.obs.prompt.clone().expect("the prompt");
    let searcher = Replay {
        root: root.copy(),
        leaf: &ToyLeaf,
        bounds: true,
    };
    let mut solver = Solver::new(Arc::new(state::witness(&Vec::new(), &root.obs)), 1.0, 16);
    solver.run(&searcher, 1).expect("the root's turn");
    assert_eq!(order::answers(&prompt, i64::MAX).0.len() as i64, solver.nodes(), "a node for each answer");
    assert_eq!(1, solver.searched());

    solver.run(&searcher, i64::MAX).expect("a search");
    let plan = result(&solver);
    assert!(plan.proved);
    let mut world = root.copy();
    let mut known = HashMap::new();
    assert_near(answers_too(&mut world, &mut known), plan.value, 9);
    for step in plan.steps() {
        world = world.step(&step).expect("a step of the plan");
    }
    assert_eq!(plan.end, world.status);
}

#[test]
fn a_beam_at_a_prompt_crosses_it_first() {
    // The second turn's exhaust (Toasty Mittens) as the decision's own prompt, as Gambling Chip's discard that opens a
    // combat: the beam's first turn is the answers alone, each valued at the round's start it leads to, and a beam of 2
    // from there wins with 10 nodes a state; a turn's search of every answer and of the turn after each, in those 10
    // nodes, found no win.
    let root = played(Toy::new(30, None, false).mittens().state(), &[Action::EndTurn]);
    assert!(root.obs.prompt.is_some());
    let plan = planned(&root, &ToyLeaf, &budget(10, 2));
    assert_eq!(Status::Won, plan.end);
}

/// `brute_force` with every answer tried where a prompt waits.
fn answers_too(state: &mut State, known: &mut HashMap<(u64, u64), f64>) -> f64 {
    if state.status != Status::InProgress {
        return outcome(state).value;
    }
    let key = (state.hash, state.hidden_key);
    if let Some(&value) = known.get(&key) {
        return value;
    }
    let actions = match &state.obs.prompt {
        Some(prompt) => order::answers(prompt, i64::MAX).0,
        None => state.legal(),
    };
    let mut best = f64::NEG_INFINITY;
    for action in actions {
        let mut after = state.step(&action).expect("a legal action");
        best = best.max(answers_too(&mut after, known));
    }
    known.insert(key, best);
    best
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

#[test]
fn the_solvers_pick_with_every_waiting_turn_paused_is_the_state_valued_highest() {
    // The root's turn searched, then every state it reaches in one batch, each paused after a node: none waits in the
    // queue and nothing ends the combat yet, and the pick is still the line to the state valued highest.
    const HP: i32 = 500;
    let mut solver = solver(1000).with_piece(1);
    solver.run(&toy_searcher(&[], HP, false), 1).expect("the root's turn");
    let before = result(&solver);
    let left = solver.left();
    solver.run(&toy_searcher(&[], HP, false), 1).expect("a batch");
    assert_eq!(left, solver.left(), "every state paused");
    let plan = picked(solver.result().expect("a pick with every turn paused"), 1);

    assert_eq!(Status::InProgress, plan.end);
    assert_eq!(before.line.actions, plan.line.actions);
    assert_replays(&plan, HP, &[], false);
}

/// A combat of `TURNS` turns where every turn's start has `WIDTH` ends, one by each card, and every line wins on its
/// last turn, the more the higher its last card. The net values a start lower the more turns it is in, as it hopes more
/// of a turn it has not seen played yet (plans/2026-11-21-1402-solver-turn-queues.md, 1.3).
fn hoping(path: &[Action]) -> TurnSearch {
    const WIDTH: u32 = 4;
    const TURNS: usize = 5;
    let turns = path.iter().filter(|a| a.is_end_turn()).count();
    let ends = (1..=WIDTH)
        .map(|card| {
            let play = Action::Play { card, target: None };
            let mut end = TurnEnd {
                history: none(),
                ..beam::start(none())
            };
            if turns + 1 == TURNS {
                (end.actions, end.status, end.value) = (vec![play], Status::Won, 0.6 + 0.01 * card as f64);
            } else {
                let key = format!("{} {}", text(path), card).bytes().fold(14_695_981_039_346_656_037u64, |h, b| (h ^ b as u64).wrapping_mul(1_099_511_628_211));
                (end.actions, end.key, end.value) = (vec![play, Action::EndTurn], key, 0.5 - 0.1 * (turns + 1) as f64);
            }
            end.hashes = vec![end.key; end.actions.len()];
            end
        })
        .collect();
    TurnSearch {
        ends,
        nodes: WIDTH as i32,
        cut: false,
        pruned: 0,
        start: None,
        paused: None,
        graph: None,
    }
}

#[test]
fn the_solver_takes_each_layer_of_turns_in_turn_so_a_net_hoping_more_of_earlier_turns_still_finishes_lines() {
    // Taken by worth across turns, every start of a layer comes before any of the next, 4 + 16 + 64 + 1 turns' searches
    // (340 nodes) before a win; one start of each layer in turn, the line reaches its last turn in a few batches.
    let searcher = jobs(|jobs: &[Job], _, _| Ok(jobs.iter().map(|job| hoping(&job.path)).collect()));
    let mut solver = solver(4);
    solver.run(&searcher, 100).expect("a search");
    assert!(solver.nodes() <= 120);
    assert_eq!(Status::Won, result(&solver).end);

    // Searched to the end, the best line, proved.
    solver.run(&searcher, i64::MAX).expect("a search");
    let plan = result(&solver);
    assert!(plan.proved);
    assert_eq!(0.64, plan.value);
}

#[test]
fn the_solvers_root_moved_along_a_won_line_keeps_it() {
    // Searched in full, then the combat goes another way than the pick: the wins the pick's way are out of reach, and
    // where the bound they made falls, the turns it pruned are to be searched again. Along the won line picked there,
    // an action at a time with only the root's turn searched again at each, the pick stays that line, as good. (On
    // this toy, the potion drunk first, the win was lost two actions on: the turns to be searched again had let go of
    // what they found, and the pick was made again of what the states held.)
    let root = || Toy::new(38, Some("P"), true).mittens().state();
    let searcher = |gone: &[Action]| Replay {
        root: played(root(), gone),
        leaf: &ToyLeaf,
        bounds: true,
    };
    let moves = |gone: &[Action]| {
        let (mut world, mut record) = (root(), Vec::new());
        for action in gone {
            world = world.step(action).expect("a legal action");
            if world.status == Status::InProgress && world.round() != 1 {
                record = state::witness(&record, &world.obs);
            }
        }
        Arc::new(record)
    };
    let mut full = solver(16);
    full.run(&searcher(&[]), i64::MAX).expect("a search");
    let first = result(&full).line.actions[0].clone();
    let mut walked = 0;
    for off in root().legal().into_iter().filter(|a| *a != first) {
        let mut solver = solver(16);
        solver.run(&searcher(&[]), i64::MAX).expect("a search");
        let mut gone = vec![off];
        solver.advance(&gone, moves(&gone), &searcher(&gone)).expect("the root's turn");
        solver.run(&searcher(&gone), 1).expect("the root's turn");
        let there = result(&solver);
        if there.end != Status::Won {
            continue;
        }
        let line = there.line.actions.clone();
        for (i, step) in line[..line.len() - 1].iter().enumerate() {
            gone.push(step.clone());
            solver.advance(std::slice::from_ref(step), moves(&gone), &searcher(&gone)).expect("the root's turn");
            solver.run(&searcher(&gone), 1).expect("the root's turn");
            let now = result(&solver);
            assert_eq!(Status::Won, now.end, "after {}", text(&gone));
            assert!(now.value >= there.value - 1e-9, "after {}: {} of {}", text(&gone), now.value, there.value);
            if now.value <= there.value + 1e-9 {
                assert_eq!(line[i + 1..].to_vec(), now.line.actions, "after {}", text(&gone));
            }
            walked += 1;
        }
    }
    assert!(walked > 0, "a won line off the pick");
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
// The dominance among a turn's ends (dominance.rs)
// ---------------------------------------------------------------------------------------------------------------------

/// A toy for the dominance: one enemy at 40 HP and, each turn, two cards of no cost. Neutralize (`N`) takes 4 HP, gives
/// `gives` of the power `power` and moves the relic's counter one on; Poke (`P`) takes 1 HP and gives `poke_gives`. The
/// power wears off by one at the turn's end where it is positive; nothing else of the next turn depends on the line. With
/// an ally beside the player (Wuppy), the enemy hits it at the turn's end, for 2 less while it holds the power, and Poke
/// costs it `poke_costs`.
#[derive(Clone)]
struct Marks {
    encounter: &'static str,
    power: &'static str,
    /// Whether the game declares the power a debuff (`State::enemy_debuffs`).
    debuff: bool,
    gives: i32,
    poke_gives: i32,
    enemy: i32,
    amount: i32,
    counter: i32,
    round: i32,
    hand: Vec<u32>,
    /// The ally's HP, if there is one.
    ally: Option<i32>,
    /// The enemy's hit on the ally at the turn's end.
    hit: i32,
    /// What Poke costs the ally.
    poke_costs: i32,
}

const N: u32 = 1;
const P: u32 = 2;

impl Marks {
    /// The search's state at the toy's start.
    fn state(encounter: &'static str, power: &'static str, debuff: bool, gives: i32, poke_gives: i32) -> State {
        State::toy(Box::new(Marks::start(encounter, power, debuff, gives, poke_gives)))
    }

    /// The toy at its start, with no ally.
    fn start(encounter: &'static str, power: &'static str, debuff: bool, gives: i32, poke_gives: i32) -> Marks {
        let hand = vec![N, P];
        Marks { encounter, power, debuff, gives, poke_gives, enemy: 40, amount: 0, counter: 0, round: 1, hand, ally: None, hit: 0, poke_costs: 0 }
    }

    /// The search's state at the start with Wuppy at 30 HP beside the player, Neutralize giving 2 Weak.
    fn with_ally(hit: i32, poke_costs: i32) -> State {
        let marks = Marks { ally: Some(30), hit, poke_costs, ..Marks::start("CORPSE_SLUGS_WEAK", "WEAK_POWER", true, 2, 0) };
        State::toy(Box::new(marks))
    }
}

impl state::ToyWorld for Marks {
    fn observe(&self) -> CombatObservation {
        let mut obs = slugs();
        obs.encounter = Some(Str::lit(self.encounter));
        obs.round = self.round;
        obs.player.hp = 20;
        obs.player.max_hp = 20;
        obs.player.block = 0;
        obs.player.energy = 1;
        let name = |uid: u32| Str::lit(if uid == N { "NEUTRALIZE" } else { "POKE" });
        obs.hand = self.hand.iter().map(|&uid| CardView { id: name(uid), uid: Some(uid), ..CardView::default() }).collect();
        obs.draw = Vec::new();
        obs.discard = Vec::new();
        obs.potions = vec![None, None, None];
        obs.relics = vec![RelicView { id: Str::lit("PEN_NIB"), counter: Some(self.counter), ..RelicView::default() }];
        let mut enemy = obs.enemies[0].clone();
        enemy.uid = Some(1);
        enemy.hp = self.enemy;
        enemy.max_hp = 40;
        enemy.powers = if self.amount == 0 { Vec::new() } else { vec![PowerView { id: Str::lit(self.power), amount: self.amount, ..PowerView::default() }] };
        obs.enemies = vec![enemy];
        obs.allies = self.ally.map(|hp| CreatureView { uid: Some(2), id: Some(Str::lit("WUPPY")), hp, max_hp: 30, ..CreatureView::default() }).into_iter().collect();
        obs
    }

    fn status(&self) -> Status {
        Status::InProgress
    }

    fn hidden_key(&self) -> u64 {
        self.round as u64
    }

    fn legal(&self) -> Vec<Action> {
        let mut legal = vec![Action::EndTurn];
        legal.extend(self.hand.iter().map(|&card| Action::Play { card, target: Some(1) }));
        legal
    }

    fn execute(&mut self, action: &Action) -> Result<(), String> {
        if !self.legal().contains(action) {
            return Err(format!("{} is not legal", action.text()));
        }
        match action {
            Action::Play { card, .. } => {
                self.hand.retain(|c| c != card);
                if *card == N {
                    (self.enemy, self.amount, self.counter) = (self.enemy - 4, self.amount + self.gives, self.counter + 1);
                } else {
                    (self.enemy, self.amount) = (self.enemy - 1, self.amount + self.poke_gives);
                    self.ally = self.ally.map(|hp| hp - self.poke_costs);
                }
            }
            _ => {
                let hit = if self.amount > 0 { self.hit - 2 } else { self.hit };
                self.ally = self.ally.map(|hp| hp - hit);
                if self.amount > 0 {
                    self.amount -= 1;
                }
                (self.round, self.hand) = (self.round + 1, vec![N, P]);
            }
        }
        Ok(())
    }

    fn run(&self) -> crate::protocol::RunChange {
        super::toy::around(&self.observe())
    }

    fn boxed(&self) -> Box<dyn state::ToyWorld> {
        Box::new(self.clone())
    }

    fn debuffs(&self) -> Vec<Vec<Str>> {
        vec![if self.debuff { vec![Str::lit(self.power)] } else { Vec::new() }]
    }
}

/// The lines of a turn's search from `root`, searched whole, each as the cards it played, sorted.
fn kept(mut root: State) -> Vec<Vec<u32>> {
    let found = search(&mut root, &ToyLeaf, UNLIMITED, usize::MAX, &Bounds::new());
    let mut lines: Vec<Vec<u32>> = found
        .ends
        .iter()
        .map(|e| {
            let mut cards: Vec<u32> = e.actions.iter().filter_map(|a| if let Action::Play { card, .. } = a { Some(*card) } else { None }).collect();
            cards.sort_unstable();
            cards
        })
        .collect();
    lines.sort();
    lines
}

#[test]
fn an_end_with_less_damage_and_less_debuff_is_dropped_whatever_the_relics_counter() {
    // Neutralize's line leaves the enemy 4 HP lower and 1 Weak, with Pen Nib's counter one further: it is still
    // compared (the user: 「遺物計數 不算」), and only the line that played both cards is left.
    assert_eq!(kept(Marks::state("CORPSE_SLUGS_WEAK", "WEAK_POWER", true, 2, 0)), vec![vec![N, P]]);
}

#[test]
fn an_allys_hp_is_compared_as_the_players() {
    // Neutralize's Weak spares Wuppy 2 of the enemy's hit (the user, of Wuppy: 「加進規則」): only the line that
    // played both cards is left, as without an ally.
    assert_eq!(kept(Marks::with_ally(6, 0)), vec![vec![N, P]]);
}

#[test]
fn an_end_that_cost_an_ally_hp_is_not_dropped_for_one_that_hit_harder() {
    // Poke costs Wuppy 3 HP for the enemy's 1: Neutralize alone and Neutralize with Poke are both kept, the enemy's HP
    // lower in one and Wuppy's higher in the other; the lines without Neutralize go.
    assert_eq!(kept(Marks::with_ally(6, 3)), vec![vec![N], vec![N, P]]);
}

#[test]
fn the_queens_fight_goes_without_the_rule() {
    // The user: 「女王戰 不要这条规则」.
    assert_eq!(kept(Marks::state("QUEEN_BOSS", "WEAK_POWER", true, 2, 0)), vec![vec![], vec![N], vec![N, P], vec![P]]);
}

#[test]
fn a_power_the_game_does_not_call_a_debuff_must_be_the_same() {
    // The same power, declared a buff: the lines with it and those without are not compared, each pair within.
    assert_eq!(kept(Marks::state("CORPSE_SLUGS_WEAK", "STRENGTH_POWER", false, 2, 0)), vec![vec![N, P], vec![P]]);
}

#[test]
fn a_debuffs_negative_amount_must_be_the_same() {
    // Neutralize's Shrink of -1 is for good, Poke's 2 wears off: neither is more.
    let lines = kept(Marks::state("CORPSE_SLUGS_WEAK", "SHRINK_POWER", true, -1, 2));
    assert!(lines.contains(&vec![N]), "{lines:?}");
    assert!(lines.contains(&vec![P]), "{lines:?}");
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
