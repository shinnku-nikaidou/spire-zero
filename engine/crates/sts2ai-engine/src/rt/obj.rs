//! Handles and arenas. An object of the game is a slot in the arena of its class; a reference to it is an `Obj`: the
//! arena's tag and the slot's index. `Obj::NULL` is C#'s null.

use std::ops::{Index, IndexMut};
use std::sync::Arc;

/// A reference to an object of the game, or null. Copy, compared by identity, as a C# reference is.
#[derive(Copy, Clone, PartialEq, Eq, Hash, PartialOrd, Ord, Default)]
pub struct Obj(pub u32);

impl Obj {
    pub const NULL: Obj = Obj(0);
    const CANON: u32 = 1 << 31;
    /// 21 bits of index, 10 bits of tag, the canonical bit.
    const INDEX_BITS: u32 = 21;
    const INDEX: u32 = (1 << Obj::INDEX_BITS) - 1;
    const TAGS: u16 = 1 << 10;

    pub fn new(tag: u16, index: usize, canon: bool) -> Obj {
        assert!(tag > 0 && tag < Obj::TAGS, "no arena {tag}");
        assert!(index <= Obj::INDEX as usize, "arena {tag} is full");
        Obj(((tag as u32) << Obj::INDEX_BITS) | index as u32 | if canon { Obj::CANON } else { 0 })
    }
    /// Which arena the object is in; 0 for null.
    pub fn tag(self) -> u16 {
        ((self.0 >> Obj::INDEX_BITS) & (Obj::TAGS as u32 - 1)) as u16
    }
    pub fn ix(self) -> usize {
        (self.0 & Obj::INDEX) as usize
    }
    /// Whether this is a canonical model: one per class, shared by every combat, never written.
    pub fn is_canon(self) -> bool {
        self.0 & Obj::CANON != 0
    }
    pub fn is_null(self) -> bool {
        self.0 == 0
    }
}

impl std::fmt::Debug for Obj {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if self.is_null() {
            return write!(f, "null");
        }
        let name = crate::r#gen::state::ARENA_NAMES
            .get(self.tag() as usize)
            .copied()
            .unwrap_or("?");
        write!(
            f,
            "{}{}#{}",
            if self.is_canon() { "canon " } else { "" },
            name,
            self.ix()
        )
    }
}

/// The data of a class that owns an arena.
pub trait Stored: Clone + 'static {
    const TAG: u16;
}

/// The objects of one class (and of the classes below it): those of this combat, and the canonical models of the class
/// (the game's ModelDb: one per model class). Every combat starts with the same canonical models and shares them; a
/// combat that writes to one (the game fills a canonical model's caches when they are first read) gets its own copy
/// of them at that moment.
///
/// A copy of a combat shares its objects with the combat it was copied from: copying an arena copies the handles, and
/// an object is copied when one of the two first writes to it. What a combat never writes (the run's history, the
/// combat's own history entries, most cards) is held once by every copy made of it.
#[derive(Clone, Debug)]
pub struct Arena<T> {
    items: Vec<Arc<T>>,
    pub canon: Arc<Vec<T>>,
    /// Set while the canonical models are being built: what is made then is canonical.
    pub canon_build: bool,
}

/// The object behind a handle for writing: the handle's own, copied first when another copy of the combat holds it too.
#[inline]
fn owned<T: Clone>(slot: &mut Arc<T>) -> &mut T {
    if Arc::strong_count(slot) != 1 {
        *slot = Arc::new(T::clone(slot));
    }
    // SAFETY: the count is 1 and no Weak is ever made of an arena's object, so this handle is the only one; it is
    // borrowed mutably, so nothing can copy it while the reference lives. A count read as more than 1 after another
    // thread let go only costs a copy.
    unsafe { &mut *(Arc::as_ptr(slot) as *mut T) }
}

/// An arena seen without its type, for measurements (tests/speed.rs): how much it holds, what a copy of it costs,
/// what is in it.
pub trait AnyArena {
    /// The objects of the combat's own (the canonical ones are shared, not copied).
    fn len(&self) -> usize;
    /// The size of one object, without what it points to.
    fn item_size(&self) -> usize;
    /// Makes a copy and drops it.
    fn copy(&self);
    fn text(&self) -> String;
    /// How many of the objects another copy of the combat holds too.
    fn shared(&self) -> usize;
}

impl<T: Clone + std::fmt::Debug> AnyArena for Arena<T> {
    fn len(&self) -> usize {
        self.items.len()
    }
    fn item_size(&self) -> usize {
        std::mem::size_of::<T>()
    }
    fn copy(&self) {
        std::hint::black_box(self.clone());
    }
    fn text(&self) -> String {
        format!("{:?}", self.items)
    }
    fn shared(&self) -> usize {
        self.items
            .iter()
            .filter(|item| Arc::strong_count(item) != 1)
            .count()
    }
}

impl<T> Default for Arena<T> {
    fn default() -> Self {
        Arena {
            items: Vec::new(),
            canon: Arc::new(Vec::new()),
            canon_build: false,
        }
    }
}

impl<T: Stored> Arena<T> {
    pub fn alloc(&mut self, value: T) -> Obj {
        if self.canon_build {
            let canon = Arc::make_mut(&mut self.canon);
            canon.push(value);
            return Obj::new(T::TAG, canon.len() - 1, true);
        }
        self.items.push(Arc::new(value));
        Obj::new(T::TAG, self.items.len() - 1, false)
    }
    /// The combat's own objects, in the order they were made.
    pub fn iter(&self) -> impl Iterator<Item = &T> {
        self.items.iter().map(|item| &**item)
    }
    pub fn len(&self) -> usize {
        self.items.len()
    }
    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }
}

#[cold]
#[inline(never)]
fn wrong_arena(o: Obj, tag: u16) -> ! {
    if o.is_null() {
        panic!(
            "null reference (a {} was expected)",
            crate::r#gen::state::ARENA_NAMES[tag as usize]
        );
    }
    panic!(
        "{:?} is not a {}",
        o,
        crate::r#gen::state::ARENA_NAMES[tag as usize]
    );
}

impl<T: Stored> Index<Obj> for Arena<T> {
    type Output = T;
    #[inline]
    fn index(&self, o: Obj) -> &T {
        if o.tag() != T::TAG {
            wrong_arena(o, T::TAG);
        }
        if o.is_canon() {
            &self.canon[o.ix()]
        } else {
            &self.items[o.ix()]
        }
    }
}

impl<T: Stored> IndexMut<Obj> for Arena<T> {
    #[inline]
    fn index_mut(&mut self, o: Obj) -> &mut T {
        if o.tag() != T::TAG {
            wrong_arena(o, T::TAG);
        }
        if o.is_canon() {
            &mut Arc::make_mut(&mut self.canon)[o.ix()]
        } else {
            owned(&mut self.items[o.ix()])
        }
    }
}
