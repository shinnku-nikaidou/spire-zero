//! The operations of C# that are not one Rust operator: null tests, type tests and casts, ToString, CompareTo, Math.

use super::coll::{Dict, Queue, Set, Stack};
use super::{Combat, Dec, MidpointRounding, Obj, R, Str};
use crate::r#gen::kinds::{Kind, is_subclass, kind_of};

/// What a type parameter of translated generic code can be.
pub trait Val: Clone + PartialEq + Default + 'static {}
impl<T: Clone + PartialEq + Default + 'static> Val for T {}

/// A thrown exception as a value (only its class is kept).
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Exception {
    pub name: &'static str,
}
impl Exception {
    pub fn new(name: &'static str) -> Exception {
        Exception { name }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum StringComparison {
    #[default]
    Ordinal,
    OrdinalIgnoreCase,
    InvariantCulture,
    InvariantCultureIgnoreCase,
    CurrentCulture,
    CurrentCultureIgnoreCase,
}

// ---------------------------------------------------------------- null

pub trait IsNull {
    fn is_null(&self) -> bool;
}
impl IsNull for Obj {
    fn is_null(&self) -> bool {
        self.0 == 0
    }
}
/// No exception: the one with no class.
impl IsNull for Exception {
    fn is_null(&self) -> bool {
        self.name.is_empty()
    }
}
impl IsNull for Str {
    fn is_null(&self) -> bool {
        Str::is_null(*self)
    }
}
impl<T> IsNull for Option<T> {
    fn is_null(&self) -> bool {
        self.is_none()
    }
}
impl IsNull for Kind {
    fn is_null(&self) -> bool {
        *self == Kind::None
    }
}
// A collection is never null here: a null one is an empty one.
impl<T> IsNull for Vec<T> {
    fn is_null(&self) -> bool {
        false
    }
}
impl<T> IsNull for Set<T> {
    fn is_null(&self) -> bool {
        false
    }
}
impl<K, V> IsNull for Dict<K, V> {
    fn is_null(&self) -> bool {
        false
    }
}
impl<T> IsNull for Queue<T> {
    fn is_null(&self) -> bool {
        false
    }
}
impl<T> IsNull for Stack<T> {
    fn is_null(&self) -> bool {
        false
    }
}
impl<T> IsNull for R<T> {
    fn is_null(&self) -> bool {
        false
    }
}

pub fn is_null<T: IsNull>(x: &T) -> bool {
    x.is_null()
}

pub fn throw_if_null<T: IsNull>(x: &T, at: &str) {
    if x.is_null() {
        panic!("{at}: ArgumentNullException");
    }
}
pub fn throw_if(bad: bool, at: &str) {
    if bad {
        panic!("{at}: ArgumentOutOfRangeException");
    }
}

// ---------------------------------------------------------------- classes

/// `x is T`.
pub fn is(o: Obj, kind: Kind, c: &Combat) -> bool {
    !o.is_null() && is_subclass(kind_of(o, c), kind)
}
/// `(T)x`: null stays null; an object of another class panics, where C# throws InvalidCastException.
pub fn cast(o: Obj, kind: Kind, c: &Combat) -> Obj {
    if !o.is_null() && !is_subclass(kind_of(o, c), kind) {
        panic!("InvalidCastException: {:?} ({:?}) is not a {:?}", o, kind_of(o, c), kind);
    }
    o
}
/// `x as T`.
pub fn as_(o: Obj, kind: Kind, c: &Combat) -> Obj {
    if is(o, kind, c) { o } else { Obj::NULL }
}

// ---------------------------------------------------------------- text

/// C#'s ToString.
pub trait ToS {
    fn to_s(&self, c: &mut Combat) -> Str;
}
macro_rules! to_s_display {
    ($($ty:ty),*) => {
        $(impl ToS for $ty {
            fn to_s(&self, _c: &mut Combat) -> Str {
                Str::from_string(self.to_string())
            }
        })*
    };
}
to_s_display!(i8, i16, i32, i64, u8, u16, u32, u64, Dec, char);
impl ToS for bool {
    fn to_s(&self, _c: &mut Combat) -> Str {
        Str::lit(if *self { "True" } else { "False" })
    }
}
impl ToS for Str {
    fn to_s(&self, _c: &mut Combat) -> Str {
        if Str::is_null(*self) { Str::EMPTY } else { *self }
    }
}
impl ToS for Obj {
    fn to_s(&self, c: &mut Combat) -> Str {
        crate::manual::object::to_string(*self, c)
    }
}
impl ToS for Kind {
    fn to_s(&self, _c: &mut Combat) -> Str {
        crate::r#gen::kinds::kind_name(*self)
    }
}
impl<T: ToS> ToS for Option<T> {
    fn to_s(&self, c: &mut Combat) -> Str {
        match self {
            Some(x) => x.to_s(c),
            None => Str::EMPTY,
        }
    }
}
// Floats print as .NET's shortest round-trip form; whole numbers without a fraction.
impl ToS for f32 {
    fn to_s(&self, _c: &mut Combat) -> Str {
        Str::from_string(format!("{}", self))
    }
}
impl ToS for f64 {
    fn to_s(&self, _c: &mut Combat) -> Str {
        Str::from_string(format!("{}", self))
    }
}

pub fn to_s<T: ToS>(x: T, c: &mut Combat) -> Str {
    x.to_s(c)
}

pub fn char_from(code: u32) -> char {
    char::from_u32(code).expect("not a character")
}

// ---------------------------------------------------------------- order

/// C#'s CompareTo: negative, zero or positive.
pub trait Cmp {
    fn cmp_net(&self, other: &Self, c: &mut Combat) -> i32;
}
macro_rules! cmp_ord {
    ($($ty:ty),*) => {
        $(impl Cmp for $ty {
            fn cmp_net(&self, other: &Self, _c: &mut Combat) -> i32 {
                (*self > *other) as i32 - (*self < *other) as i32
            }
        })*
    };
}
cmp_ord!(i8, i16, i32, i64, u8, u16, u32, u64, f32, f64, Dec, bool, char);
impl Cmp for Str {
    fn cmp_net(&self, other: &Self, _c: &mut Combat) -> i32 {
        self.compare(*other)
    }
}
impl<T: Cmp> Cmp for Option<T> {
    fn cmp_net(&self, other: &Self, c: &mut Combat) -> i32 {
        match (self, other) {
            (None, None) => 0,
            (None, Some(_)) => -1,
            (Some(_), None) => 1,
            (Some(a), Some(b)) => a.cmp_net(b, c),
        }
    }
}
impl Cmp for Obj {
    fn cmp_net(&self, other: &Self, c: &mut Combat) -> i32 {
        crate::manual::object::compare_to(*self, *other, c)
    }
}

pub fn compare<T: Cmp>(a: T, b: T, c: &mut Combat) -> i32 {
    a.cmp_net(&b, c)
}

// ---------------------------------------------------------------- numbers

/// Math.Max: the first argument when the two are equal, and Math.Min the second, as .NET's decimal does (the two can
/// differ in scale: Math.Min(1.0m, 1.00m) is 1.00).
pub fn max<T: PartialOrd>(a: T, b: T) -> T {
    if a >= b { a } else { b }
}
pub fn min<T: PartialOrd>(a: T, b: T) -> T {
    if a < b { a } else { b }
}
pub fn clamp<T: PartialOrd>(v: T, lo: T, hi: T) -> T {
    if v < lo {
        lo
    } else if v > hi {
        hi
    } else {
        v
    }
}

pub trait Signed {
    fn sign_net(self) -> i32;
}
macro_rules! signed {
    ($($ty:ty: $zero:expr),*) => {
        $(impl Signed for $ty {
            fn sign_net(self) -> i32 {
                (self > $zero) as i32 - (self < $zero) as i32
            }
        })*
    };
}
signed!(i32: 0, i64: 0, f32: 0.0, f64: 0.0);
impl Signed for Dec {
    fn sign_net(self) -> i32 {
        self.sign()
    }
}
pub fn sign<T: Signed>(x: T) -> i32 {
    x.sign_net()
}

/// Math.Round for floats: halves go to the even neighbour.
pub trait RoundEven {
    fn round_even(self) -> Self;
    fn round_mode(self, mode: MidpointRounding) -> Self;
}
macro_rules! round_even {
    ($($ty:ty),*) => {
        $(impl RoundEven for $ty {
            fn round_even(self) -> Self {
                self.round_ties_even()
            }
            fn round_mode(self, mode: MidpointRounding) -> Self {
                match mode {
                    MidpointRounding::ToEven => self.round_ties_even(),
                    MidpointRounding::AwayFromZero => self.round(),
                    MidpointRounding::ToZero => self.trunc(),
                    MidpointRounding::ToNegativeInfinity => self.floor(),
                    MidpointRounding::ToPositiveInfinity => self.ceil(),
                }
            }
        })*
    };
}
round_even!(f32, f64);
pub fn round_even<T: RoundEven>(x: T) -> T {
    x.round_even()
}
pub fn round_mode<T: RoundEven>(x: T, mode: MidpointRounding) -> T {
    x.round_mode(mode)
}
pub fn lerp(from: f32, to: f32, weight: f32) -> f32 {
    from + (to - from) * weight
}

// ---------------------------------------------------------------- nullable values

/// A lifted operator: null when either side is.
pub fn lifted<A, B, T>(a: Option<A>, b: Option<B>, f: impl FnOnce(A, B) -> T) -> Option<T> {
    match (a, b) {
        (Some(a), Some(b)) => Some(f(a, b)),
        _ => None,
    }
}
/// A lifted comparison: false when either side is null.
pub fn lifted_test<A, B>(a: Option<A>, b: Option<B>, f: impl FnOnce(A, B) -> bool) -> bool {
    match (a, b) {
        (Some(a), Some(b)) => f(a, b),
        _ => false,
    }
}

// ---------------------------------------------------------------- tasks

/// Task.WhenAll: every task has already run; the first stop among them is the stop.
pub fn when_all(tasks: Vec<R<()>>) -> R<()> {
    for t in tasks {
        t?;
    }
    Ok(())
}
pub fn when_all_values<T>(tasks: Vec<R<T>>) -> R<Vec<T>> {
    tasks.into_iter().collect()
}
