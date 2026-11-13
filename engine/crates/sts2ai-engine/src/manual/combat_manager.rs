//! MegaCrit.Sts2.Core.Combat.CombatManager: the turn loop. In the game it is one long task that waits twice a round
//! for a signal (every player ready to end the turn; every player ready for the enemies' turn) which a game action
//! gives. Here nothing waits: the loop is run from where it stands as far as it goes (`turn_loop`), and where it
//! stands is kept beside the combat (`Extra::waits`, the two signals). The rest of the class is translated.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::rt::*;

/// Where the turn loop stands when it cannot go on without the player.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Waits {
    /// For every player to be ready to end the turn (the game: `await endTurnSignalSource.Task`). The loop of an
    /// imported combat stands here: an export is taken where the player is asked.
    #[default]
    EndTurn,
    /// For every player to be ready for the enemies' turn (`await beginEnemyTurnSignalSource.Task`).
    BeginEnemyTurn,
}

/// C#: CombatManager.StartCombatInternal, up to where the first turn waits for the player.
pub fn start_combat_internal(
    me: CombatManager,
    turn_state: CombatTurnState,
    c: &mut Combat,
) -> R<()> {
    let run = c.s.run_manager__instance;
    let executor = RunManagerData::of(run, c).action_executor;
    g::game_actions::action_executor::unpause(executor, c);
    super::action_executor::execute_actions(executor, c)?;
    g::game_actions::action_executor::pause(executor, c);
    let state = CombatTurnStateData::of(turn_state, c).state;
    for creature in g::combat::combat_state::creatures(state, c) {
        g::combat::combat_manager::after_creature_added__creature_combat_state(creature, state, c)?;
    }
    let synchronizer = RunManagerData::of(run, c).action_queue_synchronizer;
    g::game_actions_multiplayer::action_queue_synchronizer::set_combat_state(
        synchronizer,
        ActionSynchronizerCombatState::NotPlayPhase,
        c,
    );
    CombatTurnStateData::of_mut(turn_state, c).is_in_progress = true;
    CombatTurnStateData::of_mut(turn_state, c).is_starting = false;
    let run_state = g::combat::combat_state::run_state(state, c);
    g::hooks::hook::before_combat_start(run_state, state, c)?;
    g::combat::combat_manager::start_turn(me, turn_state, Fn0::NULL, c)?;
    c.x.waits = Waits::EndTurn;
    Ok(())
}

/// The turn loop from where it stands (C#: the rest of StartCombatInternal, with AwaitTurnEndAndSwitchSides), as far
/// as it goes without the player. False when it could not move: it waits for a signal that is not there.
pub fn turn_loop(me: CombatManager, c: &mut Combat) -> R<bool> {
    let turn_state = CombatManagerData::of(me, c)._turn_state;
    if turn_state.is_null() {
        return Ok(false);
    }
    let in_progress = |c: &Combat| CombatTurnStateData::of(turn_state, c).is_in_progress;
    let mut action_during_enemy_turn = Fn0::NULL;
    match c.x.waits {
        Waits::EndTurn => {
            let Some(signal) = c.x.end_turn_signal.take() else {
                return Ok(false);
            };
            if in_progress(c) {
                g::combat::combat_manager::after_all_players_ready_to_end_turn(
                    me, turn_state, signal, c,
                )?;
            }
            if in_progress(c) {
                c.x.waits = Waits::BeginEnemyTurn;
                return Ok(true);
            }
        }
        Waits::BeginEnemyTurn => {
            let Some(action) = c.x.begin_enemy_turn_signal.take() else {
                return Ok(false);
            };
            if in_progress(c) {
                g::combat::combat_manager::after_all_players_ready_to_begin_enemy_turn(
                    me, turn_state, c,
                )?;
                action_during_enemy_turn = action;
            }
        }
    }
    // C#: while (turnState.IsLive), up to the next wait.
    while g::combat::combat_turn_state::is_live(turn_state, c) {
        let state = CombatTurnStateData::of(turn_state, c).state;
        if g::combat::combat_state::current_side(state, c) == CombatSide::Player {
            g::combat::combat_manager::start_turn(me, turn_state, Fn0::NULL, c)?;
            action_during_enemy_turn = Fn0::NULL;
            if in_progress(c)
                && g::combat::combat_state::current_side(state, c) == CombatSide::Player
            {
                c.x.waits = Waits::EndTurn;
                return Ok(true);
            }
        } else {
            g::combat::combat_manager::start_turn(
                me,
                turn_state,
                std::mem::take(&mut action_during_enemy_turn),
                c,
            )?;
        }
    }
    Ok(true)
}

