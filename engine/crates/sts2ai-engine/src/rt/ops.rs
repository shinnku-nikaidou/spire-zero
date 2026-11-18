//! The operations of C# that are not one Rust operator: null tests, type tests and casts, ToString, CompareTo, Math.

use super::coll::{Dict, Queue, Set, Stack};
use super::{Combat, Dec, MidpointRounding, Obj, R, Str};
use crate::r#gen::kinds::{Kind, is_subclass, kind_of};

/// What a type parameter of translated generic code can be.
pub trait Val: Clone + PartialEq + Default + 'static {}
impl<T: Clone + PartialEq + Default + 'static> Val for T {}

/// C#'s cast of an object to a type parameter, `(T)obj`: the object itself where the parameter stands for a class.
pub fn unbox<T: Clone + 'static>(o: Obj) -> T {
    (&o as &dyn std::any::Any)
        .downcast_ref::<T>()
        .expect("a cast of an object to a type that is no class")
        .clone()
}

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
/// A group that LINQ found none of (FirstOrDefault) is the empty one: a group that exists holds something.
impl<K, T> IsNull for super::coll::Group<K, T> {
    fn is_null(&self) -> bool {
        self.items.is_empty()
    }
}
/// A collection that can be null (an Option: port's World.ReturnsNullColl and Body's nullable locals), read as the
/// collection: null is C#'s NullReferenceException.
pub fn nn<T: Clone>(x: &Option<T>) -> T {
    x.as_ref().expect("NullReferenceException: a collection that is null").clone()
}
/// The same, to read without copying.
pub fn nn_ref<T>(x: &Option<T>) -> &T {
    x.as_ref().expect("NullReferenceException: a collection that is null")
}
/// The same, to change in place.
pub fn nn_mut<T>(x: &mut Option<T>) -> &mut T {
    x.as_mut().expect("NullReferenceException: a collection that is null")
}
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

/// The `Equals` of a class of the game whose equality is its own (port: Body.OwnEquals), as the comparisons below call
/// it: on the first value, given the second.
pub type OwnEquals = fn(Obj, Obj, &mut Combat) -> bool;

/// C#: `EqualityComparer<T>.Default.IndexOf` over a list of objects of a class whose equality is its own: the first
/// item that equals `x`, asked of the item (`items[i].Equals(x)`); for a null `x`, the first null.
pub fn position_eq(items: &[Obj], x: Obj, eq: OwnEquals, c: &mut Combat) -> Option<usize> {
    for (at, &item) in items.iter().enumerate() {
        if x.is_null() {
            if item.is_null() {
                return Some(at);
            }
        } else if !item.is_null() && eq(item, x, c) {
            return Some(at);
        }
    }
    None
}

/// C#: `object.Equals(a, b)`: the same object, or neither null and `a.Equals(b)`.
pub fn object_equals(a: Obj, b: Obj, eq: OwnEquals, c: &mut Combat) -> bool {
    a == b || (!a.is_null() && !b.is_null() && eq(a, b, c))
}

