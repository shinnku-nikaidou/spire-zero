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

// .NET 9's Enumerable.Sum and Average, each as it adds: int and long checked (an overflow throws), float in double
// and narrowed once at the end, double in order, decimal as decimal adds. Average of int or long is a double.
const OVERFLOW: &str = "Arithmetic operation resulted in an overflow.";
const EMPTY: &str = "Sequence contains no elements";
pub fn sum_i32(s: Vec<i32>) -> i32 {
    s.into_iter().fold(0i32, |t, x| t.checked_add(x).expect(OVERFLOW))
}
pub fn sum_i64(s: Vec<i64>) -> i64 {
    s.into_iter().fold(0i64, |t, x| t.checked_add(x).expect(OVERFLOW))
}
pub fn sum_f32(s: Vec<f32>) -> f32 {
    s.into_iter().fold(0f64, |t, x| t + x as f64) as f32
}
pub fn sum_f64(s: Vec<f64>) -> f64 {
    s.into_iter().fold(0f64, |t, x| t + x)
}
pub fn sum_dec(s: Vec<Dec>) -> Dec {
    s.into_iter().fold(Dec::ZERO, |t, x| t + x)
}
pub fn average_i32(s: Vec<i32>) -> f64 {
    assert!(!s.is_empty(), "{EMPTY}");
    let n = s.len();
    s.into_iter().fold(0i64, |t, x| t.checked_add(x as i64).expect(OVERFLOW)) as f64 / n as f64
}
pub fn average_i64(s: Vec<i64>) -> f64 {
    assert!(!s.is_empty(), "{EMPTY}");
    let n = s.len();
    sum_i64(s) as f64 / n as f64
}
pub fn average_f32(s: Vec<f32>) -> f32 {
    assert!(!s.is_empty(), "{EMPTY}");
    let n = s.len();
    (s.into_iter().fold(0f64, |t, x| t + x as f64) / n as f64) as f32
}
pub fn average_f64(s: Vec<f64>) -> f64 {
    assert!(!s.is_empty(), "{EMPTY}");
    let n = s.len();
    sum_f64(s) / n as f64
}
pub fn average_dec(s: Vec<Dec>) -> Dec {
    assert!(!s.is_empty(), "{EMPTY}");
    let n = s.len();
    sum_dec(s) / Dec::from_u64(n as u64)
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
/// MaxBy and MinBy of a value type: .NET throws on no items.
pub fn max_by_value<T: Clone + Default, K: PartialOrd>(s: Vec<T>, f: impl FnMut(T, &mut Combat) -> K, c: &mut Combat) -> T {
    assert!(!s.is_empty(), "{EMPTY}");
    max_by(s, f, c)
}
pub fn min_by_value<T: Clone + Default, K: PartialOrd>(s: Vec<T>, f: impl FnMut(T, &mut Combat) -> K, c: &mut Combat) -> T {
    assert!(!s.is_empty(), "{EMPTY}");
    min_by(s, f, c)
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

#[cfg(test)]
mod number_tests {
    use super::*;

    /// .NET 9 adds floats in double and narrows once: 0.1f + 0.2f + 0.4f is 0x3f333333, where adding in float gives
    /// 0x3f333334.
    #[test]
    fn float_sum_adds_in_double() {
        assert_eq!(sum_f32(vec![0.1f32, 0.2, 0.4]).to_bits(), 0x3f333333);
        let in_double = (0.1f32 as f64 + 0.2f32 as f64 + 0.4f32 as f64) / 3.0;
        assert_eq!(average_f32(vec![0.1f32, 0.2, 0.4]).to_bits(), (in_double as f32).to_bits());
    }

    /// Average of ints is a double, not an integer division.
    #[test]
    fn int_average_is_a_double() {
        assert_eq!(average_i32(vec![1, 2]), 1.5);
    }

    /// An int sum that overflows throws, as .NET's checked one does.
    #[test]
    #[should_panic(expected = "overflow")]
    fn int_sum_overflow_throws() {
        sum_i32(vec![i32::MAX, 1]);
    }
}
