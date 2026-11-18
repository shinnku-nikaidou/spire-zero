//! MegaCrit.Sts2.Core.Combat.CombatManager: the turn loop. In the game it is one long task that waits twice a round
//! for a signal (every player ready to end the turn; every player ready for the enemies' turn) which a game action
//! gives. Here nothing waits: the loop is run from where it stands as far as it goes (`turn_loop`), and where it
//! stands is kept beside the combat (`Extra::waits`, the two signals). The turn start is written by hand too
//! (`start_turn`), for where a player's setup pauses for a choice. The rest of the class is translated.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::manual as m;
use crate::rt;
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
    m::combat_manager::start_turn(me, turn_state, Fn0::NULL, c)?;
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
    // A resume point's combat (world.rs) goes on inside the loop below, where the point was taken: before the
    // players' turn start, with nothing to run during an enemies' turn.
    let resumed = std::mem::take(&mut c.x.enter_loop);
    if !resumed {
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
    }
    let mut point = !resumed;
    // C#: while (turnState.IsLive), up to the next wait.
    while g::combat::combat_turn_state::is_live(turn_state, c) {
        let state = CombatTurnStateData::of(turn_state, c).state;
        if g::combat::combat_state::current_side(state, c) == CombatSide::Player {
            // The players' turn is about to start: nothing of the game's code is running, so a prompt of the turn
            // start is answered from here (world.rs), not from the action that ended the players' turn.
            if std::mem::replace(&mut point, true) {
                crate::world::resume_point(c);
            }
            m::combat_manager::start_turn(me, turn_state, Fn0::NULL, c)?;
            action_during_enemy_turn = Fn0::NULL;
            if in_progress(c)
                && g::combat::combat_state::current_side(state, c) == CombatSide::Player
            {
                c.x.waits = Waits::EndTurn;
                return Ok(true);
            }
        } else {
            m::combat_manager::start_turn(
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

/// The turn start that is running, while a player's setup runs (`start_turn`).
#[derive(Clone, Debug)]
pub struct TurnStart {
    me: CombatManager,
    turn_state: CombatTurnState,
    creatures: Vec<Creature>,
    players: Vec<Player>,
    /// The choice context of the setup that is running.
    setup: HookPlayerChoiceContext,
    /// Set once that setup has paused for a choice, where the rest of the turn start runs (`turn_start_rest`).
    paused: bool,
}

/// C#: CombatManager.StartTurn. The game starts each player's setup (`SetupPlayerTurn`: the energy, the hand, then
/// `AfterPlayerTurnStart`) and waits only until it ends or pauses. A setup that asks the player (Tools of the Trade's
/// discard) pauses at its first choice (`HookPlayerChoiceContext.SignalPlayerChoiceBegun`); the rest of the turn start
/// runs (`AfterSideTurnStart`, the orbs, the ready check, the win check, the play phase), and only then is the choice
/// shown. The setup's rest and its `RunAutoPrePlayPhase` run after the answer. Here the rest of the turn start runs
/// where the setup pauses (`turn_start_rest`, called from hook_player_choice_context.rs), so the choice is asked, and
/// the combat shown at it, as in the game. A setup that asks nothing runs to its end, and the rest after it.
pub fn start_turn(
    me: CombatManager,
    turn_state: CombatTurnState,
    action_during_enemy_turn: Fn0<R<()>>,
    c: &mut Combat,
) -> R<()> {
    if !g::combat::combat_turn_state::is_in_progress(turn_state, c) {
        return Ok(());
    }
    // A resume point's combat taken in the setup (`setup_player_turn`) goes on there: the turn start before it ran.
    let (creatures, players) = match (c.x.enter_setup, &c.x.turn_start) {
        (true, Some(running)) => (running.creatures.clone(), running.players.clone()),
        _ => turn_start_begun(me, turn_state, c)?,
    };
    let mut setups = Vec::new();
    let mut paused = false;
    for &player in &players {
        let Some(net_id) = c.s.local_context__net_id else {
            continue;
        };
        let context = match (c.x.enter_setup, &c.x.turn_start) {
            (true, Some(running)) => running.setup,
            _ => {
                let context = g::game_actions_multiplayer::hook_player_choice_context::new__player_u64_game_action_type(
                    player,
                    net_id,
                    GameActionType::CombatPlayPhaseOnly,
                    c,
                );
                c.x.turn_start = Some(TurnStart {
                    me,
                    turn_state,
                    creatures: creatures.clone(),
                    players: players.clone(),
                    setup: context,
                    paused: false,
                });
                context
            }
        };
        let setup = setup_player_turn(me, turn_state, player, context, c);
        paused = c.x.turn_start.take().is_some_and(|t| t.paused);
        match &setup {
            // The rest of the turn start ended the combat, or every player was ready to end the turn: the game never
            // runs the paused setup's rest.
            Err(Stop::Dropped) => return Ok(()),
            Err(stop) => return Err(stop.clone()),
            Ok(()) => {}
        }
        setups.push((player, context, setup));
    }
    turn_start_end(me, turn_state, action_during_enemy_turn, &creatures, &players, setups, paused, c)
}

/// C#: CombatManager.StartTurn up to the players' setups: who starts the turn, the turn's start for each, the block
/// cleared.
fn turn_start_begun(me: CombatManager, turn_state: CombatTurnState, c: &mut Combat) -> R<(Vec<Creature>, Vec<Player>)> {
    let state = g::combat::combat_turn_state::state(turn_state, c);
    let players_side =
        |c: &mut Combat| g::combat::combat_state::current_side(state, c) == CombatSide::Player;
    g::combat::combat_manager::set_phase_for_all_players(state, PlayerTurnPhase::None, c);
    let extra_turn = !CombatTurnStateData::of(turn_state, c)
        .players_taking_extra_turn
        .is_empty();
    let (creatures, players) = if players_side(c) && extra_turn {
        let players = g::combat::combat_turn_state::players_taking_extra_turn(turn_state, c);
        let mut creatures = Vec::new();
        for &player in &players {
            creatures.push(g::entities_players::player::creature(player, c));
        }
        (creatures, players)
    } else {
        let creatures = g::combat::combat_state::creatures_on_current_side(state, c);
        let players = if players_side(c) {
            g::combat::combat_state::players(state, c)
        } else {
            Vec::new()
        };
        (creatures, players)
    };
    for &creature in &creatures {
        let side = g::combat::combat_state::current_side(state, c);
        g::entities_creatures::creature::before_turn_start(creature, side, c);
    }
    let side = g::combat::combat_state::current_side(state, c);
    g::hooks::hook::before_side_turn_start(state, side, creatures.clone(), c)?;
    if players_side(c) {
        g::combat::combat_manager::set_phase_for_all_players(state, PlayerTurnPhase::Start, c);
        g::combat::combat_manager::set_player_actions_disabled(me, false, c);
        let turn = CombatTurnStateData::of_mut(turn_state, c);
        turn.players_ready_to_end_turn.clear();
        turn.players_that_cannot_un_end_turn.clear();
        turn.players_ready_to_begin_enemy_turn.clear();
        // The turn's number names its banner only, which is presentation.
        if !extra_turn {
            // C#: foreach over turnState.State.Enemies, which throws if the list changes while it is walked (the
            // game's combat then sticks); here the walk goes over the list as it began.
            let enemies = g::combat::combat_state::enemies(state, c);
            for &enemy in enemies.iter() {
                let player_creatures = g::combat::combat_state::player_creatures(state, c);
                g::entities_creatures::creature::prepare_for_next_turn(
                    enemy,
                    player_creatures,
                    true,
                    c,
                );
            }
        }
    }
    for &creature in &creatures {
        let side = g::combat::combat_state::current_side(state, c);
        g::entities_creatures::creature::after_turn_start(creature, side, c)?;
    }
    for &creature in &creatures {
        g::hooks::hook::after_block_cleared(state, creature, c)?;
    }
    Ok((creatures, players))
}

/// C#: CombatManager.StartTurn after the players' setups.
#[allow(clippy::too_many_arguments)]
fn turn_start_end(
    me: CombatManager,
    turn_state: CombatTurnState,
    action_during_enemy_turn: Fn0<R<()>>,
    creatures: &[Creature],
    players: &[Player],
    setups: Vec<(Player, HookPlayerChoiceContext, R<()>)>,
    paused: bool,
    c: &mut Combat,
) -> R<()> {
    let state = g::combat::combat_turn_state::state(turn_state, c);
    let players_side =
        |c: &mut Combat| g::combat::combat_state::current_side(state, c) == CombatSide::Player;
    if !paused && !side_turn_started(me, turn_state, creatures, players, c)? {
        return Ok(());
    }
    if players_side(c) {
        for (player, context, setup) in setups {
            if !g::entities_creatures::creature::is_dead(
                g::entities_players::player::creature(player, c),
                c,
            ) {
                let task = g::combat::combat_manager::run_auto_pre_play_phase(
                    me, turn_state, context, setup, player, c,
                );
                m::hook_player_choice_context::assign_task_and_wait_for_pause_or_completion(
                    context, task, c,
                )?;
            }
        }
        if paused {
            // The game runs the paused setup's rest and its RunAutoPrePlayPhase as an action of its own (a
            // GenericHookGameAction), and the executor checks the win condition after it, as after every action.
            let manager = c.s.combat_manager__instance;
            if g::combat::combat_manager::is_in_progress(manager, c) {
                g::combat::combat_manager::check_win_condition__0(manager, c)?;
            }
        } else {
            players_turn_started(me, turn_state, c)?;
        }
    } else {
        g::combat::combat_turn_state::set_is_enemy_turn_started(turn_state, true, c);
        g::combat::combat_manager::wait_for_unpause__combat_turn_state(me, turn_state, c)?;
        g::combat::combat_manager::check_win_condition__combat_turn_state(me, turn_state, c)?;
        if g::combat::combat_turn_state::is_in_progress(turn_state, c) {
            g::combat::combat_manager::execute_enemy_turn(
                me,
                turn_state,
                action_during_enemy_turn,
                c,
            )?;
        }
    }
    Ok(())
}

/// C#: CombatManager.SetupPlayerTurn: the energy, the hand, then `AfterPlayerTurnStart`. Written by hand for its resume
/// point (world.rs): after the hand is drawn and before those hooks, where a prompt they raise (Toasty Mittens') is
/// answered from, so its answers do not draw the hand again. A combat resumed there (`Extra::enter_setup`) goes on with
/// the hooks.
pub fn setup_player_turn(
    _me: CombatManager,
    turn_state: CombatTurnState,
    player: Player,
    player_choice_context: HookPlayerChoiceContext,
    c: &mut Combat,
) -> R<()> {
    if !std::mem::take(&mut c.x.enter_setup) {
        if g::entities_creatures::creature::is_dead(g::entities_players::player::creature(player, c), c) {
            return Ok(());
        }
        if rt::is_null(&PlayerData::of(player, c).player_combat_state) {
            return Ok(());
        }
        hand_drawn(turn_state, player, player_choice_context, c)?;
        crate::world::setup_point(c);
    }
    let state = g::combat::combat_turn_state::state(turn_state, c);
    g::hooks::hook::after_player_turn_start(state, player_choice_context, player, c)?;
    Ok(())
}

/// C#: SetupPlayerTurn up to the hand drawn: the energy reset or added, the hand's size by the hooks, on the first turn
/// the cards that start at the bottom of the draw pile and the innate ones at its top, the draw.
fn hand_drawn(turn_state: CombatTurnState, player: Player, player_choice_context: HookPlayerChoiceContext, c: &mut Combat) -> R<()> {
    let state = g::combat::combat_turn_state::state(turn_state, c);
    if g::hooks::hook::should_player_reset_energy(state, player, c) {
        g::entities_players::player_combat_state::reset_energy(g::entities_players::player::player_combat_state(player, c), c);
    } else {
        g::entities_players::player_combat_state::add_max_energy_to_current(g::entities_players::player::player_combat_state(player, c), c);
    }
    g::hooks::hook::after_energy_reset(state, player, c)?;
    g::hooks::hook::before_hand_draw(state, player, player_choice_context, c)?;
    let (mut hand_draw, modifiers) = g::hooks::hook::modify_hand_draw(state, player, Dec::lit(5, 0), c);
    g::hooks::hook::after_modifying_hand_draw(state, modifiers, c)?;
    if g::entities_players::player_combat_state::turn_number(g::entities_players::player::player_combat_state(player, c), c) == 1 {
        let pile = g::entities_cards::pile_type_extensions::get_pile(PileType::Draw, player, c);
        let list: Vec<CardModel> = linq::where_(
            g::entities_cards::card_pile::cards(pile, c),
            |card: CardModel, c: &mut Combat| -> bool {
                let enchantment = g::models::card_model::enchantment(card, c);
                !rt::is_null(&enchantment) && g::dispatch::enchantment_model::should_start_at_bottom_of_draw_pile(enchantment, c)
            },
            c,
        );
        for &item in &list {
            g::entities_cards::card_pile::move_to_bottom_internal(pile, item, c);
        }
        let list2: Vec<CardModel> = linq::except(
            linq::where_(
                g::entities_cards::card_pile::cards(pile, c),
                |card: CardModel, c: &mut Combat| -> bool { g::models::card_model::keywords(card, c).contains(&CardKeyword::Innate) },
                c,
            ),
            list.clone(),
        );
        for &item in &list2 {
            g::entities_cards::card_pile::move_to_top_internal(pile, item, c);
        }
        hand_draw = rt::max(hand_draw, Dec::from_i32(list2.len() as i32));
        hand_draw = rt::min(hand_draw, Dec::from_i32(g::entities_cards::card_pile::max_cards_in_hand(c)));
    }
    g::commands::card_pile_cmd::draw__player_choice_context_dec_player_bool(player_choice_context, hand_draw, player, true, c)?;
    Ok(())
}

/// The rest of the turn start, where the running setup pauses for its first choice (`start_turn`), then the actions it
/// queued. The engine's combats have one player; a setup that pauses with more starting the turn is not ported (the game
/// goes on with the other players' setups first).
pub fn turn_start_rest(c: &mut Combat) -> R<()> {
    let running =
        c.x.turn_start
            .as_mut()
            .expect("a turn start's rest outside its setup");
    running.paused = true;
    let TurnStart {
        me,
        turn_state,
        creatures,
        players,
        ..
    } = running.clone();
    assert_eq!(
        players.len(),
        1,
        "a turn start's setup paused with more than one player starting the turn"
    );
    if !side_turn_started(me, turn_state, &creatures, &players, c)? {
        return Err(Stop::Dropped);
    }
    players_turn_started(me, turn_state, c)?;
    // The setup's rest is an action the game queues now (`RequestEnqueueHookAction`), behind what the rest of the turn
    // start queued: the executor, unpaused, runs those first.
    if g::combat::combat_turn_state::is_in_progress(turn_state, c) {
        let executor = g::runs::run_manager::action_executor(c.s.run_manager__instance, c);
        m::action_executor::execute_actions(executor, c)?;
    }
    if !g::combat::combat_turn_state::is_in_progress(turn_state, c) {
        return Err(Stop::Dropped);
    }
    Ok(())
}

/// Whether the turn start runs one player's setup that has not paused for a choice yet (world.rs, `setup_point`).
pub fn setup_running(c: &Combat) -> bool {
    c.x.turn_start
        .as_ref()
        .is_some_and(|t| !t.paused && t.players.len() == 1)
}

/// Whether a choice begun in `context` pauses the setup that is running at a turn start (`start_turn`): its first.
pub fn turn_start_pauses(context: HookPlayerChoiceContext, c: &Combat) -> bool {
    c.x.turn_start
        .as_ref()
        .is_some_and(|t| t.setup == context && !t.paused)
}

/// C#: CombatManager.StartTurn from `Hook.AfterSideTurnStart` to the players' ready check; false where the game's
/// turn start returns there (every player is ready to end the turn).
fn side_turn_started(
    me: CombatManager,
    turn_state: CombatTurnState,
    creatures: &[Creature],
    players: &[Player],
    c: &mut Combat,
) -> R<bool> {
    let state = g::combat::combat_turn_state::state(turn_state, c);
    let side = g::combat::combat_state::current_side(state, c);
    g::hooks::hook::after_side_turn_start(state, side, creatures.to_vec(), c)?;
    if g::combat::combat_state::current_side(state, c) != CombatSide::Player {
        return Ok(true);
    }
    for &player in players {
        if rt::is_null(&PlayerData::of(player, c).player_combat_state) {
            continue;
        }
        let Some(net_id) = c.s.local_context__net_id else {
            continue;
        };
        let context = g::game_actions_multiplayer::hook_player_choice_context::new__player_u64_game_action_type(
            player,
            net_id,
            GameActionType::CombatPlayPhaseOnly,
            c,
        );
        let player_combat_state = g::entities_players::player::player_combat_state(player, c);
        let orbs = g::entities_players::player_combat_state::orb_queue(player_combat_state, c);
        let task = g::entities_orbs::orb_queue::after_turn_start(orbs, context, c);
        m::hook_player_choice_context::assign_task_and_wait_for_pause_or_completion(
            context, task, c,
        )?;
    }
    // A player who is dead or does not start the turn is ready to end it at once.
    for player in g::combat::combat_state::players(state, c) {
        let dead = g::entities_creatures::creature::is_dead(
            g::entities_players::player::creature(player, c),
            c,
        );
        if dead || !players.contains(&player) {
            set_ready_to_end_turn(me, player, false, Fn0::default(), c);
            if g::combat::combat_manager::all_players_ready_to_end_turn__combat_turn_state(
                me, turn_state, c,
            ) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

/// C#: CombatManager.StartTurn's end on the players' side: the win check, then the play phase.
fn players_turn_started(me: CombatManager, turn_state: CombatTurnState, c: &mut Combat) -> R<()> {
    g::combat::combat_manager::check_win_condition__combat_turn_state(me, turn_state, c)?;
    if g::combat::combat_turn_state::is_in_progress(turn_state, c) {
        let run_manager = c.s.run_manager__instance;
        let executor = g::runs::run_manager::action_executor(run_manager, c);
        g::game_actions::action_executor::unpause(executor, c);
        let synchronizer = g::runs::run_manager::action_queue_synchronizer(run_manager, c);
        g::game_actions_multiplayer::action_queue_synchronizer::set_combat_state(
            synchronizer,
            ActionSynchronizerCombatState::PlayPhase,
            c,
        );
        g::combat::combat_turn_state::set_is_enemy_turn_started(turn_state, false, c);
    }
    Ok(())
}
