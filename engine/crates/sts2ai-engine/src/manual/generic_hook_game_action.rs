//! MegaCrit.Sts2.Core.GameActions.GenericHookGameAction: the action a hook that asked the player waits for
//! (manual/hook_player_choice_context.rs).

use crate::r#gen as g;
use crate::r#gen::kinds::*;
use crate::rt::*;

/// C#: GenericHookGameAction.ExecuteAction: the rest of the hook that paused for this action.
pub fn execute_action(me: GenericHookGameAction, c: &mut Combat) -> R<()> {
    let context = g::game_actions::generic_hook_game_action::choice_context(me, c);
    super::hook_player_choice_context::resume(context, c)
}
