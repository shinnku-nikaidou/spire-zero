//! MegaCrit.Sts2.Core.GameActions.Multiplayer.GameActionPlayerChoiceContext: the choices of a game action (a card's
//! effect). The game pauses the action for the choice and resumes it; here the action stops and is run again.

use crate::r#gen::enums::PlayerChoiceOptions;
use crate::r#gen::kinds::*;
use crate::rt::*;

/// C#: GameActionPlayerChoiceContext.SignalPlayerChoiceBegun.
pub fn signal_player_choice_begun(
    _me: GameActionPlayerChoiceContext,
    _chooser: Player,
    _options: PlayerChoiceOptions,
    _c: &mut Combat,
) -> R<()> {
    Ok(())
}

/// C#: GameActionPlayerChoiceContext.SignalPlayerChoiceEnded.
pub fn signal_player_choice_ended(_me: GameActionPlayerChoiceContext, _c: &mut Combat) -> R<()> {
    Ok(())
}
