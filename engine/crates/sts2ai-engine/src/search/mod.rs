//! The SL planner's search, in the engine (plans/2026-11-18-2132-sl-search-rust.md). Each part is the C# brain's
//! (`src/Sts2Ai.Brain/Combat`) moved here line for line, so that the same search gives the same plans: the same nodes,
//! the same ends in the same order, the same values to the last bit.
//!
//! - `state`: a state of the search, a world where the player is asked, with what the search reads of it; the actions;
//!   the record of enemy moves (C#: MoveRecord).
//! - `order`: the actions tried at a state, in their order (C#: PimcBrain.Ordered, PromptAnswers).
//! - `bound`: the one rule written by hand, the pruning by HP (C#: LifeBound).
//! - `loops`: cycles inside a turn (C#: LoopSearch).
//! - `turn`: one turn searched depth first to its ends (C#: SlPlanner's TurnDfs).
//! - `beam`: the beam over turns (C#: SlPlanner.Search); `solver`: the solver (C#: SlSolver); `threads`: their turns
//!   searched in threads.
//! - `leaf`: what the ends are worth, by the combat net and V (C#: CombatNet, FreeDisposal); `tokens`: the net's
//!   reading of a state (C#: CombatTokens and CombatNet's inputs).
//! - `read`: a record of enemy moves and a turn's search asked for, from their JSON as the C# side writes them; the
//!   turn's search found, in the JSON C# reads.
//! - `ffi`: the C ABI the C# side calls.

pub mod beam;
pub mod bound;
pub mod ffi;
pub mod leaf;
pub mod loops;
pub mod order;
pub mod read;
pub mod solver;
pub mod state;
pub mod threads;
pub mod tokens;
pub mod turn;

/// The search's tests, on toy worlds (C#: SlPlannerTests, LoopSearchTests).
#[cfg(test)]
mod tests;
#[cfg(test)]
mod toy;
