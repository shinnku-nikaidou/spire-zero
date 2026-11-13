//! MegaCrit.Sts2.Core.GameActions.GameAction: running one action. The game pauses an action at a card prompt and
//! resumes it; here an action that reaches a prompt with no answer stops, and is run again with the answer.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::rt::*;

/// C#: GameAction.Execute.
pub fn execute(me: GameAction, c: &mut Combat) -> R<()> {
    match GameActionData::of(me, c).state {
        GameActionState::WaitingForExecution => {}
        state => panic!(
            "GameAction.Execute: {:?} in the state {:?}",
            g::kinds::kind_of(me, c),
            state
        ),
    }
    GameActionData::of_mut(me, c).state = GameActionState::Executing;
    g::dispatch::game_action::execute_action(me, c)?;
    GameActionData::of_mut(me, c).state = GameActionState::Finished;
    // C#: AfterFinished, which the queue set listens to (OnEnqueued).
    let queues = RunManagerData::of(c.s.run_manager__instance, c).action_queue_set;
    g::game_actions_multiplayer::action_queue_set::pop_action(queues, me, c);
    Ok(())
}

/// C#: GameAction.WaitForActionToResumeExecutingAfterPlayerChoice: the answer is already there (see `execute`).
pub fn wait_for_action_to_resume_executing_after_player_choice(
    _me: GameAction,
    _c: &mut Combat,
) -> R<()> {
    Ok(())
}
