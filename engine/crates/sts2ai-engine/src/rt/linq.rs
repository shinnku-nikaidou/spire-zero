//! System.Linq over lists. Sources are owned lists; a function argument gets the item and the combat. Everything is
//! evaluated at once, in order; where the game depends on LINQ's laziness, the translator unrolls the loop instead.

use super::coll::{Group, Ordered};
use super::{Cmp, Combat, Dec, Dict, Obj};
use crate::r#gen::kinds::Kind;

pub fn is_empty<T>(s: &[T]) -> bool {
    s.is_empty()
}
pub fn count<T>(s: &[T]) -> i32 {
    s.len() as i32
}
pub fn contains<T: PartialEq>(s: &[T], x: &T) -> bool {
    s.contains(x)
}

pub fn where_<T: Clone>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> Vec<T> {
    let mut out = Vec::new();
    for x in s {
        if f(x.clone(), c) {
            out.push(x);
        }
    }
    out
}
pub fn select<T, U>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> U, c: &mut Combat) -> Vec<U> {
    let mut out = Vec::with_capacity(s.len());
    for x in s {
        out.push(f(x, c));
    }
    out
}
pub fn select_indexed<T, U>(s: Vec<T>, mut f: impl FnMut(T, i32, &mut Combat) -> U, c: &mut Combat) -> Vec<U> {
    let mut out = Vec::with_capacity(s.len());
    for (i, x) in s.into_iter().enumerate() {
        out.push(f(x, i as i32, c));
    }
    out
}
pub fn select_many<T, U>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> Vec<U>, c: &mut Combat) -> Vec<U> {
    let mut out = Vec::new();
    for x in s {
        out.extend(f(x, c));
    }
    out
}
pub fn any<T>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> bool {
    for x in s {
        if f(x, c) {
            return true;
        }
    }
    false
}
pub fn all<T>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> bool {
    for x in s {
        if !f(x, c) {
            return false;
        }
    }
    true
}
pub fn count_by<T>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> i32 {
    let mut n = 0;
    for x in s {
        if f(x, c) {
            n += 1;
        }
    }
    n
}
pub fn find_index<T>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> i32 {
    for (i, x) in s.into_iter().enumerate() {
        if f(x, c) {
            return i as i32;
        }
    }
    -1
}

