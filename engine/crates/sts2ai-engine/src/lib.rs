//! Slay the Spire 2's combat, for the SL planner's search. See engine/DESIGN.md.
//!
//! `gen` is the game's own code, translated by `port` (not in git; built by engine/gen.sh). `rt` is what the translated
//! code stands on. `manual` is the part of the game's core that is written by hand, one module per C# class.

// The game's arithmetic wraps on overflow, as C# does by default; the profiles in Cargo.toml turn the checks off.
#![allow(clippy::too_many_arguments)]

#[path = "gen/mod.rs"]
pub mod r#gen;
pub mod manual;
pub mod rt;
