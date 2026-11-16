//! MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext: the choices of a hook (the start and the end
//! of a turn). In the game a hook that asks the player is left waiting while the turn goes on, and finishes later as
//! an action of its own. A player's setup at the start of a turn does so here too: at its first choice the rest of the
//! turn start runs (manual/combat_manager.rs, `start_turn`), then the choice is asked. Any other hook that reaches a
//! prompt with no answer stops the whole step, which is run again with the answer: the hook's rest then runs where the
//! hook stands, before what follows it.

use crate::r#gen::enums::PlayerChoiceOptions;
use crate::r#gen::kinds::*;
use crate::rt::*;

/// C#: HookPlayerChoiceContext.AssignTaskAndWaitForPauseOrCompletion: true when the task ran to its end.
pub fn assign_task_and_wait_for_pause_or_completion(
    _me: HookPlayerChoiceContext,
    task: R<()>,
    _c: &mut Combat,
) -> R<bool> {
    task?;
    Ok(true)
}

/// C#: HookPlayerChoiceContext.WaitForPauseOrCompletionWithoutAssigningTask.
pub fn wait_for_pause_or_completion_without_assigning_task(
    _me: HookPlayerChoiceContext,
    task: R<()>,
    _c: &mut Combat,
) -> R<bool> {
    task?;
    Ok(true)
}

/// C#: HookPlayerChoiceContext.WaitForCompletion.
pub fn wait_for_completion(_me: HookPlayerChoiceContext, _c: &mut Combat) -> R<()> {
    Ok(())
}

/// C#: HookPlayerChoiceContext.SignalPlayerChoiceBegun. The first choice of a turn start's setup pauses it: the rest
/// of the turn start runs here, before the choice is asked. Nothing else is paused for a choice here.
pub fn signal_player_choice_begun(
    me: HookPlayerChoiceContext,
    _chooser: Player,
    _options: PlayerChoiceOptions,
    c: &mut Combat,
) -> R<()> {
    if crate::manual::combat_manager::turn_start_pauses(me, c) {
        crate::manual::combat_manager::turn_start_rest(c)?;
    }
    Ok(())
}

/// C#: HookPlayerChoiceContext.SignalPlayerChoiceEnded.
pub fn signal_player_choice_ended(_me: HookPlayerChoiceContext, _c: &mut Combat) -> R<()> {
    Ok(())
}
