//! The part of the game's core that is written by hand, one module per C# class (engine/port.toml lists them; port
//! prints the signature each member must have in gen/manual.txt).

pub mod canon;
pub mod object;

/// What the hand-written core keeps beside the game's own objects.
#[derive(Clone, Debug, Default)]
pub struct Extra {}
