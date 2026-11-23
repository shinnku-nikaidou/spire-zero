//! A world: a combat and where it stands, taking the player's actions.
//!
//! An action starts through the game's own entry points (the card's `TryManualPlay`, the potion's `EnqueueManualUse`,
//! an `EndPlayerTurnAction`), then the queued actions and the turn loop run in turn until neither can go on: the
//! player is asked again, or the combat is over.
//!
//! An action is taken in a copy of the combat, which makes the world after it: the world before stays as it is, for
//! search to come back to (`step`), or is let go (`apply`). A copy shares the combat's objects until one of the two
//! writes to them (rt/obj.rs), so the copy is the price of what the action changes.
//!
//! A card prompt in the middle of an action has no answer the first time: the action stops there
//! (`Err(Stop::Choice)`). The world then keeps the combat as it was before the action, the action and the answers
//! given so far, and shows the combat as it stood at the prompt. The answer runs the action again with one more
//! answer; the random streams are part of the combat, so it runs the same way up to the prompt. It runs from the
//! step's last resume point before the prompt, if there is one, not from the action's start: a place where nothing of
//! the game's code is running, so the combat there is all there is (`resume_point`). There are two: before the players'
//! turn starts (manual/combat_manager.rs, `turn_loop`), and in the player's setup at that turn start, after the hand is
//! drawn and before `AfterPlayerTurnStart`'s hooks (`setup_point`). A prompt of those hooks (Toasty Mittens' exhaust)
//! is answered from there: the end of the players' turn, the enemies' turn and the draw are not run again.
//!
//! A combat can also be taken in as it begins, before its first turn (`begin`): the combat's start then runs here
//! (C#: StartCombatInternal after its wait for the executor), so a prompt that opens the combat (Gambling Chip's discard, the Knowledge Demon
//! dominion's choice) is a prompt of a world like any other.
//!
//! A NoSL world can be drawn at a card prompt (`draw_at_prompt`, plans/2026-11-21-1213-nosl-prompt-worlds.md): the
//! real combat runs again to the prompt as it ran, and where the prompt takes its answer, everything the player cannot
//! see is drawn again (determinize.rs), from the world's seed and the draw pile's record at the prompt. Every run again
//! passes the same point with the same seed, so the world stays one world: what was seen up to the prompt is the real
//! one, what comes after it is drawn.

use crate::ffi;
use crate::r#gen as g;
use crate::r#gen::enums::GameActionState;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::manual;
use crate::prompt::{self, Asked};
use crate::rt::*;
use std::sync::Arc;

/// A combat the player acts in.
#[derive(Clone)]
pub struct Live {
    /// The combat as it stands; at a card prompt, as it stood when the running action stopped there.
    combat: Combat,
    /// At a card prompt: what the answer runs again.
    prompted: Option<Box<Prompted>>,
}

#[derive(Clone)]
struct Prompted {
    /// The combat before the action, which every world along the action's prompts holds.
    before: Arc<Combat>,
    action: String,
    /// The answers to the action's prompts so far: for each, the picked cards as indexes into the game's list.
    answers: Vec<Vec<i32>>,
    asked: Asked,
    /// The step's last resume point before the prompt, which the answer runs from; none: from `before`.
    resume: Option<Arc<Combat>>,
    /// Where the world was drawn, if it was drawn at one of the action's prompts (`draw_at_prompt`): every run of the
    /// action draws it there again.
    drawn: Option<Arc<crate::determinize::DrawAt>>,
}

/// The action that begins a combat taken in before its first turn (`Live::begin`).
const BEGIN: &str = "begin";

impl Live {
    /// A world from a combat written out as it begins, before its first turn (the host's export in C#'s
    /// StartCombatInternal, once the executor has finished what it ran, the bridge's CombatOpening), where the combat's start has run: where the player is first asked, at a card
    /// prompt that opens the combat or at the first turn's plays.
    pub fn begin(export: &str) -> Result<Live, String> {
        let mut world = <Live as ffi::World>::import(export)?;
        ffi::World::step(&mut world, BEGIN)
    }

