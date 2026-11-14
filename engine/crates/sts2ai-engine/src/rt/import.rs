//! Reading an export of the game's combat (DESIGN.md, "The export"): the pieces the generated reader is made of.
//! Nothing here forgives: a member that is missing, one too many, a value of another shape, each is an error.

use super::{
    Arena, Combat, Dec, Dict, Fn0, Fn1, Fn2, Fn3, Fn4, Fn5, KeyValuePair, Obj, Queue, Set, Stack,
    Stored, Str,
};
use crate::r#gen::kinds::Kind;
use serde_json::{Map, Value};

pub type Res<T> = Result<T, String>;
pub type Object = Map<String, Value>;

/// A value of the export, read as the type of the field it goes into.
pub trait Rd: Sized {
    fn rd(v: &Value) -> Res<Self>;
}

pub fn object(v: &Value) -> Res<&Object> {
    v.as_object()
        .ok_or_else(|| format!("not an object: {}", brief(v)))
}

pub fn array(v: &Value) -> Res<&Vec<Value>> {
    v.as_array()
        .ok_or_else(|| format!("not an array: {}", brief(v)))
}

fn brief(v: &Value) -> String {
    let text = v.to_string();
    if text.len() > 80 {
        format!(
            "{}..",
            &text[..text.char_indices().nth(80).map_or(text.len(), |x| x.0)]
        )
    } else {
        text
    }
}

/// An object must have exactly this many members.
pub fn members(o: &Object, count: usize) -> Res<()> {
    if o.len() != count {
        return Err(format!(
            "{} members where {} are expected: {}",
            o.len(),
            count,
            o.keys().cloned().collect::<Vec<_>>().join(", ")
        ));
    }
    Ok(())
}

/// The member of an exported object that holds the fields of one class of its chain.
pub fn level<'a>(o: &'a Object, class: &str, fields: usize) -> Res<&'a Object> {
    let m = object(o.get(class).ok_or_else(|| format!("no member {class}"))?)?;
    members(m, fields).map_err(|e| format!("{class}: {e}"))?;
    Ok(m)
}

pub fn field<T: Rd>(m: &Object, name: &str) -> Res<T> {
    let v = m.get(name).ok_or_else(|| format!("no field {name}"))?;
    T::rd(v).map_err(|e| format!("{name}: {e}"))
}

/// Whether a field holds a value: the flag of a collection that can be null in the game (a collection here has no
/// null of its own).
pub fn there(m: &Object, name: &str) -> Res<bool> {
    Ok(!m
        .get(name)
        .ok_or_else(|| format!("no field {name}"))?
        .is_null())
}

/// The objects of one arena.
pub fn arena<T: Stored>(
    into: &mut Arena<T>,
    name: &str,
    v: &Value,
    read: fn(&Value) -> Res<T>,
) -> Res<()> {
    for (i, o) in array(v)?.iter().enumerate() {
        into.alloc(read(o).map_err(|e| format!("{name}[{i}]: {e}"))?);
    }
    Ok(())
}

macro_rules! rd_int {
    ($($t:ty),*) => {$(
        impl Rd for $t {
            fn rd(v: &Value) -> Res<Self> {
                let n = match (v.as_i64(), v.as_u64()) {
                    (Some(n), _) => n as i128,
                    (_, Some(n)) => n as i128,
                    _ => return Err(format!("not a whole number: {}", brief(v))),
                };
                <$t>::try_from(n).map_err(|_| format!("{n} does not fit {}", stringify!($t)))
            }
        }
    )*};
}
rd_int!(i8, i16, i32, i64, u8, u16, u32, u64);

impl Rd for bool {
    fn rd(v: &Value) -> Res<Self> {
        v.as_bool()
            .ok_or_else(|| format!("not a bool: {}", brief(v)))
    }
}

impl Rd for f64 {
    fn rd(v: &Value) -> Res<Self> {
        v.as_f64()
            .ok_or_else(|| format!("not a number: {}", brief(v)))
    }
}

impl Rd for f32 {
    fn rd(v: &Value) -> Res<Self> {
        Ok(f64::rd(v)? as f32)
    }
}

/// A char is its UTF-16 code unit.
impl Rd for char {
    fn rd(v: &Value) -> Res<Self> {
        let n = u32::rd(v)?;
        char::from_u32(n).ok_or_else(|| format!("not a char: {n}"))
    }
}

