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

use crate::r#gen::kinds::Kind;
use crate::rt::{Combat, Obj};
use std::sync::atomic::{AtomicU8, Ordering::Relaxed};
use std::sync::{Arc, Mutex};

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
    /// For each set of kinds a walk was asked for (`Combat::only`, by its test's address), the positions of the items
    /// of those kinds. An object's kind never changes, so these hold as long as the list.
    picked: Mutex<Vec<(usize, Arc<[u32]>)>>,
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
    let kept = Arc::new(Kept { at, key, items, answers, picked: Mutex::new(Vec::new()) });
    if at == c.a.writes {
        c.memo.0[slot] = Some(kept.clone());
    }
    kept
}

/// The positions of the items a walk hands on when its caller's code does something only for some kinds
/// (`rt::wanted`); `None`: every item.
pub fn picked(kept: &Kept, only: Option<fn(Kind) -> bool>, c: &Combat) -> Option<Arc<[u32]>> {
    let test = only?;
    let id = test as usize;
    let mut known = kept.picked.lock().unwrap();
    if let Some((_, positions)) = known.iter().find(|(of, _)| *of == id) {
        return Some(positions.clone());
    }
    let positions: Arc<[u32]> = (0..kept.items.len())
        .filter(|&at| crate::rt::wanted(only, kept.items[at], c))
        .map(|at| at as u32)
        .collect();
    known.push((id, positions.clone()));
    Some(positions)
}

/// How many items a walk goes through: every item, or those picked.
#[inline]
pub fn count(kept: &Kept, picked: &Option<Arc<[u32]>>) -> usize {
    picked.as_ref().map_or(kept.items.len(), |p| p.len())
}

/// The position of the `n`th item a walk goes through.
#[inline]
pub fn nth(picked: &Option<Arc<[u32]>>, n: usize) -> usize {
    picked.as_ref().map_or(n, |p| p[n] as usize)
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