    /// SL's world of a co-op combat (the user, 2026-11-22: 隊友當然是當全部死光的sl): the combat as written out, between
    /// actions, or as it begins (`opening`, then begun as `begin` does), with every other player killed first
    /// (`kill_teammates`), so that the local player's plays alone decide what follows. They die before anything is
    /// played: a card prompt's answer runs its action again from the combat before it, which holds them dead.
    pub fn alone(export: &str, opening: bool) -> Result<Live, String> {
        let mut world = <Live as ffi::World>::import(export)?;
        if let Err(stop) = kill_teammates(&mut world.combat) {
            return Err(format!("killing the teammates stopped: {stop:?}"));
        }
        if opening {
            return ffi::World::step(&mut world, BEGIN);
        }
        Ok(world)
    }

    /// This world, standing at a card prompt, as one NoSL sample (`seed`): where the prompt takes its answer, everything
    /// the player cannot see is drawn again (determinize.rs), from the draw pile's record the combat holds
    /// (`Extra::draw_log`, the host's at the prompt). What the prompt shows stays the real combat's.
    pub fn draw_at_prompt(&mut self, seed: u64) -> Result<(), String> {
        let log = self.combat.x.draw_log.clone().ok_or("the combat came with no draw pile record (sim.draw_log)")?;
        let prompted = self.prompted.as_mut().ok_or("a world between actions is drawn with determinize, not at a prompt")?;
        if prompted.drawn.is_some() {
            return Err("the world was drawn already".into());
        }
        prompted.drawn = Some(Arc::new(crate::determinize::DrawAt {
            answer: prompted.answers.len(),
            seed,
            log,
        }));
        Ok(())
    }
}

impl ffi::World for Live {
    fn import(export: &str) -> Result<Self, String> {
        Ok(Live {
            combat: crate::import::import(export)?,
            prompted: None,
        })
    }

    fn step(&mut self, action: &str) -> Result<Self, String> {
        // What runs: the action from the combat as it stands, or at a card prompt the prompted action again from
        // the combat before it, with one more answer.
        let (before, action, answers, resume, drawn) = match &self.prompted {
            None => (&self.combat, action.to_string(), Vec::new(), None, None),
            Some(prompted) => {
                let mut answers = prompted.answers.clone();
                answers.push(picks(action, &prompted.asked, &mut self.combat)?);
                (
                    &*prompted.before,
                    prompted.action.clone(),
                    answers,
                    prompted.resume.clone(),
                    prompted.drawn.clone(),
                )
            }
        };
        // A hook that pauses for the player is found again from what the step started from
        // (manual/hook_player_choice_context.rs): a step where one pauses is run again keeping it.
        let (mut c, stopped) = match &resume {
            None => match run(before, &action, &answers, false, &drawn)? {
                (c, _) if c.x.hooks.restart => run(before, &action, &answers, true, &drawn)?,
                ran => ran,
            },
            Some(point) => match run_from(point, &answers, false, &drawn) {
                (c, _) if c.x.hooks.restart => run_from(point, &answers, true, &drawn),
                ran => ran,
            },
        };
        // The step's last resume point, or the one it ran from.
        let point = c.x.resume_point.take().or(resume);
        let stopped = if c.choices.stopped {
            Err(Stop::Choice)
        } else {
            stopped
        };
        match stopped {
            Ok(()) => {
                if c.choices.next != answers.len() {
                    panic!(
                        "{action}: {} answers given, {} asked for",
                        answers.len(),
                        c.choices.next
                    );
                }
                // Every hook that paused for the player has run its rest, or never will: the combat's end cancels
                // every combat action not running (ActionQueueSet.CombatEnded), a hook's that waits too.
                if !c.x.hooks.started.is_null() || !c.x.hooks.asking.is_null() {
                    panic!("{action}: the step ended with a hook action started and not run (not ported)");
                }
                for &(context, _) in &c.x.hooks.waiting {
                    let waits = HookPlayerChoiceContextData::of(context, &c)._game_action;
                    if !matches!(
                        GameActionData::of(waits, &c).state,
                        GameActionState::Finished | GameActionState::Canceled
                    ) {
                        panic!("{action}: the step ended with a hook that waits for the player: {waits:?} (not ported)");
                    }
                }
                c.x.hooks = Default::default();
                // The world was drawn where it was: the next action's prompts are not that one.
                c.x.draw_at = None;
                Ok(Live {
                    combat: c,
                    prompted: None,
                })
            }
            Err(Stop::Dropped) => {
                unreachable!("{action}: a dropped setup's stop left the turn start")
            }
            Err(stop @ (Stop::Paused(_) | Stop::Resumed(_) | Stop::Pending(_) | Stop::Restart)) => {
                unreachable!("{action}: {stop:?} left the step")
            }
            Err(Stop::Choice) => {
                c.x.hooks = Default::default();
                c.x.draw_at = None;
                let asked =
                    c.x.asked
                        .take()
                        .expect("an action stopped for a choice without saying what it asks");
                let before = match &self.prompted {
                    None => Arc::new(self.combat.clone()),
                    Some(prompted) => prompted.before.clone(),
                };
                Ok(Live {
                    combat: c,
                    prompted: Some(Box::new(Prompted {
                        before,
                        action,
                        answers,
                        asked,
                        resume: point,
                        drawn,
                    })),
                })
            }
        }
    }

