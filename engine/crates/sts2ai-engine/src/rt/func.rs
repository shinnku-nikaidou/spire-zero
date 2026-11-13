//! C# delegates: a function value that can be stored, copied and null. The combat is always the last argument.
//!
//! A delegate of the game is a method and the object it is bound to (`Delegate.Method`, `Delegate.Target`), and that
//! is what one is here wherever port can name the method: a function of port's table (`gen/fns.rs`) and a target (the
//! object of a method group, the object or the closure a lambda has captured, nothing for a static one). Such a
//! delegate is data: it can be compared, and an export can carry it. A function made where it is used and never
//! stored in the game's state (`new`) is just the code.

use super::{Combat, Obj};
use std::sync::Arc;

macro_rules! delegate {
    ($name:ident, $repr:ident $(, $arg:ident : $ty:ident)*) => {
        enum $repr<$($ty,)* R> {
            Null,
            /// A function of port's table with what it is bound to.
            Bound { id: u32, target: Obj, run: fn(Obj, $($ty,)* &mut Combat) -> R },
            /// Code made on the spot.
            Made(Arc<dyn Fn($($ty,)* &mut Combat) -> R + Send + Sync>),
        }

        pub struct $name<$($ty,)* R>($repr<$($ty,)* R>);

        impl<$($ty,)* R> $name<$($ty,)* R> {
            pub const NULL: Self = $name($repr::Null);
            pub fn new(f: impl Fn($($ty,)* &mut Combat) -> R + Send + Sync + 'static) -> Self {
                $name($repr::Made(Arc::new(f)))
            }
            /// The function `id` of port's table, bound to `target`.
            pub fn of(id: u32, target: Obj, f: fn(Obj, $($ty,)* &mut Combat) -> R) -> Self {
                $name($repr::Bound { id, target, run: f })
            }
            pub fn is_null(&self) -> bool {
                matches!(self.0, $repr::Null)
            }
            /// The function's number in port's table and its target, when the delegate is one of the table's.
            pub fn bound(&self) -> Option<(u32, Obj)> {
                match &self.0 {
                    $repr::Bound { id, target, .. } => Some((*id, *target)),
                    _ => None,
                }
            }
            /// Calls it. Panics on null, where C# throws NullReferenceException.
            pub fn call(&self, $($arg: $ty,)* c: &mut Combat) -> R {
                match &self.0 {
                    $repr::Bound { target, run, .. } => run(*target, $($arg,)* c),
                    $repr::Made(f) => f($($arg,)* c),
                    $repr::Null => panic!("null delegate"),
                }
            }
        }
        impl<$($ty,)* R> Clone for $name<$($ty,)* R> {
            fn clone(&self) -> Self {
                $name(match &self.0 {
                    $repr::Null => $repr::Null,
                    $repr::Bound { id, target, run } => $repr::Bound { id: *id, target: *target, run: *run },
                    $repr::Made(f) => $repr::Made(f.clone()),
                })
            }
        }
        impl<$($ty,)* R> Default for $name<$($ty,)* R> {
            fn default() -> Self {
                $name($repr::Null)
            }
        }
        /// C#'s equality of delegates: the same method on the same target.
        impl<$($ty,)* R> PartialEq for $name<$($ty,)* R> {
            fn eq(&self, other: &Self) -> bool {
                match (&self.0, &other.0) {
                    ($repr::Null, $repr::Null) => true,
                    ($repr::Bound { id: a, target: s, .. }, $repr::Bound { id: b, target: t, .. }) => a == b && s == t,
                    ($repr::Made(a), $repr::Made(b)) => Arc::ptr_eq(a, b),
                    _ => false,
                }
            }
        }
        impl<$($ty,)* R> std::fmt::Debug for $name<$($ty,)* R> {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                match &self.0 {
                    $repr::Null => f.write_str("null"),
                    $repr::Bound { id, target, .. } => write!(f, concat!(stringify!($name), "(#{} on {:?})"), id, target),
                    $repr::Made(_) => f.write_str(concat!(stringify!($name), "(..)")),
                }
            }
        }
        impl<$($ty,)* R> super::IsNull for $name<$($ty,)* R> {
            fn is_null(&self) -> bool {
                matches!(self.0, $repr::Null)
            }
        }
    };
}

delegate!(Fn0, Repr0);
delegate!(Fn1, Repr1, a: A);
delegate!(Fn2, Repr2, a: A, b: B);
delegate!(Fn3, Repr3, a: A, b: B, d: D);
delegate!(Fn4, Repr4, a: A, b: B, d: D, e: E);
delegate!(Fn5, Repr5, a: A, b: B, d: D, e: E, f: F);
