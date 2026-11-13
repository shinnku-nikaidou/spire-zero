//! The combat: every object of it, and the answers given to the prompts of the action that is running.

use super::{Obj, Str};
use crate::r#gen::state::Arenas;
use std::sync::OnceLock;

/// Why an action stopped before its end.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Stop {
    /// A prompt has no answer yet: the action is run again from its start once the answer is known.
    Choice,
}

/// What a C# Task is here: the value, or the reason the action stopped. `await x` is `x?`.
pub type R<T> = Result<T, Stop>;

/// A prompt in the middle of an effect, as the player sees it.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Prompt {
    /// Which command asks ("FromHand", "FromSimpleGrid", ..).
    pub kind: Str,
    /// What can be picked, in the order shown.
    pub options: Vec<Obj>,
    pub min: i32,
    pub max: i32,
}

/// The answers given so far to the prompts of the running action, and the prompt it stopped at.
#[derive(Clone, Debug, Default)]
pub struct Choices {
    /// One entry per prompt answered: the indexes picked.
    pub answers: Vec<Vec<i32>>,
    pub next: usize,
    pub pending: Option<Prompt>,
}

impl Choices {
    /// The answer to the prompt the action has just reached, or the stop that asks for it.
    pub fn take(&mut self, prompt: Prompt) -> R<Vec<i32>> {
        if let Some(answer) = self.answers.get(self.next) {
            self.next += 1;
            return Ok(answer.clone());
        }
        self.pending = Some(prompt);
        Err(Stop::Choice)
    }
}

/// A combat: plain data. Cloning it gives an independent combat.
#[derive(Clone, Debug, Default)]
pub struct Combat {
    /// Every object, by class.
    pub a: Arenas,
    pub choices: Choices,
    /// What the hand-written core keeps beside the game's own objects.
    pub x: crate::manual::Extra,
}

static CANON: OnceLock<Arenas> = OnceLock::new();

/// The canonical models: one object per model class, built once, shared by every combat, never written.
pub fn canon() -> &'static Arenas {
    CANON.get_or_init(crate::manual::canon::build)
}