/// C#: `EqualityComparer<T>.Default.Equals(a, b)` for a class: both null, or neither null and `a.Equals(b)`.
pub fn comparer_equals(a: Obj, b: Obj, eq: OwnEquals, c: &mut Combat) -> bool {
    if a.is_null() {
        b.is_null()
    } else {
        !b.is_null() && eq(a, b, c)
    }
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
    crate::r#gen::kinds::is_kind(o, kind, c)
}
/// `(T)x`: null stays null; an object of another class panics, where C# throws InvalidCastException.
pub fn cast(o: Obj, kind: Kind, c: &Combat) -> Obj {
    if !o.is_null() && !is_subclass(kind_of(o, c), kind) {
        panic!(
            "InvalidCastException: {:?} ({:?}) is not a {:?}",
            o,
            kind_of(o, c),
            kind
        );
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
        if Str::is_null(*self) {
            Str::EMPTY
        } else {
            *self
        }
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
cmp_ord!(
    i8, i16, i32, i64, u8, u16, u32, u64, f32, f64, Dec, bool, char
);
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
/// Math.Max and Math.Min as .NET 9 has them: of equal numbers, Max gives the first and Min the second (a decimal's
/// scale shows which); of floats, NaN wins and Max(-0, +0) is +0, Min(-0, +0) is -0.
pub trait MinMax: PartialOrd + Sized {
    fn net_max(self, o: Self) -> Self {
        if self < o { o } else { self }
    }
    fn net_min(self, o: Self) -> Self {
        if self < o { self } else { o }
    }
}
impl MinMax for i8 {}
impl MinMax for i16 {}
impl MinMax for i32 {}
impl MinMax for i64 {}
impl MinMax for u8 {}
impl MinMax for u16 {}
impl MinMax for u32 {}
impl MinMax for u64 {}
impl MinMax for Dec {}
macro_rules! float_min_max {
    ($($ty:ty),*) => {
        $(impl MinMax for $ty {
            fn net_max(self, o: Self) -> Self {
                if self != o {
                    if self.is_nan() || o < self { self } else { o }
                } else if o.is_sign_negative() { self } else { o }
            }
            fn net_min(self, o: Self) -> Self {
                if self != o {
                    if self.is_nan() || self < o { self } else { o }
                } else if self.is_sign_negative() { self } else { o }
            }
        })*
    };
}
float_min_max!(f32, f64);
pub fn max<T: MinMax>(a: T, b: T) -> T {
    a.net_max(b)
}
pub fn min<T: MinMax>(a: T, b: T) -> T {
    a.net_min(b)
}

/// Math.Abs of an int: .NET throws for the one value with no positive.
pub trait AbsChecked: Sized {
    fn abs_checked(self) -> Self;
}
impl AbsChecked for i32 {
    fn abs_checked(self) -> Self {
        self.checked_abs().expect("Negating the minimum value of a twos complement number is invalid.")
    }
}
impl AbsChecked for i64 {
    fn abs_checked(self) -> Self {
        self.checked_abs().expect("Negating the minimum value of a twos complement number is invalid.")
    }
}
pub fn abs_checked<T: AbsChecked>(x: T) -> T {
    x.abs_checked()
}

/// .NET's FormatFlagNames: the names of the flags a value is made of, lowest first, joined by ", "; None when some
/// bits have no name. `values` are the enum's values as unsigned, each with one name, lowest first.
pub fn flags_text(value: u64, values: &[(u64, &str)]) -> Option<String> {
    if value == 0 {
        return Some("0".to_string());
    }
    let mut left = value;
    let mut found = Vec::new();
    // From the highest value not above it, down.
    let mut at = values.iter().rposition(|&(v, _)| v <= value)?;
    loop {
        let (v, name) = values[at];
        if at == 0 && v == 0 {
            break;
        }
        if left & v == v {
            left &= !v;
            found.push(name);
            if left == 0 {
                break;
            }
        }
        if at == 0 {
            break;
        }
        at -= 1;
    }
    if left != 0 {
        return None;
    }
    found.reverse();
    Some(found.join(", "))
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

/// Task.WhenAll: every task has already run, but a hook that waits for the player (`Stop::Pending`), whose rest
/// runs here as the action the executor runs next (manual/hook_player_choice_context.rs, `wait`); the first other
/// stop among them is the stop.
pub fn when_all(tasks: Vec<R<()>>, c: &mut Combat) -> R<()> {
    let mut waiting = Vec::new();
    for t in tasks {
        match t {
            Err(super::Stop::Pending(context)) => waiting.push(context),
            t => t?,
        }
    }
    if !waiting.is_empty() {
        crate::manual::hook_player_choice_context::wait(&waiting, c)?;
    }
    Ok(())
}
pub fn when_all_values<T>(tasks: Vec<R<T>>) -> R<Vec<T>> {
    tasks.into_iter().collect()
}

/// A whole sequence handed out value by value: `k` takes each and answers whether the walk ends there. Gives back
/// whether it did. (A sequence written with yield has its own function of this shape: see World.Yields in port.)
pub fn walk<T>(items: Vec<T>, c: &mut Combat, k: &mut dyn FnMut(T, &mut Combat) -> R<bool>) -> R<bool> {
    for item in items {
        if k(item, c)? {
            return Ok(true);
        }
    }
    Ok(false)
}

/// The kinds the code a walk hands its values to does something for (`Combat::only`): a test of a kind, and its
/// number among the tests port made (gen/state.rs MEMO_FILTERS), under which a kept walk keeps what it picked.
#[derive(Debug)]
pub struct Only {
    pub id: usize,
    pub test: fn(Kind) -> bool,
}

/// Whether a walk hands a value on to code that does something only for some kinds (`Combat::only`, taken when
/// the walk began): every other kind is passed over, and null is handed on, as C# would call it and throw.
#[inline]
pub fn wanted(only: Option<&'static Only>, o: Obj, c: &Combat) -> bool {
    match only {
        None => true,
        Some(only) => o.is_null() || (only.test)(kind_of(o, c)),
    }
}

/// The end of a walk whose body awaits nothing: no stop can have left it.
pub fn walked(r: R<bool>) -> bool {
    match r {
        Ok(ended) => ended,
        Err(_) => panic!("a stop left a walk that awaits nothing"),
    }
}

#[cfg(test)]
mod net_tests {
    use super::*;

    /// Math.Max and Math.Min of floats as .NET 9: NaN wins, and of +0 and -0 Max is +0, Min is -0.
    #[test]
    fn float_max_and_min() {
        assert!(max(f32::NAN, 1.0).is_nan() && max(1.0, f32::NAN).is_nan());
        assert!(min(f32::NAN, 1.0).is_nan() && min(1.0f64, f64::NAN).is_nan());
        assert!(max(-0.0f32, 0.0).is_sign_positive() && max(0.0f32, -0.0).is_sign_positive());
        assert!(min(-0.0f32, 0.0).is_sign_negative() && min(0.0f32, -0.0).is_sign_negative());
        assert_eq!(max(2, 3), 3);
        assert_eq!(min(2.5f32, -1.0), -1.0);
    }

    /// A [Flags] value no one name has: its flags' names, lowest first; the number where bits have no name.
    #[test]
    fn flags_text_as_dotnet() {
        let values = [(0, "None"), (1, "A"), (2, "B"), (4, "C"), (6, "BC")];
        assert_eq!(flags_text(3, &values).as_deref(), Some("A, B"));
        assert_eq!(flags_text(7, &values).as_deref(), Some("A, BC"));
        assert_eq!(flags_text(9, &values), None);
    }

    #[test]
    #[should_panic(expected = "twos complement")]
    fn abs_of_the_minimum_int_throws() {
        abs_checked(i32::MIN);
    }

    #[test]
    fn null_is_white_space() {
        assert!(Str::NULL.is_null_or_white_space());
        assert!(Str::lit(" \t").is_null_or_white_space());
        assert!(!Str::lit(" a").is_null_or_white_space());
    }
}