/// C#: CombatManager.SetReadyToEndTurn. The signal is kept for the turn loop.
pub fn set_ready_to_end_turn(
    me: CombatManager,
    player: Player,
    can_back_out: bool,
    action_during_enemy_turn: Fn0<R<()>>,
    c: &mut Combat,
) {
    let turn_state = CombatManagerData::of(me, c)._turn_state;
    if turn_state.is_null() {
        return;
    }
    if CombatTurnStateData::of(turn_state, c)
        .players_ready_to_end_turn
        .contains(&player)
    {
        return;
    }
    CombatTurnStateData::of_mut(turn_state, c)
        .players_ready_to_end_turn
        .add(player);
    if !can_back_out {
        CombatTurnStateData::of_mut(turn_state, c)
            .players_that_cannot_un_end_turn
            .add(player);
    }
    if g::combat::combat_manager::all_players_ready_to_end_turn__combat_turn_state(
        me, turn_state, c,
    ) {
        let combat_state = g::entities_players::player::player_combat_state(player, c);
        let scheduled_turn_number = if combat_state.is_null() {
            -1
        } else {
            g::entities_players::player_combat_state::turn_number(combat_state, c)
        };
        // C#: endTurnSignalSource?.TrySetResult(..): the first result stands.
        if c.x.end_turn_signal.is_none() {
            let signal = g::combat::end_turn_signal::new(
                scheduled_turn_number,
                player,
                action_during_enemy_turn,
                c,
            );
            c.x.end_turn_signal = Some(signal);
        }
    }
}

/// C#: CombatManager.SetReadyToBeginEnemyTurn. The signal is kept for the turn loop.
pub fn set_ready_to_begin_enemy_turn(
    me: CombatManager,
    player: Player,
    action_during_enemy_turn: Fn0<R<()>>,
    c: &mut Combat,
) {
    let turn_state = CombatManagerData::of(me, c)._turn_state;
    if turn_state.is_null() || !CombatTurnStateData::of(turn_state, c).is_in_progress {
        return;
    }
    if !CombatTurnStateData::of_mut(turn_state, c)
        .players_ready_to_begin_enemy_turn
        .add(player)
    {
        return;
    }
    // One player: the game is single-player here (NetGameType.Singleplayer).
    let state = CombatTurnStateData::of(turn_state, c).state;
    if g::combat::combat_state::current_side(state, c) == CombatSide::Player
        && c.x.begin_enemy_turn_signal.is_none()
    {
        c.x.begin_enemy_turn_signal = Some(action_during_enemy_turn);
    }
}

/// C#: CombatManager.WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction. The turn loop runs between actions here,
/// never inside one: there is nothing to wait for.
pub fn wait_until_queue_is_empty_or_waiting_on_non_player_driven_action(
    _me: CombatManager,
    _turn_state: CombatTurnState,
    _c: &mut Combat,
) -> R<()> {
    Ok(())
}

/// C#: CombatManager.DoTurnEndCards. The game starts every card's steps as tasks that wait on each other and on the
/// screen; with no screen each card goes to the play pile, has its effects, and goes to its pile, one card at a time.
pub fn do_turn_end_cards(
    me: CombatManager,
    turn_state: CombatTurnState,
    turn_end_cards: Vec<CardModel>,
    choice_context: PlayerChoiceContext,
    c: &mut Combat,
) -> R<()> {
    for card in turn_end_cards {
        g::combat::combat_manager::add_turn_end_card_to_play_pile_with_delay(me, card, 0.0, c)?;
        if !g::models::card_model::has_been_removed_from_state(card, c) {
            let resolved = g::combat::combat_manager::resolve_turn_end_card_effects(
                me,
                card,
                choice_context,
                Ok(()),
                c,
            );
            g::combat::combat_manager::tween_turn_end_card_to_result_pile(
                me, turn_state, resolved, c,
            )?;
        }
    }
    Ok(())
}
