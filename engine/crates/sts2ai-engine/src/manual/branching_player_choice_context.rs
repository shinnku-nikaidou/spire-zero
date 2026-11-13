//! MegaCrit.Sts2.Core.GameActions.Multiplayer.BranchingPlayerChoiceContext: the context around a card's or a
//! potion's own effect. In the game it waits for the effect to end or to branch off into a context of another
//! player; with one player there is no other, and the effect has ended or stopped the step by the time it is here.

use crate::r#gen::kinds::*;
use crate::rt::*;

/// C#: BranchingPlayerChoiceContext.AssignTaskAndWaitForPauseOrCompletion.
pub fn assign_task_and_wait_for_pause_or_completion(
    _me: BranchingPlayerChoiceContext,
    task: R<()>,
    _c: &mut Combat,
) -> R<()> {
    task
}
