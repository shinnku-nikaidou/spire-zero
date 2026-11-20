//! Turns searched in threads (C#: EnginePlanner's Searcher): each job's state is the job's path played from the world it
//! starts at (one kept from an earlier turn's search, `Worlds`, else the root), searched in a thread of its own; a
//! thread that is done takes the next job, and the results come back in job order, so a plan is the same on any number
//! of threads. Threads fewer jobs leave over (the greedy dive's one job) step states ahead of the jobs' searches
//! (helpers.rs), which changes nothing the searches find.

use std::collections::{HashMap, VecDeque};
use std::panic::{self, AssertUnwindSafe};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicUsize, Ordering};

use super::beam::{Job, Searcher};
use super::ffi;
use super::helpers::Helpers;
use super::leaf::{Net, Price};
use super::state::{Action, State};
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
    /// One job's turn: the job's path played from where it starts (`Job::from`, else the root), searched; with the world
    /// it was searched from.
    /// `helpers`: the call's spare threads, if it has any.
    fn job(&self, root: &State, job: &Job, nodes: i32, keep: usize, helpers: Option<&Helpers>) -> Result<TurnSearch, String> {
        let leaf = self.net.leaf(self.base, self.price);
        turn::job(job, || start(root, job), nodes, keep, &job.bounds, &leaf, helpers)
    }
}

/// The state a job's turn is searched from: its path played from the world it starts at, else from the root.
pub fn start(root: &State, job: &Job) -> Result<State, String> {
    let (mut state, done) = match &job.from {
        Some((done, world)) => (world.copy(), *done),
        None => (root.copy(), 0),
    };
    for action in &job.path[done..] {
        state = state.step(action)?;
    }
    Ok(state)
}

/// Worlds of states searched before, by the path from the root that reaches each (Job's `from`), the latest `most`:
/// a job starts from the one at the longest beginning of its path instead of replaying the path from the root. The
/// engine's steps depend on nothing else, so the world is the one the replay would make. Kept by the hash of the path,
/// with the path to tell a match.
pub struct Worlds {
    by_hash: HashMap<u64, (Vec<Action>, State)>,
    order: VecDeque<u64>,
    most: usize,
}

/// The hash of each beginning of `path`, the first action's first.
fn beginnings(path: &[Action]) -> Vec<u64> {
    use std::hash::{Hash, Hasher};
    let mut at = 0u64;
    path.iter()
        .map(|action| {
            let mut one = std::collections::hash_map::DefaultHasher::new();
            action.hash(&mut one);
            at = super::turn::mix(at, one.finish());
            at
        })
        .collect()
}

impl Worlds {
    pub fn new(most: usize) -> Worlds {
        Worlds {
            by_hash: HashMap::new(),
            order: VecDeque::new(),
            most,
        }
    }

    /// The world at the longest beginning of `path` kept, with how many of its actions lead there.
    pub fn nearest(&self, path: &[Action]) -> Option<(usize, State)> {
        let hashes = beginnings(path);
        (1..=path.len()).rev().find_map(|k| match self.by_hash.get(&hashes[k - 1]) {
            Some((kept, world)) if kept[..] == path[..k] => Some((k, world.copy())),
            _ => None,
        })
    }

    /// Keeps the world `path` reaches; the oldest goes when `most` are kept. The root's own (no path) is the root.
    pub fn keep(&mut self, path: Vec<Action>, world: State) {
        let Some(&hash) = beginnings(&path).last() else {
            return;
        };
        if self.most == 0 || self.by_hash.contains_key(&hash) {
            return;
        }
        if self.order.len() == self.most
            && let Some(oldest) = self.order.pop_front()
        {
            self.by_hash.remove(&oldest);
        }
        self.order.push_back(hash);
        self.by_hash.insert(hash, (path, world));
    }

    /// The root moved by `played`: the worlds below it stay, by their paths from there; the rest go.
    pub fn advance(&mut self, played: &[Action]) {
        let order = std::mem::take(&mut self.order);
        let mut by_hash = std::mem::take(&mut self.by_hash);
        for hash in order {
            let (path, world) = by_hash.remove(&hash).expect("a world for each hash kept");
            if path.len() > played.len() && path.starts_with(played) {
                self.keep(path[played.len()..].to_vec(), world);
            }
        }
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
        // The threads fewer jobs leave over step states ahead of the jobs' searches (helpers.rs).
        let spare = self.threads.saturating_sub(count);
        let helpers = Helpers::new();
        let helping = (spare > 0).then_some(&helpers);
        std::thread::scope(|scope| {
            for _ in 0..spare {
                let helpers = &helpers;
                scope.spawn(move || {
                    let stop = stop;
                    ffi::with_stop(stop.0, || helpers.work())
                });
            }
            let mut searching = Vec::with_capacity(count);
            for root in roots {
                let (results, next, failed) = (&results, &next, &failed);
                searching.push(scope.spawn(move || {
                    let stop = stop;
                    ffi::with_stop(stop.0, || {
                        loop {
                            let i = next.fetch_add(1, Ordering::Relaxed);
                            if i >= jobs.len() || failed.load(Ordering::Relaxed) {
                                break;
                            }
                            let found = panic::catch_unwind(AssertUnwindSafe(|| self.job(&root, &jobs[i], nodes, keep, helping)))
                                .unwrap_or_else(|payload| Err(ffi::panic_text(payload)));
                            if found.is_err() {
                                failed.store(true, Ordering::Relaxed);
                            }
                            *results[i].lock().expect("a result") = Some(found);
                        }
                    })
                }));
            }
            for thread in searching {
                // A panic is caught inside; the thread's end is all that is waited for.
                let _ = thread.join();
            }
            helpers.finish();
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

    fn root_waits(&self) -> bool {
        self.root.obs.prompt.is_some()
    }
}