pub fn first<T>(s: Vec<T>) -> T {
    s.into_iter().next().expect("Sequence contains no elements")
}
pub fn first_or_default<T: Default>(s: Vec<T>) -> T {
    s.into_iter().next().unwrap_or_default()
}
pub fn first_by<T: Clone>(s: Vec<T>, f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> T {
    where_first(s, f, c).expect("Sequence contains no matching element")
}
pub fn first_or_default_by<T: Clone + Default>(s: Vec<T>, f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> T {
    where_first(s, f, c).unwrap_or_default()
}
fn where_first<T: Clone>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> Option<T> {
    for x in s {
        if f(x.clone(), c) {
            return Some(x);
        }
    }
    None
}
pub fn last<T>(s: Vec<T>) -> T {
    s.into_iter().next_back().expect("Sequence contains no elements")
}
pub fn last_or_default<T: Default>(s: Vec<T>) -> T {
    s.into_iter().next_back().unwrap_or_default()
}
/// Last(predicate): .NET walks a list from its end.
pub fn last_by<T: Clone>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> T {
    for x in s.into_iter().rev() {
        if f(x.clone(), c) {
            return x;
        }
    }
    panic!("Sequence contains no matching element")
}
pub fn last_or_default_by<T: Clone + Default>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> T {
    for x in s.into_iter().rev() {
        if f(x.clone(), c) {
            return x;
        }
    }
    T::default()
}
pub fn single<T>(s: Vec<T>) -> T {
    assert!(s.len() == 1, "Sequence contains {} elements where one was expected", s.len());
    s.into_iter().next().unwrap()
}
pub fn single_or_default<T: Default>(s: Vec<T>) -> T {
    assert!(s.len() <= 1, "Sequence contains more than one element");
    s.into_iter().next().unwrap_or_default()
}
pub fn single_by<T: Clone>(s: Vec<T>, f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> T {
    single(where_(s, f, c))
}
pub fn single_or_default_by<T: Clone + Default>(s: Vec<T>, f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> T {
    single_or_default(where_(s, f, c))
}

pub fn to_dictionary<T: Clone, K: PartialEq + Clone, V: Clone>(
    s: Vec<T>,
    mut key: impl FnMut(T, &mut Combat) -> K,
    mut value: impl FnMut(T, &mut Combat) -> V,
    c: &mut Combat,
) -> Dict<K, V> {
    let mut d = Dict::new();
    for x in s {
        let k = key(x.clone(), c);
        let v = value(x, c);
        d.add(k, v);
    }
    d
}

pub fn concat<T>(mut a: Vec<T>, b: Vec<T>) -> Vec<T> {
    a.extend(b);
    a
}
pub fn append<T>(mut a: Vec<T>, x: T) -> Vec<T> {
    a.push(x);
    a
}
pub fn prepend<T>(mut a: Vec<T>, x: T) -> Vec<T> {
    a.insert(0, x);
    a
}
pub fn distinct<T: PartialEq>(s: Vec<T>) -> Vec<T> {
    let mut out: Vec<T> = Vec::new();
    for x in s {
        if !out.contains(&x) {
            out.push(x);
        }
    }
    out
}
pub fn distinct_by<T: Clone, K: PartialEq>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> K, c: &mut Combat) -> Vec<T> {
    let mut keys: Vec<K> = Vec::new();
    let mut out = Vec::new();
    for x in s {
        let k = f(x.clone(), c);
        if !keys.contains(&k) {
            keys.push(k);
            out.push(x);
        }
    }
    out
}
/// Except: the distinct items of a that are not in b.
pub fn except<T: PartialEq>(a: Vec<T>, b: Vec<T>) -> Vec<T> {
    let mut out: Vec<T> = Vec::new();
    for x in a {
        if !b.contains(&x) && !out.contains(&x) {
            out.push(x);
        }
    }
    out
}
pub fn intersect<T: PartialEq>(a: Vec<T>, b: Vec<T>) -> Vec<T> {
    let mut out: Vec<T> = Vec::new();
    for x in a {
        if b.contains(&x) && !out.contains(&x) {
            out.push(x);
        }
    }
    out
}
pub fn union<T: PartialEq>(a: Vec<T>, b: Vec<T>) -> Vec<T> {
    distinct(concat(a, b))
}

/// Orders the items of one tie group by a key; returns the new order and its tie groups.
fn order_group<T: Clone, K: Cmp>(items: Vec<T>, f: &mut impl FnMut(T, &mut Combat) -> K, descending: bool, c: &mut Combat) -> (Vec<T>, Vec<u32>) {
    let mut keys = Vec::with_capacity(items.len());
    for x in &items {
        keys.push(f(x.clone(), c));
    }
    let mut order: Vec<usize> = (0..items.len()).collect();
    order.sort_by(|&a, &b| {
        let o = keys[a].cmp_net(&keys[b], c).cmp(&0);
        if descending { o.reverse() } else { o }
    });
    let mut rank = Vec::with_capacity(items.len());
    let mut r = 0u32;
    for (i, &at) in order.iter().enumerate() {
        if i > 0 && keys[order[i - 1]].cmp_net(&keys[at], c) != 0 {
            r += 1;
        }
        rank.push(r);
    }
    (order.into_iter().map(|i| items[i].clone()).collect(), rank)
}

pub fn order_by<T: Clone, K: Cmp>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> K, descending: bool, c: &mut Combat) -> Ordered<T> {
    let (items, rank) = order_group(s, &mut f, descending, c);
    Ordered { items, rank }
}
pub fn then_by<T: Clone, K: Cmp>(o: Ordered<T>, mut f: impl FnMut(T, &mut Combat) -> K, descending: bool, c: &mut Combat) -> Ordered<T> {
    let mut items = Vec::with_capacity(o.items.len());
    let mut rank = Vec::with_capacity(o.items.len());
    let mut next = 0u32;
    let mut start = 0;
    while start < o.items.len() {
        let mut end = start + 1;
        while end < o.items.len() && o.rank[end] == o.rank[start] {
            end += 1;
        }
        let (group, sub) = order_group(o.items[start..end].to_vec(), &mut f, descending, c);
        let top = sub.last().copied().unwrap_or(0);
        items.extend(group);
        rank.extend(sub.into_iter().map(|r| next + r));
        next += top + 1;
        start = end;
    }
    Ordered { items, rank }
}

/// What Sum, Average, Max and Min work on.
pub trait Number: Copy + PartialOrd {
    const ZERO: Self;
    fn add(self, o: Self) -> Self;
    fn div_count(self, n: usize) -> Self;
}
macro_rules! number {
    ($($ty:ty),*) => {
        $(impl Number for $ty {
            const ZERO: Self = 0 as $ty;
            fn add(self, o: Self) -> Self {
                self + o
            }
            fn div_count(self, n: usize) -> Self {
                self / n as $ty
            }
        })*
    };
}
number!(i32, i64, u32, u64, f32, f64);
impl Number for Dec {
    const ZERO: Self = Dec::ZERO;
    fn add(self, o: Self) -> Self {
        self + o
    }
    fn div_count(self, n: usize) -> Self {
        self / Dec::from_u64(n as u64)
    }
}

pub fn sum<T: Number>(s: Vec<T>) -> T {
    s.into_iter().fold(T::ZERO, T::add)
}
pub fn sum_by<T, N: Number>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> N, c: &mut Combat) -> N {
    let mut total = N::ZERO;
    for x in s {
        total = total.add(f(x, c));
    }
    total
}
pub fn average<T: Number>(s: Vec<T>) -> T {
    assert!(!s.is_empty(), "Sequence contains no elements");
    let n = s.len();
    sum(s).div_count(n)
}
pub fn max<T: PartialOrd>(s: Vec<T>) -> T {
    let mut it = s.into_iter();
    let mut best = it.next().expect("Sequence contains no elements");
    for x in it {
        if x > best {
            best = x;
        }
    }
    best
}
pub fn min<T: PartialOrd>(s: Vec<T>) -> T {
    let mut it = s.into_iter();
    let mut best = it.next().expect("Sequence contains no elements");
    for x in it {
        if x < best {
            best = x;
        }
    }
    best
}
/// MaxBy: the first item with the greatest key; the default for an empty list of references.
pub fn max_by<T: Clone + Default, K: PartialOrd>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> K, c: &mut Combat) -> T {
    let mut best: Option<(T, K)> = None;
    for x in s {
        let k = f(x.clone(), c);
        if best.as_ref().is_none_or(|(_, bk)| k > *bk) {
            best = Some((x, k));
        }
    }
    best.map(|(x, _)| x).unwrap_or_default()
}
pub fn min_by<T: Clone + Default, K: PartialOrd>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> K, c: &mut Combat) -> T {
    let mut best: Option<(T, K)> = None;
    for x in s {
        let k = f(x.clone(), c);
        if best.as_ref().is_none_or(|(_, bk)| k < *bk) {
            best = Some((x, k));
        }
    }
    best.map(|(x, _)| x).unwrap_or_default()
}

