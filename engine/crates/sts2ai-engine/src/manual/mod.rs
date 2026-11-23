//! The part of the game's core that is written by hand, one module per C# class (engine/port.toml lists them; port
//! prints the signature each member must have in gen/manual.txt).

// The names are port's: the C# member, with the parameter types where the member is overloaded.
#![allow(non_snake_case)]

pub mod action_executor;
pub mod action_queue_set;
pub mod action_queue_synchronizer;
pub mod branching_player_choice_context;
pub mod canon;
pub mod card_select_cmd;
pub mod card_selector_stack;
pub mod combat_manager;
pub mod combat_state;
pub mod epoch_model;
pub mod game_action;
pub mod game_action_player_choice_context;
pub mod generic_hook_game_action;
pub mod hook_player_choice_context;
pub mod i_card_selector;
pub mod mod_helper;
pub mod model_db;
pub mod object;
pub mod rolling_boulder_power;
pub mod saved_properties;
pub mod whispering_earring;

/// What the hand-written core keeps beside the game's own objects.
#[derive(Clone, Debug, Default)]
pub struct Extra {
    /// The canonical models made so far, while they are being built (manual/canon.rs); None in a combat.
    pub canon_in_build: Option<canon::Table>,
    /// Where the turn loop stands (manual/combat_manager.rs).
    pub waits: combat_manager::Waits,
    /// The turn start whose player's setup is running (manual/combat_manager.rs, `start_turn`).
    pub turn_start: Option<combat_manager::TurnStart>,
    /// Set while a setup that paused for a choice runs its rest and its RunAutoPrePlayPhase, after the rest of the
    /// turn start (manual/combat_manager.rs, `turn_start_rest` to `turn_start_end`): the game's executor runs that as
    /// the setup's own action, so it is busy then (manual/action_executor.rs, `start_at_once`).
    pub setup_action: bool,
    /// The game's two signals to the turn loop, while nobody has taken them: every player is ready to end the turn
    /// (an EndTurnSignal), every player is ready for the enemies' turn (what to run during it; null for nothing).
    pub end_turn_signal: Option<crate::rt::Obj>,
    pub begin_enemy_turn_signal: Option<crate::rt::Fn0<crate::rt::R<()>>>,
    /// The CardSelectCmd entries that are running, one inside the other, and what the outermost was called with
    /// (manual/card_select_cmd.rs).
    pub entries: i32,
    pub entry: Option<card_select_cmd::Entry>,
    /// How many times Whispering Earring's own selector is on the selector stack now (manual/whispering_earring.rs).
    pub vakuu: i32,
    /// The card prompt about to be asked is a co-op teammate's (manual/card_select_cmd.rs): the selector gives it a
    /// default (manual/i_card_selector.rs).
    pub teammate_prompt: bool,
    /// The card prompt the running action stopped at (manual/i_card_selector.rs), for the world to take.
    pub asked: Option<crate::prompt::Asked>,
    /// How many card prompts have been asked in this world so far.
    pub prompts: i32,
    /// The draw pile's public record since its last shuffle, as the export carried it (drawlog.rs); shared by every
    /// world made of the import, and read only where a NoSL world is drawn from it (determinize.rs). A record the
    /// engine could not read is kept as what was wrong with it.
    pub draw_log: Option<std::sync::Arc<Result<crate::drawlog::DrawLog, String>>>,
    /// A NoSL world drawn at one of the running action's prompts: where it is drawn (determinize.rs, `take_answer`).
    pub draw_at: Option<std::sync::Arc<crate::determinize::DrawAt>>,
    /// The hooks that asked the player in the running step (manual/hook_player_choice_context.rs).
    pub hooks: hook_player_choice_context::Hooks,
    /// What the running step started from (world.rs), where a paused hook is found again; none between steps.
    pub step: Option<std::sync::Arc<hook_player_choice_context::StepStart>>,
    /// In a replay of the step: the paused hook it runs to and the combat it goes on in from there.
    pub resume: Option<Box<hook_player_choice_context::Resume>>,
    /// Whether the running step keeps resume points (world.rs): the step a world runs, not a replay inside it.
    pub points: bool,
    /// The running step's latest resume point (world.rs): the combat as it stood there, where a prompt after it is
    /// answered from.
    pub resume_point: Option<std::sync::Arc<crate::rt::Combat>>,
    /// Set on a resume point's combat only: the turn loop goes on inside its loop, where the point was taken
    /// (manual/combat_manager.rs, `turn_loop`).
    pub enter_loop: bool,
    /// Set on a resume point taken in a player's setup, after the hand is drawn (manual/combat_manager.rs,
    /// `setup_player_turn`): the turn start goes on in that setup, from there.
    pub enter_setup: bool,
}
