//! MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer: the members that go through the network
//! service in the game. The engine is the single-player case: what is asked for is enqueued.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::rt::*;

/// C#: ActionQueueSynchronizer.RequestEnqueue (NetGameType.Singleplayer).
pub fn request_enqueue(me: ActionQueueSynchronizer, action: GameAction, c: &mut Combat) {
    if g::dispatch::game_action::action_type(action, c) == GameActionType::CombatPlayPhaseOnly
        && ActionQueueSynchronizerData::of(me, c).combat_state
            == ActionSynchronizerCombatState::NotPlayPhase
    {
        ActionQueueSynchronizerData::of_mut(me, c)
            ._requested_actions_waiting_for_player_turn
            .push(action);
        return;
    }
    g::game_actions_multiplayer::action_queue_synchronizer::enqueue_or_hold_until_combat_end(
        me, action, c,
    );
}

/// C#: ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice: an action of the engine is never paused.
pub fn request_resume_action_after_player_choice(
    _me: ActionQueueSynchronizer,
    _action: GameAction,
    _c: &mut Combat,
) {
}