pub fn take<T>(s: Vec<T>, n: i32) -> Vec<T> {
    s.into_iter().take(n.max(0) as usize).collect()
}
pub fn skip<T>(s: Vec<T>, n: i32) -> Vec<T> {
    s.into_iter().skip(n.max(0) as usize).collect()
}
pub fn take_last<T>(s: Vec<T>, n: i32) -> Vec<T> {
    let drop = s.len().saturating_sub(n.max(0) as usize);
    s.into_iter().skip(drop).collect()
}
pub fn skip_last<T>(s: Vec<T>, n: i32) -> Vec<T> {
    let keep = s.len().saturating_sub(n.max(0) as usize);
    s.into_iter().take(keep).collect()
}
pub fn take_while<T: Clone>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> Vec<T> {
    let mut out = Vec::new();
    for x in s {
        if !f(x.clone(), c) {
            break;
        }
        out.push(x);
    }
    out
}
pub fn skip_while<T: Clone>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> Vec<T> {
    let mut out = Vec::new();
    let mut skipping = true;
    for x in s {
        if skipping && f(x.clone(), c) {
            continue;
        }
        skipping = false;
        out.push(x);
    }
    out
}
pub fn reverse<T>(mut s: Vec<T>) -> Vec<T> {
    s.reverse();
    s
}
pub fn element_at<T>(s: Vec<T>, i: i32) -> T {
    s.into_iter().nth(i as usize).expect("index out of range")
}
pub fn element_at_or_default<T: Default>(s: Vec<T>, i: i32) -> T {
    if i < 0 {
        return T::default();
    }
    s.into_iter().nth(i as usize).unwrap_or_default()
}
/// OfType<T>: the objects of a class.
pub fn of_type(s: Vec<Obj>, kind: Kind, c: &mut Combat) -> Vec<Obj> {
    s.into_iter().filter(|x| super::is(*x, kind, c)).collect()
}
pub fn zip<A, B, U>(a: Vec<A>, b: Vec<B>, mut f: impl FnMut(A, B, &mut Combat) -> U, c: &mut Combat) -> Vec<U> {
    let mut out = Vec::new();
    for (x, y) in a.into_iter().zip(b) {
        out.push(f(x, y, c));
    }
    out
}
/// GroupBy: the groups in the order their keys first appear.
pub fn group_by<T: Clone, K: PartialEq>(s: Vec<T>, mut f: impl FnMut(T, &mut Combat) -> K, c: &mut Combat) -> Vec<Group<K, T>> {
    let mut out: Vec<Group<K, T>> = Vec::new();
    for x in s {
        let k = f(x.clone(), c);
        match out.iter_mut().find(|g| g.key == k) {
            Some(g) => g.items.push(x),
            None => out.push(Group { key: k, items: vec![x] }),
        }
    }
    out
}
pub fn aggregate<T, A>(s: Vec<T>, seed: A, mut f: impl FnMut(A, T, &mut Combat) -> A, c: &mut Combat) -> A {
    let mut acc = seed;
    for x in s {
        acc = f(acc, x, c);
    }
    acc
}
pub fn range(start: i32, count: i32) -> Vec<i32> {
    (start..start + count).collect()
}
pub fn repeat<T: Clone>(x: T, n: i32) -> Vec<T> {
    vec![x; n.max(0) as usize]
}
pub fn default_if_empty<T: Default>(s: Vec<T>) -> Vec<T> {
    if s.is_empty() { vec![T::default()] } else { s }
}
