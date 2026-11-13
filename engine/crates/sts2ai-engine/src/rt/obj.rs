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
#[derive(Clone, Debug)]
pub struct Arena<T> {
    pub items: Vec<T>,
    pub canon: Arc<Vec<T>>,
    /// Set while the canonical models are being built: what is made then is canonical.
    pub canon_build: bool,
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
        self.items.push(value);
        Obj::new(T::TAG, self.items.len() - 1, false)
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
            &mut self.items[o.ix()]
        }
    }
}
