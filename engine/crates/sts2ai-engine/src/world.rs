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
//! given so far, and shows the combat as it stood at the prompt. The answer runs the action again from the start
//! with one more answer; the random streams are part of the combat, so it runs the same way up to the prompt.

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
        let (before, action, answers) = match &self.prompted {
            None => (&self.combat, action.to_string(), Vec::new()),
            Some(prompted) => {
                let mut answers = prompted.answers.clone();
                answers.push(picks(action, &prompted.asked, &mut self.combat)?);
                (&*prompted.before, prompted.action.clone(), answers)
            }
        };
        // A hook that pauses for the player is found again from what the step started from
        // (manual/hook_player_choice_context.rs): a step where one pauses is run again keeping it.
        let (mut c, stopped) = match run(before, &action, &answers, false)? {
            (c, _) if c.x.hooks.restart => run(before, &action, &answers, true)?,
            ran => ran,
        };
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
                // Every hook that paused for the player has run its rest.
                if !c.x.hooks.started.is_null() || !c.x.hooks.asking.is_null() {
                    panic!("{action}: the step ended with a hook action started and not run (not ported)");
                }
                for &(context, _) in &c.x.hooks.waiting {
                    let waits = HookPlayerChoiceContextData::of(context, &c)._game_action;
                    if GameActionData::of(waits, &c).state != GameActionState::Finished {
                        panic!("{action}: the step ended with a hook that waits for the player: {waits:?} (not ported)");
                    }
                }
                c.x.hooks = Default::default();
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
/// it, such a hook stops the step at once (`hooks.restart`).
fn run(before: &Combat, action: &str, answers: &[Vec<i32>], keep: bool) -> Result<(Combat, R<()>), String> {
    let mut c = before.clone();
    c.choices = Choices {
        answers: answers.to_vec(),
        next: 0,
        stopped: false,
    };
    c.x.hooks = Default::default();
    if keep {
        c.x.step = Some(Arc::new(manual::hook_player_choice_context::StepStart {
            before: before.clone(),
            action: action.to_string(),
        }));
    }
    start(action, &mut c)?;
    let stopped = pump(&mut c).and_then(|()| cap(&mut c));
    c.x.step = None;
    Ok((c, stopped))
}

/// "pick:0,2": the picked options of the prompt as the player sees it, as indexes into the game's list of cards.
fn picks(action: &str, asked: &Asked, c: &mut Combat) -> Result<Vec<i32>, String> {
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

/// The fight round cap: when the player can act again and the fight is past its last round, the player is killed the
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
    let player = g::combat::combat_state::players(state, c)[0];
    let creature = g::entities_players::player::creature(player, c);
    if g::entities_creatures::creature::is_dead(creature, c) {
        return Ok(());
    }
    g::commands::creature_cmd::kill__creature_bool(creature, true, c)?;
    g::combat::combat_manager::check_win_condition__0(manager, c)?;
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

/// The step `action` once more from the combat it started from, for a hook that paused in it
/// (manual/hook_player_choice_context.rs, `resume`): the replay goes over to the step's combat at the hook's choice,
/// and stops at the hook's end (`Stop::Resumed`).
pub(crate) fn replay(action: &str, c: &mut Combat) -> R<()> {
    if let Err(refused) = start(action, c) {
        panic!("a replay of `{action}` was refused: {refused}");
    }
    pump(c)?;
    cap(c)
}