    fn apply(&mut self, action: &str) -> Result<(), String> {
        *self = self.step(action)?;
        Ok(())
    }

    fn combat(&mut self) -> &mut Combat {
        &mut self.combat
    }

    fn asked(&self) -> Option<&Asked> {
        self.prompted.as_deref().map(|p| &p.asked)
    }
}

/// The action from `before` with these answers, and how it stopped. `keep`: the combat keeps what the step started
/// from while it runs, which a hook that pauses for the player needs (manual/hook_player_choice_context.rs); without
/// it, such a hook stops the step at once (`hooks.restart`). `drawn`: where the world was drawn at one of the action's
/// prompts, which this run draws again (`Live::draw_at_prompt`); a replay of the step from what it started from does too.
fn run(
    before: &Combat,
    action: &str,
    answers: &[Vec<i32>],
    keep: bool,
    drawn: &Option<Arc<crate::determinize::DrawAt>>,
) -> Result<(Combat, R<()>), String> {
    let mut start_from = before.clone();
    start_from.x.draw_at = drawn.clone();
    let mut c = start_from.clone();
    c.choices = Choices {
        answers: answers.to_vec(),
        next: 0,
        stopped: false,
    };
    c.x.hooks = Default::default();
    if keep {
        c.x.step = Some(Arc::new(manual::hook_player_choice_context::StepStart {
            before: start_from,
            action: Some(action.to_string()),
        }));
    }
    c.x.points = true;
    c.x.resume_point = None;
    let stopped = act(action, &mut c)?.and_then(|()| pump(&mut c)).and_then(|()| cap(&mut c));
    c.x.step = None;
    c.x.points = false;
    Ok((c, stopped))
}

/// The step from its resume point `point` on (`resume_point`), with these answers, and how it stopped: as `run` from
/// the action's start, which got there with the answers the point had taken. `keep` as for `run`, with the point as
/// what the step started from.
fn run_from(point: &Arc<Combat>, answers: &[Vec<i32>], keep: bool, drawn: &Option<Arc<crate::determinize::DrawAt>>) -> (Combat, R<()>) {
    let mut start_from = (**point).clone();
    start_from.x.draw_at = drawn.clone();
    let mut c = start_from.clone();
    c.choices.answers = answers.to_vec();
    c.x.step = keep.then(|| {
        Arc::new(manual::hook_player_choice_context::StepStart {
            before: start_from,
            action: None,
        })
    });
    c.x.points = true;
    c.x.resume_point = None;
    let stopped = go_on(&mut c).and_then(|()| cap(&mut c));
    c.x.step = None;
    c.x.points = false;
    (c, stopped)
}

