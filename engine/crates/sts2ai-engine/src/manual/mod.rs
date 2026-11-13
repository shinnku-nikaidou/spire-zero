//! The part of the game's core that is written by hand, one module per C# class (engine/port.toml lists them; port
//! prints the signature each member must have in gen/manual.txt).

// The names are port's: the C# member, with the parameter types where the member is overloaded.
#![allow(non_snake_case)]

pub mod action_executor;
pub mod action_queue_set;
pub mod action_queue_synchronizer;
pub mod branching_player_choice_context;
pub mod canon;
pub mod card_pile;
pub mod card_select_cmd;
pub mod card_selector_stack;
pub mod combat_manager;
pub mod combat_state;
pub mod delegates;
pub mod epoch_model;
pub mod game_action;
pub mod game_action_player_choice_context;
pub mod hook_player_choice_context;
pub mod i_card_selector;
pub mod mod_helper;
pub mod model_db;
pub mod object;
pub mod whispering_earring;

/// What the hand-written core keeps beside the game's own objects.
#[derive(Clone, Debug, Default)]
pub struct Extra {
    /// The canonical models made so far, while they are being built (manual/canon.rs); None in a combat.
    pub canon_in_build: Option<canon::Table>,
    /// Where the turn loop stands (manual/combat_manager.rs).
    pub waits: combat_manager::Waits,
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
    /// The card prompt the running action stopped at (manual/i_card_selector.rs), for the world to take.
    pub asked: Option<crate::prompt::Asked>,
    /// How many card prompts have been asked in this world so far.
    pub prompts: i32,
}
