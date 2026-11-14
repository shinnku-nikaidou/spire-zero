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

use crate::r#gen::state::{MEMO_FILTERS, MEMO_GETTERS, MEMO_WALKS};
use crate::rt::{Combat, Obj, Only};
use std::sync::atomic::{AtomicU8, AtomicU64, Ordering::Relaxed};
use std::sync::{Arc, OnceLock};

/// A walk's list as it was built at a write count, of the walk's object and argument, with the test's answers.
#[derive(Debug)]
pub struct Kept {
    at: u64,
    key: [Obj; 2],
    items: Vec<Obj>,
    /// For each item: 0 not asked yet, 1 does not pass, 2 passes. Shared by the copies of a combat that stand where it
    /// was kept, whose answers are the same.
    answers: Box<[AtomicU8]>,
    /// For each set of kinds a walk was asked for (`Combat::only`, by its number), the positions of the items of
    /// those kinds. An object's kind never changes, so these hold as long as the list.
    picked: Box<[OnceLock<Box<[u32]>>]>,
}

impl Kept {
    pub fn len(&self) -> usize {
        self.items.len()
    }

    pub fn item(&self, at: usize) -> Obj {
        self.items[at]
    }
}

/// A kept getter's values at a write count, by the index of the object they are of: 0 not asked yet, else the
/// handle's bits plus one. Shared by the copies of a combat that stand where they were kept, whose values are the same.
#[derive(Debug)]
pub struct Values {
    at: u64,
    of: Box<[AtomicU64]>,
}

/// The kept walks and getters of a combat, one slot each in port.toml's order.
#[derive(Clone, Debug, Default)]
pub struct Memo {
    walks: [Option<Arc<Kept>>; MEMO_WALKS],
    values: [Option<Arc<Values>>; MEMO_GETTERS],
}

/// The combat's write count.
#[inline]
pub fn writes(c: &Combat) -> u64 {
    c.a.writes
}

/// The list of walk `slot` with this key, if it was built where the combat stands.
#[inline]
pub fn kept(c: &Combat, slot: usize, key: [Obj; 2]) -> Option<Arc<Kept>> {
    match &c.memo.walks[slot] {
        Some(kept) if kept.at == c.a.writes && kept.key == key => Some(kept.clone()),
        _ => None,
    }
}

/// The list just built, begun at write count `at`. It is kept if building it wrote nothing (a getter that fills a
/// cache of the game's writes).
pub fn keep(c: &mut Combat, slot: usize, key: [Obj; 2], at: u64, items: Vec<Obj>) -> Arc<Kept> {
    let answers = (0..items.len()).map(|_| AtomicU8::new(0)).collect();
    let picked = (0..MEMO_FILTERS).map(|_| OnceLock::new()).collect();
    let kept = Arc::new(Kept { at, key, items, answers, picked });
    if at == c.a.writes {
        c.memo.walks[slot] = Some(kept.clone());
    }
    kept
}

/// The value of getter `slot` for this object, if it was asked where the combat stands.
#[inline]
pub fn value(c: &Combat, slot: usize, me: Obj) -> Option<Obj> {
    let values = c.memo.values[slot].as_ref()?;
    if values.at != c.a.writes || me.is_canon() {
        return None;
    }
    match values.of.get(me.ix())?.load(Relaxed) {
        0 => None,
        bits => Some(Obj((bits - 1) as u32)),
    }
}

/// The value just found for this object, begun at write count `at` (`len`: how many objects its arena holds). It is
/// kept if finding it wrote nothing.
pub fn keep_value(c: &mut Combat, slot: usize, me: Obj, at: u64, value: Obj, len: usize) {
    if at != c.a.writes || me.is_canon() {
        return;
    }
    if !c.memo.values[slot].as_ref().is_some_and(|v| v.at == at) {
        let of = (0..len).map(|_| AtomicU64::new(0)).collect();
        c.memo.values[slot] = Some(Arc::new(Values { at, of }));
    }
    if let Some(cell) = c.memo.values[slot].as_ref().and_then(|v| v.of.get(me.ix())) {
        cell.store(value.0 as u64 + 1, Relaxed);
    }
}

/// The positions of the items a walk hands on when its caller's code does something only for some kinds
/// (`rt::wanted`); `None`: every item.
#[inline]
pub fn picked<'k>(kept: &'k Kept, only: Option<&'static Only>, c: &Combat) -> Option<&'k [u32]> {
    let of = only?;
    Some(kept.picked[of.id].get_or_init(|| {
        (0..kept.items.len())
            .filter(|&at| crate::rt::wanted(only, kept.items[at], c))
            .map(|at| at as u32)
            .collect()
    }))
}

/// How many items a walk goes through: every item, or those picked.
#[inline]
pub fn count(kept: &Kept, picked: Option<&[u32]>) -> usize {
    picked.map_or(kept.items.len(), |p| p.len())
}

/// The position of the `n`th item a walk goes through.
#[inline]
pub fn nth(picked: Option<&[u32]>, n: usize) -> usize {
    picked.map_or(n, |p| p[n] as usize)
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