/// A resume point's combat goes on: the turn loop from inside its loop, where the point was taken, then the queued
/// actions and the turn loop as `pump` runs them, as the step did after that turn loop's move.
fn go_on(c: &mut Combat) -> R<()> {
    let manager = c.s.combat_manager__instance;
    if !manual::combat_manager::turn_loop(manager, c)? && c.x.hooks.started.is_null() {
        return Ok(());
    }
    pump(c)
}

/// A resume point of the running step, before the players' turn starts in the turn loop (manual/combat_manager.rs):
/// the combat as it stands, kept for a prompt after it to be answered from. Only where nothing of the game's code is
/// running and the step can be run on from the combat alone: no hook paused or waiting for the player, no card prompt
/// running, no turn start's setup running, not in a replay of the step. A point replaces the one before it.
pub(crate) fn resume_point(c: &mut Combat) {
    if c.x.turn_start.is_none() && quiet(c) {
        take_point(c, false);
    }
}

/// A resume point in the player's setup at the players' turn start, after the hand is drawn and before
/// `AfterPlayerTurnStart`'s hooks (manual/combat_manager.rs, `setup_player_turn`): as `resume_point`, with the turn
/// start running for one player and its setup not paused for a choice yet.
pub(crate) fn setup_point(c: &mut Combat) {
    if manual::combat_manager::setup_running(c) && quiet(c) {
        take_point(c, true);
    }
}

/// The combat as it stands, kept as the step's resume point. `setup`: taken in the turn start's setup.
fn take_point(c: &mut Combat, setup: bool) {
    c.x.resume_point = None;
    let mut point = c.clone();
    point.x.step = None;
    point.x.points = false;
    point.x.enter_loop = true;
    point.x.enter_setup = setup;
    c.x.resume_point = Some(Arc::new(point));
}

/// Whether the step can be run on from the combat alone, as far as the turn start: points are taken, nothing paused
/// or waiting for the player, no card prompt running, not in a replay of the step.
fn quiet(c: &Combat) -> bool {
    let hooks = &c.x.hooks;
    c.x.points
        && c.x.resume.is_none()
        && hooks.waiting.is_empty()
        && hooks.started.is_null()
        && hooks.asking.is_null()
        && hooks.asked_at_once.is_empty()
        && hooks.resumed.is_empty()
        && !hooks.restart
        && c.x.entries == 0
        && c.x.entry.is_none()
        && c.x.vakuu == 0
        && c.x.asked.is_none()
        && c.only.is_none()
        && !c.choices.stopped
}

/// "pick:0,2": the picked options of the prompt as the player sees it, as indexes into the game's list of cards.
/// "given:2,0": an answer as places in the game's own list, as the simulator's trace records one (a host's record of
/// the steps since it wrote the combat out).
fn picks(action: &str, asked: &Asked, c: &mut Combat) -> Result<Vec<i32>, String> {
    if let Some(list) = action.strip_prefix("given:").or(if action == "given" { Some("") } else { None }) {
        let mut given = Vec::new();
        for part in list.split(',').filter(|part| !part.is_empty()) {
            given.push(part.parse::<i32>().map_err(|_| format!("{action}: not an answer"))?);
        }
        let (choice, _) = prompt::canonical(asked, c);
        let mut seen = given.clone();
        seen.sort();
        seen.dedup();
        let count = given.len() as i32;
        if seen.len() != given.len() || given.iter().any(|&i| i < 0 || i as usize >= asked.cards.len()) || count < choice.min || count > choice.max {
            return Err(format!(
                "{action}: not an answer of this prompt ({} options, {} to {})",
                asked.cards.len(),
                choice.min,
                choice.max
            ));
        }
        return Ok(given);
    }
    let Some(list) =
        action
            .strip_prefix("pick:")
            .or(if action == "pick" { Some("") } else { None })
    else {
        return Err(format!("{action}: a card prompt is open, it takes a pick"));
    };
    let mut shown = Vec::new();
    for part in list.split(',').filter(|part| !part.is_empty()) {
        shown.push(
            part.parse::<i32>()
                .map_err(|_| format!("{action}: not a pick"))?,
        );
    }
    let (choice, game_index) = prompt::canonical(asked, c);
    if !choice.accepts(&shown) {
        return Err(format!(
            "{action}: not a pick of this prompt ({} options, {} to {})",
            choice.options.len(),
            choice.min,
            choice.max
        ));
    }
    Ok(shown
        .into_iter()
        .map(|i| game_index[i as usize] as i32)
        .collect())
}

