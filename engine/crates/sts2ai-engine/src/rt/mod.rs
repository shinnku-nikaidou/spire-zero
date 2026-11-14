//! The primitives the translated code stands on: handles and arenas, the numbers and strings of .NET, its collections,
//! LINQ, delegates, the random streams, and the combat itself.

pub mod coll;
pub mod combat;
pub mod dec;
pub mod func;
pub mod import;
pub mod linq;
pub mod list;
pub mod memo;
pub mod obj;
pub mod ops;
pub mod random;
pub mod str;

pub use coll::{Dict, Group, IntoVec, KeyValuePair, Ordered, Queue, Set, Stack};
pub use combat::{Choices, Combat, R, Stop};
pub use dec::{Dec, MidpointRounding};
pub use func::{Fn0, Fn1, Fn2, Fn3, Fn4, Fn5, unsubscribe};
pub use obj::{AnyArena, Arena, Obj, Stored};
pub use ops::*;
pub use str::Str;

/// A C# enum: a number with named values. Gives it the operators of C# flags, its name as text, and its order.
#[macro_export]
macro_rules! cs_enum {
    ($name:ident, $under:ty) => {
        impl std::ops::BitOr for $name {
            type Output = $name;
            fn bitor(self, o: $name) -> $name {
                $name(self.0 | o.0)
            }
        }
        impl std::ops::BitAnd for $name {
            type Output = $name;
            fn bitand(self, o: $name) -> $name {
                $name(self.0 & o.0)
            }
        }
        impl std::ops::BitXor for $name {
            type Output = $name;
            fn bitxor(self, o: $name) -> $name {
                $name(self.0 ^ o.0)
            }
        }
        impl std::ops::Not for $name {
            type Output = $name;
            fn not(self) -> $name {
                $name(!self.0)
            }
        }
        impl $name {
            /// C#'s Enum.HasFlag.
            pub fn has_flag(self, flag: $name) -> bool {
                self.0 & flag.0 == flag.0
            }
        }
        impl $crate::rt::import::Rd for $name {
            fn rd(v: &serde_json::Value) -> $crate::rt::import::Res<Self> {
                Ok($name(<$under as $crate::rt::import::Rd>::rd(v)?))
            }
        }
        impl $crate::rt::ToS for $name {
            fn to_s(&self, _c: &mut $crate::rt::Combat) -> $crate::rt::Str {
                match self.name() {
                    Some(n) => $crate::rt::Str::lit(n),
                    None => $crate::rt::Str::from_string(self.unnamed_text()),
                }
            }
        }
        impl $crate::rt::Cmp for $name {
            fn cmp_net(&self, other: &Self, _c: &mut $crate::rt::Combat) -> i32 {
                (self.0 > other.0) as i32 - (self.0 < other.0) as i32
            }
        }
    };
}
pub use cs_enum;