impl Rd for Dec {
    fn rd(v: &Value) -> Res<Self> {
        let s = v
            .as_str()
            .ok_or_else(|| format!("a decimal is written as a string: {}", brief(v)))?;
        Dec::parse(s).ok_or_else(|| format!("not a decimal: {s}"))
    }
}

impl Rd for Str {
    fn rd(v: &Value) -> Res<Self> {
        match v {
            Value::Null => Ok(Str::NULL),
            Value::String(s) => Ok(Str::from_string(s.clone())),
            _ => Err(format!("not a string: {}", brief(v))),
        }
    }
}

impl Rd for String {
    fn rd(v: &Value) -> Res<Self> {
        v.as_str()
            .map(str::to_owned)
            .ok_or_else(|| format!("not a string: {}", brief(v)))
    }
}

impl Rd for Kind {
    fn rd(v: &Value) -> Res<Self> {
        match v {
            Value::Null => Ok(Kind::None),
            Value::String(s) => {
                crate::r#gen::kinds::kind_by_name(s).ok_or_else(|| format!("no class {s}"))
            }
            _ => Err(format!("not a class name: {}", brief(v))),
        }
    }
}

/// null; `["<arena>", index]`; `["canon", "<Class>"]`. An object of a class outside the schema is refused.
impl Rd for Obj {
    fn rd(v: &Value) -> Res<Self> {
        if v.is_null() {
            return Ok(Obj::NULL);
        }
        let pair = array(v)?;
        let (Some(first), 2) = (pair.first().and_then(Value::as_str), pair.len()) else {
            return Err(format!("not a reference: {}", brief(v)));
        };
        match first {
            "canon" => Ok(crate::manual::canon::of(Kind::rd(&pair[1])?)),
            "outside" => Err(format!(
                "an object of {}, a class the engine does not hold",
                pair[1]
            )),
            arena => {
                let tag = crate::r#gen::state::ARENA_NAMES
                    .iter()
                    .position(|n| *n == arena && !n.is_empty());
                let tag = tag.ok_or_else(|| format!("no arena {arena}"))?;
                Ok(Obj::new(tag as u16, usize::rd(&pair[1])?, false))
            }
        }
    }
}

impl Rd for usize {
    fn rd(v: &Value) -> Res<Self> {
        Ok(u32::rd(v)? as usize)
    }
}

impl<T: Rd> Rd for Option<T> {
    fn rd(v: &Value) -> Res<Self> {
        if v.is_null() {
            Ok(None)
        } else {
            Ok(Some(T::rd(v)?))
        }
    }
}

/// A collection that is null in the game is an empty one here.
fn items<T: Rd>(v: &Value) -> Res<Vec<T>> {
    if v.is_null() {
        return Ok(Vec::new());
    }
    array(v)?
        .iter()
        .enumerate()
        .map(|(i, x)| T::rd(x).map_err(|e| format!("[{i}]: {e}")))
        .collect()
}

impl<T: Rd> Rd for Vec<T> {
    fn rd(v: &Value) -> Res<Self> {
        items(v)
    }
}

/// A hash set or dictionary with freed slots: `{"items": [..], "at": [..], "free": [..]}`. The items in enumeration
/// order, the slot of each, and the freed slots, the next one to be filled first.
fn layout<T: Rd>(o: &Object) -> Res<(Vec<T>, Vec<usize>, Vec<usize>)> {
    members(o, 3)?;
    Ok((field(o, "items")?, field(o, "at")?, field(o, "free")?))
}

impl<T: Rd> Rd for Set<T> {
    fn rd(v: &Value) -> Res<Self> {
        let Value::Object(o) = v else {
            return Ok(Set::from_items(items(v)?));
        };
        let (items, at, free) = layout(o)?;
        Set::from_layout(items, at, free)
    }
}

impl<K: Rd, V: Rd> Rd for Dict<K, V> {
    fn rd(v: &Value) -> Res<Self> {
        let Value::Object(o) = v else {
            return Ok(Dict::from_pairs(items(v)?));
        };
        let (pairs, at, free) = layout(o)?;
        Dict::from_layout(pairs, at, free)
    }
}