/// Starts the action: a player's through `start`, or the combat's own start (`BEGIN`, C#: StartCombatInternal after its wait for the executor), which
/// runs the game's code here and may stop at a prompt in it. Refused (the outer error) as `start` refuses an action.
fn act(action: &str, c: &mut Combat) -> Result<R<()>, String> {
    if action != BEGIN {
        start(action, c)?;
        return Ok(Ok(()));
    }
    let manager = c.s.combat_manager__instance;
    let turn_state = CombatManagerData::of(manager, c)._turn_state;
    if turn_state.is_null() || CombatTurnStateData::of(turn_state, c).is_in_progress {
        return Err("begin: no combat waits to begin (it began already, or none was set up)".into());
    }
    Ok(manual::combat_manager::start_combat_internal(manager, turn_state, c))
}

/// Starts an action through the entry points the game's own screen uses (C#: CombatActions.Start).
fn start(action: &str, c: &mut Combat) -> Result<(), String> {
    let manager = c.s.combat_manager__instance;
    let state = g::combat::combat_manager::debug_only_get_state(manager, c);
    if state.is_null() {
        return Err("no combat".to_string());
    }
    let player = g::context::local_context::get_me__i_combat_state(state, c);
    if player.is_null() {
        return Err("the combat has no player with the local net id".to_string());
    }
    let mut parts = action.split(':');
    let number = |text: Option<&str>| -> Result<Option<u32>, String> {
        text.map(|t| {
            t.parse::<u32>()
                .map_err(|_| format!("{action}: not an action"))
        })
        .transpose()
    };
    let verb = parts.next().unwrap_or("");
    let (first, second) = (number(parts.next())?, number(parts.next())?);
    if parts.next().is_some() {
        return Err(format!("{action}: not an action"));
    }
    let target = |c: &mut Combat| -> Result<Creature, String> {
        let Some(uid) = second else {
            return Ok(Obj::NULL);
        };
        let mut creatures = g::combat::combat_state::enemies(state, c);
        creatures.extend(g::combat::combat_state::allies(state, c));
        creatures
            .into_iter()
            .find(|x| g::entities_creatures::creature::combat_id(*x, c) == Some(uid))
            .ok_or(format!("{action}: no creature {uid}"))
    };
    match (verb, first) {
        ("end_turn", None) => {
            let turn = g::entities_players::player_combat_state::turn_number(
                g::entities_players::player::player_combat_state(player, c),
                c,
            );
            let ended = g::game_actions::end_player_turn_action::new(player, turn, c);
            let synchronizer =
                RunManagerData::of(c.s.run_manager__instance, c).action_queue_synchronizer;
            manual::action_queue_synchronizer::request_enqueue(synchronizer, ended, c);
        }
        ("play", Some(uid)) => {
            let (found, card) = g::game_actions_multiplayer::net_combat_card_db::try_get_card(
                c.s.net_combat_card_db__instance,
                uid,
                c,
            );
            let hand = g::entities_players::player_combat_state::hand(
                g::entities_players::player::player_combat_state(player, c),
                c,
            );
            if !found
                || card.is_null()
                || !g::entities_cards::card_pile::cards(hand, c).contains(&card)
            {
                return Err(format!("{action}: no card {uid} in hand"));
            }
            let target = target(c)?;
            if !g::models::card_model::try_manual_play(card, target, c) {
                return Err(format!("{action}: illegal play"));
            }
        }
        ("potion", Some(slot)) => {
            let slots = g::entities_players::player::potion_slots(player, c);
            let potion = slots.get(slot as usize).copied().unwrap_or(Obj::NULL);
            if potion.is_null() {
                return Err(format!("{action}: no potion in slot {slot}"));
            }
            let target = target(c)?;
            g::models::potion_model::enqueue_manual_use(potion, target, c);
        }
        _ => return Err(format!("{action}: not an action")),
    }
    Ok(())
}

