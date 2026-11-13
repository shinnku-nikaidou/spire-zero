//! Handles and arenas. An object of the game is a slot in the arena of its class; a reference to it is an `Obj`: the
//! arena's tag and the slot's index. `Obj::NULL` is C#'s null.

use serde::{Deserialize, Serialize};
use std::ops::{Index, IndexMut};

/// A reference to an object of the game, or null. Copy, compared by identity, as a C# reference is.
#[derive(Copy, Clone, PartialEq, Eq, Hash, PartialOrd, Ord, Default, Serialize, Deserialize)]
#[serde(transparent)]
pub struct Obj(pub u32);

impl Obj {
    pub const NULL: Obj = Obj(0);
    const CANON: u32 = 1 << 31;
    const INDEX: u32 = (1 << 24) - 1;

    pub fn new(tag: u8, index: usize, canon: bool) -> Obj {
        assert!(tag > 0 && tag < 128 && index <= Obj::INDEX as usize, "arena {tag} is full");
        Obj(((tag as u32) << 24) | index as u32 | if canon { Obj::CANON } else { 0 })
    }
    /// Which arena the object is in; 0 for null.
    pub fn tag(self) -> u8 {
        ((self.0 >> 24) & 0x7f) as u8
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
        let name = crate::r#gen::state::ARENA_NAMES.get(self.tag() as usize).copied().unwrap_or("?");
        write!(f, "{}{}#{}", if self.is_canon() { "canon " } else { "" }, name, self.ix())
    }
}

/// The data of a class that owns an arena.
pub trait Stored: Sized + 'static {
    const TAG: u8;
    /// The arena of the canonical models of this class.
    fn canon() -> &'static Arena<Self>;
}

/// The objects of one class (and of the classes below it).
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(transparent)]
pub struct Arena<T> {
    pub items: Vec<T>,
    /// Set while the canonical models are being built: allocations are canonical, and canonical handles are local.
    #[serde(skip)]
    pub canon_build: bool,
}

impl<T> Default for Arena<T> {
    fn default() -> Self {
        Arena { items: Vec::new(), canon_build: false }
    }
}

impl<T: Stored> Arena<T> {
    pub fn alloc(&mut self, value: T) -> Obj {
        self.items.push(value);
        Obj::new(T::TAG, self.items.len() - 1, self.canon_build)
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
fn wrong_arena(o: Obj, tag: u8) -> ! {
    if o.is_null() {
        panic!("null reference (a {} was expected)", crate::r#gen::state::ARENA_NAMES[tag as usize]);
    }
    panic!("{:?} is not a {}", o, crate::r#gen::state::ARENA_NAMES[tag as usize]);
}

impl<T: Stored> Index<Obj> for Arena<T> {
    type Output = T;
    #[inline]
    fn index(&self, o: Obj) -> &T {
        if o.tag() != T::TAG {
            wrong_arena(o, T::TAG);
        }
        if o.is_canon() && !self.canon_build { &T::canon().items[o.ix()] } else { &self.items[o.ix()] }
    }
}

impl<T: Stored> IndexMut<Obj> for Arena<T> {
    #[inline]
    fn index_mut(&mut self, o: Obj) -> &mut T {
        if o.tag() != T::TAG {
            wrong_arena(o, T::TAG);
        }
        if o.is_canon() && !self.canon_build {
            panic!("write to the canonical {:?}", o);
        }
        &mut self.items[o.ix()]
    }
}
