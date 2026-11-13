//! System.Object's virtual members for the game's objects.

use crate::rt::{Combat, Obj, Str};

/// C#: IComparable.CompareTo between two objects of the game.
pub fn compare_to(a: Obj, b: Obj, _c: &mut Combat) -> i32 {
    todo!("Object.CompareTo({a:?}, {b:?})")
}

/// C#: Object.ToString.
pub fn to_string(o: Obj, _c: &mut Combat) -> Str {
    todo!("Object.ToString({o:?})")
}