/// The rounds a fight may last. The game has no limit; the simulator the engine stands in for has this one
/// (FightCap in the simulator), so that a player who can no longer hurt the enemies does not play forever.
const MAX_ROUNDS: i32 = 80;

/// The fight round cap: when the player can act again and the fight is past its last round, the (local) player is killed the
/// way the game's own kill command does, forced so that nothing revives them, and the win condition is checked.
fn cap(c: &mut Combat) -> R<()> {
    let manager = c.s.combat_manager__instance;
    if !g::combat::combat_manager::is_in_progress(manager, c) {
        return Ok(());
    }
    let state = g::combat::combat_manager::debug_only_get_state(manager, c);
    if is_null(&state) || g::combat::combat_state::round_number(state, c) <= MAX_ROUNDS {
        return Ok(());
    }
    let (_, player) = crate::ffi::live(c);
    let creature = g::entities_players::player::creature(player, c);
    if g::entities_creatures::creature::is_dead(creature, c) {
        return Ok(());
    }
    g::commands::creature_cmd::kill__creature_bool(creature, true, c)?;
    g::combat::combat_manager::check_win_condition__0(manager, c)?;
    pump(c)
}

/// Every player but the local one killed the way the game's own kill command does, forced so that nothing revives
/// them, and the win condition checked, as `cap` kills the local player; then what their deaths queued runs. Nothing
/// in a combat of one player, or where the others are dead already.
fn kill_teammates(c: &mut Combat) -> R<()> {
    let (st, me) = crate::ffi::live(c);
    let mut killed = false;
    for player in g::combat::combat_state::players(st, c) {
        if player == me {
            continue;
        }
        let creature = g::entities_players::player::creature(player, c);
        if g::entities_creatures::creature::is_dead(creature, c) {
            continue;
        }
        g::commands::creature_cmd::kill__creature_bool(creature, true, c)?;
        killed = true;
    }
    if !killed {
        return Ok(());
    }
    g::combat::combat_manager::check_win_condition__0(c.s.combat_manager__instance, c)?;
    pump(c)
}

/// Runs the queued actions and the turn loop in turn until neither can go on. A hook that paused for the player in
/// the turn loop's last move has its action started (manual/hook_player_choice_context.rs): its rest runs then, and
/// the turn loop may move again after it.
fn pump(c: &mut Combat) -> R<()> {
    let executor = RunManagerData::of(c.s.run_manager__instance, c).action_executor;
    let manager = c.s.combat_manager__instance;
    loop {
        manual::action_executor::execute_actions(executor, c)?;
        if !manual::combat_manager::turn_loop(manager, c)? && c.x.hooks.started.is_null() {
            return Ok(());
        }
    }
}

/// The step once more from what it started from, for a hook that paused in it (manual/hook_player_choice_context.rs,
/// `resume`): its action from the combat before it, or the turn loop from a resume point. The replay goes over to the
/// step's combat at the hook's choice, and stops at the hook's end (`Stop::Resumed`).
pub(crate) fn replay(step: &manual::hook_player_choice_context::StepStart, c: &mut Combat) -> R<()> {
    match &step.action {
        Some(action) => {
            match act(action, c) {
                Err(refused) => panic!("a replay of `{action}` was refused: {refused}"),
                Ok(started) => started?,
            }
            pump(c)?;
        }
        None => go_on(c)?,
    }
    cap(c)
}
