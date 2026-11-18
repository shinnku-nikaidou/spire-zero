//! Turns searched in threads (C#: EnginePlanner's Searcher): each job's state is the root with the job's path played,
//! searched in a thread of its own; a thread that is done takes the next job, and the results come back in job order,
//! so a plan is the same on any number of threads.

use std::panic::{self, AssertUnwindSafe};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicUsize, Ordering};

use super::beam::{Job, Searcher};
use super::ffi;
use super::leaf::{Net, Price};
use super::state::State;
use super::turn::{self, TurnSearch};

/// The engine's turn searches from one root.
pub struct Threads<'a> {
    pub root: &'a State,
    pub net: &'a Net,
    /// The run at the root's import, hashed (`Net::leaf`).
    pub base: u64,
    pub price: &'a Price<'a>,
    pub threads: usize,
    /// Where the search is told to stop.
    pub stop: Stop,
}

/// Where a search is told to stop (null: never): a pointer that crosses into the search's threads, which only read
/// through it while the call lives.
#[derive(Copy, Clone)]
pub struct Stop(pub *const AtomicI32);

unsafe impl Send for Stop {}
unsafe impl Sync for Stop {}

impl Threads<'_> {
    /// One job's turn: the root with the path played, searched.
    fn job(&self, root: &State, job: &Job, nodes: i32, keep: usize) -> Result<TurnSearch, String> {
        let mut state = root.copy();
        for action in &job.path {
            state = state.step(action)?;
        }
        let leaf = self.net.leaf(self.base, self.price);
        turn::search(&mut state, &job.history, nodes, keep, &job.bounds, &leaf)
    }
}

impl Searcher for Threads<'_> {
    fn search(&self, jobs: &[Job], nodes: i32, keep: usize) -> Result<Vec<TurnSearch>, String> {
        let count = self.threads.clamp(1, jobs.len().max(1));
        let results: Vec<Mutex<Option<Result<TurnSearch, String>>>> = jobs.iter().map(|_| Mutex::new(None)).collect();
        let next = AtomicUsize::new(0);
        let failed = AtomicBool::new(false);
        let stop = self.stop;
        // Each thread its own copy of the root: a world is one thread's at a time.
        let roots: Vec<State> = (0..count).map(|_| self.root.copy()).collect();
        std::thread::scope(|scope| {
            for root in roots {
                let (results, next, failed) = (&results, &next, &failed);
                scope.spawn(move || {
                    let stop = stop;
                    ffi::with_stop(stop.0, || {
                        loop {
                            let i = next.fetch_add(1, Ordering::Relaxed);
                            if i >= jobs.len() || failed.load(Ordering::Relaxed) {
                                break;
                            }
                            let found = panic::catch_unwind(AssertUnwindSafe(|| self.job(&root, &jobs[i], nodes, keep)))
                                .unwrap_or_else(|payload| Err(ffi::panic_text(payload)));
                            if found.is_err() {
                                failed.store(true, Ordering::Relaxed);
                            }
                            *results[i].lock().expect("a result") = Some(found);
                        }
                    })
                });
            }
        });
        let mut found = Vec::with_capacity(jobs.len());
        for result in results {
            match result.into_inner().expect("a result") {
                Some(Ok(search)) => found.push(search),
                Some(Err(error)) => return Err(error),
                // A job not taken because another failed first.
                None => continue,
            }
        }
        if found.len() < jobs.len() {
            return Err("a turn's search failed".into());
        }
        Ok(found)
    }
}