impl<T: Rd> Rd for Queue<T> {
    fn rd(v: &Value) -> Res<Self> {
        Ok(Queue::from_items(items(v)?))
    }
}

/// A stack is written from its top.
impl<T: Rd> Rd for Stack<T> {
    fn rd(v: &Value) -> Res<Self> {
        let mut from_top: Vec<T> = items(v)?;
        from_top.reverse();
        Ok(Stack::from_bottom(from_top))
    }
}

impl<K: Rd, V: Rd> Rd for KeyValuePair<K, V> {
    fn rd(v: &Value) -> Res<Self> {
        let (key, value) = <(K, V)>::rd(v)?;
        Ok(KeyValuePair { key, value })
    }
}

macro_rules! rd_tuple {
    ($n:expr; $($t:ident $i:tt),*) => {
        impl<$($t: Rd),*> Rd for ($($t,)*) {
            fn rd(v: &Value) -> Res<Self> {
                let a = array(v)?;
                if a.len() != $n {
                    return Err(format!("{} items where {} are expected", a.len(), $n));
                }
                Ok(($($t::rd(&a[$i])?,)*))
            }
        }
    };
}
rd_tuple!(2; A 0, B 1);
rd_tuple!(3; A 0, B 1, C 2);
rd_tuple!(4; A 0, B 1, C 2, D 3);

/// A delegate: null, or `{"method": "<class>::<method>", "target": reference or null}`, the method by the game's own
/// names (the class by its CLR full name, a lambda by the name the compiler gave it). It is the function of port's
/// table under that name whose type is the field's; a method the engine has no function for is refused.
macro_rules! rd_delegate {
    ($name:ident $(, $ty:ident)*) => {
        impl<$($ty: 'static,)* R: 'static> Rd for $name<$($ty,)* R> {
            fn rd(v: &Value) -> Res<Self> {
                if v.is_null() {
                    return Ok(Self::NULL);
                }
                let o = object(v)?;
                members(o, 2)?;
                let method: String = field(o, "method")?;
                let target: Obj = field(o, "target")?;
                let table = crate::r#gen::fns::BY_NAME;
                let from = table.partition_point(|x| x.0 < method.as_str());
                let same = table[from..].iter().take_while(|x| x.0 == method);
                let mut known = false;
                for (_, id, f) in same {
                    known = true;
                    if let Some(f) = f.downcast_ref::<fn(Obj, $($ty,)* &mut Combat) -> R>() {
                        return Ok(Self::of(*id, target, *f));
                    }
                }
                Err(if known {
                    format!("{method} is not a function of this delegate's type")
                } else {
                    format!("the engine has no function {method}")
                })
            }
        }
    };
}
rd_delegate!(Fn0);
rd_delegate!(Fn1, A);
rd_delegate!(Fn2, A, B);
rd_delegate!(Fn3, A, B, D);
rd_delegate!(Fn4, A, B, D, E);
rd_delegate!(Fn5, A, B, D, E, F);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::rt::R;
    use serde_json::json;

    const UNSET: &str =
        "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState::UnsetMove";

    #[test]
    fn a_delegate_is_read_by_the_games_name_of_its_method() {
        let v = json!({"method": UNSET, "target": null});
        let f = Fn1::<Vec<Obj>, R<()>>::rd(&v).unwrap();
        assert_eq!(f.bound().map(|(_, target)| target), Some(Obj::NULL));
        // The same method read twice is the same delegate, as two delegates of one method are equal in C#.
        assert!(f == Fn1::<Vec<Obj>, R<()>>::rd(&v).unwrap());
        assert!(Fn1::<Vec<Obj>, R<()>>::rd(&Value::Null).unwrap().is_null());
    }

    #[test]
    fn a_delegate_of_another_type_or_of_no_function_is_refused() {
        let v = json!({"method": UNSET, "target": null});
        let e = Fn0::<bool>::rd(&v).unwrap_err();
        assert!(e.contains("not a function of this delegate's type"), "{e}");
        let v = json!({"method": "Some.Class::NoSuchMethod", "target": null});
        let e = Fn0::<bool>::rd(&v).unwrap_err();
        assert!(e.contains("the engine has no function"), "{e}");
        let v = json!({"method": UNSET});
        assert!(Fn1::<Vec<Obj>, R<()>>::rd(&v).is_err());
    }
}
