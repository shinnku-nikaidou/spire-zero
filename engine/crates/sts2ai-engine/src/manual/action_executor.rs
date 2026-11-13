//! MegaCrit.Sts2.Core.GameActions.ActionExecutor: the loop that runs the queued actions. In the game it is a task
//! that waits while the executor is paused; here it returns then, and is called again when the turn loop has moved.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::rt::*;

/// C#: ActionExecutor.ExecuteActions: every action that is ready, until none is or the executor is paused.
pub fn execute_actions(me: ActionExecutor, c: &mut Combat) -> R<()> {
    let queues = ActionExecutorData::of(me, c)._action_queue_set;
    loop {
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
        g::game_actions::action_executor::after_action_finished(me, action, c);
        let manager = c.s.combat_manager__instance;
        let kind = g::kinds::kind_of(action, c);
        if g::combat::combat_manager::is_in_progress(manager, c)
            && kind != Kind::EndPlayerTurnAction
            && kind != Kind::ReadyToBeginEnemyTurnAction
        {
            g::combat::combat_manager::check_win_condition__0(manager, c)?;
        }
    }
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
