//! MegaCrit.Sts2.Core.GameActions.ActionExecutor: the loop that runs the queued actions. In the game it is a task
//! that waits while the executor is paused; here it returns then, and is called again when the turn loop has moved.

use crate::r#gen as g;
use crate::r#gen::enums::GameActionState;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::rt::*;

/// C#: ActionExecutor.ExecuteActions: every action that is ready, until none is or the executor is paused.
///
/// A hook action an idle executor started at once (`start_at_once`) goes on here, the next time the executor runs:
/// the hook's rest runs then, once the code that paused the hook has gone on.
pub fn execute_actions(me: ActionExecutor, c: &mut Combat) -> R<()> {
    let queues = ActionExecutorData::of(me, c)._action_queue_set;
    loop {
        let started = c.x.hooks.started;
        if !started.is_null() && c.x.hooks.asking.is_null() {
            c.x.hooks.started = Obj::NULL;
            super::game_action::execute_started(started, c)?;
            finished(me, started, c)?;
            continue;
        }
        if ActionExecutorData::of(me, c)._is_paused {
            return Ok(());
        }
        let action = g::game_actions_multiplayer::action_queue_set::get_ready_action(queues, c);
        if action.is_null() {
            return Ok(());
        }
        if GameActionData::of(action, c).state == GameActionState::Canceled {
            continue;
        }
        ActionExecutorData::of_mut(me, c).currently_running_action = action;
        super::game_action::execute(action, c)?;
        finished(me, action, c)?;
    }
}

/// C#: ActionExecutor.ActionQueueChanged, for the action of a hook that pauses for the player, just enqueued
/// (manual/hook_player_choice_context.rs): an executor that is idle starts at once, and when the hook's action is the
/// one it starts, starts it and waits on the hook (the game's one thread goes on in the hook). Whether it did.
pub fn start_at_once(me: ActionExecutor, action: GameAction, c: &mut Combat) -> bool {
    let executor = ActionExecutorData::of(me, c);
    if !executor.currently_running_action.is_null() || executor._is_paused {
        return false;
    }
    let queues = executor._action_queue_set;
    if g::game_actions_multiplayer::action_queue_set::get_ready_action(queues, c) != action {
        return false;
    }
    ActionExecutorData::of_mut(me, c).currently_running_action = action;
    super::game_action::start(action, c);
    c.x.hooks.started = action;
    true
}

/// A hook action the executor started at once ends with its hook, which ran to its end without asking the player
/// (manual/hook_player_choice_context.rs); the executor goes on as its loop does.
pub fn end_started(me: ActionExecutor, action: GameAction, c: &mut Combat) -> R<()> {
    c.x.hooks.started = Obj::NULL;
    super::game_action::finish(action, c);
    finished(me, action, c)?;
    execute_actions(me, c)
}

/// What the loop does after an action has run.
fn finished(me: ActionExecutor, action: GameAction, c: &mut Combat) -> R<()> {
    g::game_actions::action_executor::after_action_finished(me, action, c);
    let manager = c.s.combat_manager__instance;
    let kind = g::kinds::kind_of(action, c);
    if g::combat::combat_manager::is_in_progress(manager, c)
        && kind != Kind::EndPlayerTurnAction
        && kind != Kind::ReadyToBeginEnemyTurnAction
    {
        g::combat::combat_manager::check_win_condition__0(manager, c)?;
    }
    Ok(())
}

/// C#: ActionExecutor.FinishedExecutingActions: here the actions have run when the call that ran them returns.
pub fn finished_executing_actions(_me: ActionExecutor, _c: &mut Combat) -> R<()> {
    Ok(())
}

/// C#: ActionExecutor.IsRunning: whether the loop is in the middle of the queue.
pub fn is_running(me: ActionExecutor, c: &mut Combat) -> bool {
    !ActionExecutorData::of(me, c)
        .currently_running_action
        .is_null()
}
