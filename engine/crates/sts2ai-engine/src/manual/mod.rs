//! The part of the game's core that is written by hand, one module per C# class (engine/port.toml lists them; port
//! prints the signature each member must have in gen/manual.txt).

// The names are port's: the C# member, with the parameter types where the member is overloaded.
#![allow(non_snake_case)]

pub mod canon;
pub mod delegates;
pub mod model_db;
pub mod object;

/// What the hand-written core keeps beside the game's own objects.
#[derive(Clone, Debug, Default)]
pub struct Extra {
    /// The canonical models made so far, while they are being built (manual/canon.rs); None in a combat.
    pub canon_in_build: Option<canon::Table>,
}
