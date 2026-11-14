//! Kept walks (port.toml [memo], DESIGN.md "Kept walks"). A walk the game makes at every hook builds a list of the
//! combat's objects and hands on each one that passes a test. Here the list, and each test's answer once asked, are
//! kept with the combat's write count: while the count stands nothing the walks read has changed, so the next walk
//! finds the same list and the same answers. A write between two items of a walk (the code an item was handed to
//! changed what they read) sends the rest of that walk to the test, asked as the game asks it.
//!
//! A combat's count goes up at every `of_mut` of a class whose fields the walks read, at any depth of call (port finds
//! them, and lists them in gen/report.txt): a field is written only through the `of_mut` of the class that declares it,
//! and a new object changes none that is there. A copy of the combat has the count and the kept lists it was copied
//! with, and the count of each only goes up, so a list kept at a count is the list of the one combat that stood there.

use crate::rt::{Combat, Obj};
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering::Relaxed};

/// One slot a walk of port.toml's [memo], in its order.
const SLOTS: usize = 2;

/// A walk's list as it was built at a write count, of the walk's object and argument, with the test's answers.
#[derive(Debug)]
pub struct Kept {
    at: u64,
    key: [Obj; 2],
    items: Vec<Obj>,
    /// For each item: 0 not asked yet, 1 does not pass, 2 passes. Shared by the copies of a combat that stand where it
    /// was kept, whose answers are the same.
    answers: Box<[AtomicU8]>,
}

impl Kept {
    pub fn len(&self) -> usize {
        self.items.len()
    }

    pub fn item(&self, at: usize) -> Obj {
        self.items[at]
    }
}

/// The kept walks of a combat.
#[derive(Clone, Debug, Default)]
pub struct Memo([Option<Arc<Kept>>; SLOTS]);

/// The combat's write count.
#[inline]
pub fn writes(c: &Combat) -> u64 {
    c.a.writes
}

/// The list of walk `slot` with this key, if it was built where the combat stands.
#[inline]
pub fn kept(c: &Combat, slot: usize, key: [Obj; 2]) -> Option<Arc<Kept>> {
    match &c.memo.0[slot] {
        Some(kept) if kept.at == c.a.writes && kept.key == key => Some(kept.clone()),
        _ => None,
    }
}

/// The list just built, begun at write count `at`. It is kept if building it wrote nothing (a getter that fills a
/// cache of the game's writes).
pub fn keep(c: &mut Combat, slot: usize, key: [Obj; 2], at: u64, items: Vec<Obj>) -> Arc<Kept> {
    let answers = (0..items.len()).map(|_| AtomicU8::new(0)).collect();
    let kept = Arc::new(Kept { at, key, items, answers });
    if at == c.a.writes {
        c.memo.0[slot] = Some(kept.clone());
    }
    kept
}

/// Whether item `at` of the list passes the walk's test: the kept answer while the combat stands where the list was
/// built, else the test asked now (and kept if asking it wrote nothing).
#[inline]
pub fn passes(c: &mut Combat, kept: &Kept, at: usize, test: impl FnOnce(&mut Combat) -> bool) -> bool {
    if c.a.writes != kept.at {
        return test(c);
    }
    match kept.answers[at].load(Relaxed) {
        1 => false,
        2 => true,
        _ => {
            let passed = test(c);
            if c.a.writes == kept.at {
                kept.answers[at].store(1 + passed as u8, Relaxed);
            }
            passed
        }
    }
}
