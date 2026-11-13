//! System.Object's virtual members for the game's objects.

use crate::r#gen as g;
use crate::r#gen::kinds::{Kind, is_subclass, kind_of};
use crate::rt::{Combat, Obj, Str};

/// C#: IComparable.CompareTo between two objects of the game. Of the game's classes only the models compare
/// (AbstractModel: IComparable<AbstractModel>).
pub fn compare_to(a: Obj, b: Obj, c: &mut Combat) -> i32 {
    if is_subclass(kind_of(a, c), Kind::AbstractModel) {
        return g::dispatch::abstract_model::compare_to(a, b, c);
    }
    panic!("Object.CompareTo({a:?}, {b:?}): the class does not compare")
}

/// C#: Object.ToString.
pub fn to_string(o: Obj, _c: &mut Combat) -> Str {
    todo!("Object.ToString({o:?})")
}
