//! Threads a search call does not need for its jobs (fewer jobs than threads: the greedy dive's one at a time, the
//! beam's last turns) step states ahead of a turn's depth-first search: at each state it reaches, the steps of its
//! actions after the first go to a queue while the search goes down the first, and when the search comes back to an
//! action its step is there already (or it makes it itself, if no helper has taken it yet). A helper that steps a state
//! where the player can play on queues that state's own actions too, `AHEAD` levels down.
//!
//! The search stays one thread: it visits the same states in the same order, asks the leaf about the same ends in the
//! same batches, and takes each state as the step it would have made itself (a step depends on nothing but the world
//! it starts from). A step queued for an action the search never comes to (its budget ran out first, or the state was
//! met before) is dropped unasked: an error or a stop in it goes nowhere, as the step was never the search's. So a plan
//! is the same with or without helpers, on any number of threads. Measured on the 45 roots of a dump at medium, 8
//! threads (2026-11-19): the dive alone, wall 1.57 -> 1.18 s; the whole plan, wall -10 % for 10 % more CPU (the steps
//! dropped, and a copy of the world for each step queued). Looking further ahead (`AHEAD` 2) took the dive to -27 % for
//! four times the CPU spent on it.

use std::panic::{self, AssertUnwindSafe};
use std::sync::{Arc, Condvar, Mutex};

use super::ffi;
use super::order;
use super::state::{Action, State, Status};

/// How many levels below a state a helper stepped it queues the actions of the states it makes, where the player can
/// play: 0, only the steps the search itself queues.
pub const AHEAD: u32 = 0;

/// A state's actions and the slots of their steps, queued before the search reached it.
pub struct Ahead {
    pub actions: Vec<Action>,
    pub slots: Vec<Arc<Slot>>,
}

enum Job {
    Waiting { from: State, action: Action, below: u32, round: i32 },
    Taken,
    Done(Result<(State, Option<Ahead>), String>),
    Dropped,
}

/// One step queued: the state it starts from and the action, then the state it made.
pub struct Slot {
    job: Mutex<Job>,
    done: Condvar,
}

/// The queue of a search call's steps ahead, which its spare threads work from.
pub struct Helpers {
    queue: Mutex<(Vec<Arc<Slot>>, bool)>,
    ready: Condvar,
}

impl Default for Helpers {
    fn default() -> Self {
        Self::new()
    }
}

impl Helpers {
    pub fn new() -> Helpers {
        Helpers { queue: Mutex::new((Vec::new(), false)), ready: Condvar::new() }
    }

    /// A helper's life: takes the latest step queued (the deepest state's, which the search needs first) until told
    /// the call is over.
    pub fn work(&self) {
        loop {
            let slot = {
                let mut queue = self.queue.lock().expect("the queue");
                loop {
                    if let Some(slot) = queue.0.pop() {
                        break slot;
                    }
                    if queue.1 {
                        return;
                    }
                    queue = self.ready.wait(queue).expect("the queue");
                }
            };
            let taken = {
                let mut job = slot.job.lock().expect("a slot");
                match std::mem::replace(&mut *job, Job::Taken) {
                    Job::Waiting { from, action, below, round } => Some((from, action, below, round)),
                    other => {
                        *job = other;
                        None
                    }
                }
            };
            if let Some((from, action, below, round)) = taken {
                let made = panic::catch_unwind(AssertUnwindSafe(|| self.step(from, &action, below, round)))
                    .unwrap_or_else(|payload| Err(ffi::panic_text(payload)));
                let mut job = slot.job.lock().expect("a slot");
                if matches!(*job, Job::Taken) {
                    *job = Job::Done(made);
                    slot.done.notify_all();
                } else if let Ok((_, Some(ahead))) = made {
                    // Dropped while it was being made.
                    drop(job);
                    ahead.drop_steps();
                }
            }
        }
    }

    /// The call is over: the helpers go once the queue is empty.
    pub fn finish(&self) {
        self.queue.lock().expect("the queue").1 = true;
        self.ready.notify_all();
    }

    /// Queues the steps of `from` by each of `actions`, `below` levels to look further ahead; the first of them is
    /// taken first.
    pub fn queue(&self, from: &State, actions: &[Action], below: u32) -> Vec<Arc<Slot>> {
        let round = from.round();
        let slots: Vec<Arc<Slot>> = actions
            .iter()
            .map(|action| {
                Arc::new(Slot {
                    job: Mutex::new(Job::Waiting { from: from.copy(), action: action.clone(), below, round }),
                    done: Condvar::new(),
                })
            })
            .collect();
        self.queue.lock().expect("the queue").0.extend(slots.iter().rev().cloned());
        self.ready.notify_all();
        slots
    }

    /// The step, and where it leaves the player to play on in the same turn, its actions queued `below` levels on.
    fn step(&self, mut from: State, action: &Action, below: u32, round: i32) -> Result<(State, Option<Ahead>), String> {
        let mut after = from.step(action)?;
        drop(from);
        let goes_on = below > 0
            && after.status == Status::InProgress
            && after.obs.prompt.is_none()
            && !action.is_end_turn()
            && after.round() == round;
        let ahead = if goes_on {
            let (actions, _) = order::actions(&mut after, i64::MAX);
            let slots = self.queue(&after, &actions, below - 1);
            Some(Ahead { actions, slots })
        } else {
            None
        };
        Ok((after, ahead))
    }
}

impl Slot {
    /// The step's state: the helper's, waited for if one is on it, else made here.
    pub fn take(&self, helpers: &Helpers) -> Result<(State, Option<Ahead>), String> {
        let mut job = self.job.lock().expect("a slot");
        loop {
            match std::mem::replace(&mut *job, Job::Taken) {
                Job::Waiting { from, action, below, round } => {
                    drop(job);
                    return helpers.step(from, &action, below, round);
                }
                Job::Done(made) => return made,
                Job::Taken => job = self.done.wait(job).expect("a slot"),
                Job::Dropped => unreachable!("a step taken after it was dropped"),
            }
        }
    }

    /// The search will not come to this step: a helper not yet on it leaves it, and what it made goes, with the steps
    /// it queued below.
    pub fn drop_step(&self) {
        let before = std::mem::replace(&mut *self.job.lock().expect("a slot"), Job::Dropped);
        if let Job::Done(Ok((_, Some(ahead)))) = before {
            ahead.drop_steps();
        }
    }
}

impl Ahead {
    pub fn drop_steps(&self) {
        for slot in &self.slots {
            slot.drop_step();
        }
    }
}
