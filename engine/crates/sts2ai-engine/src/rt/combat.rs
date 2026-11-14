//! The combat: every object of it, and the answers given to the prompts of the action that is running.

use crate::r#gen::state::{Arenas, Roots};

/// Why an action stopped before its end.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Stop {
    /// A prompt has no answer yet: the action is run again from its start once the answer is known.
    Choice,
}

/// What a C# Task is here: the value, or the reason the action stopped. `await x` is `x?`.
pub type R<T> = Result<T, Stop>;

/// The answers given to the prompts of the running action: for each prompt it has met, the picked options as
/// indexes into the list the game offered.
#[derive(Clone, Debug, Default)]
pub struct Choices {
    pub answers: Vec<Vec<i32>>,
    /// How many of them the action has taken so far.
    pub next: usize,
    /// Set when a task nobody awaits stopped at a prompt, in a member that cannot pass the stop on.
    pub stopped: bool,
}

impl Choices {
    /// The answer to the prompt the action has just reached; None when it has not been given yet.
    pub fn take(&mut self) -> Option<Vec<i32>> {
        let answer = self.answers.get(self.next)?.clone();
        self.next += 1;
        Some(answer)
    }
}

/// A combat: plain data. Cloning it gives an independent combat. `Combat::default()` has nothing in it, not even the
/// canonical models; `manual::canon::combat()` is the one to start from.
#[derive(Clone, Debug, Default)]
pub struct Combat {
    /// Every object, by class.
    pub a: Arenas,
    /// The roots: the statics of the game that are state.
    pub s: Roots,
    pub choices: Choices,
    /// What the hand-written core keeps beside the game's own objects.
    pub x: crate::manual::Extra,
    /// The walks kept while nothing is written (rt/memo.rs).
    pub memo: crate::rt::memo::Memo,
}
