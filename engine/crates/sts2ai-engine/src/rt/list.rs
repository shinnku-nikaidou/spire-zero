//! List<T>'s methods that are more than one Vec call, with .NET's behaviour (List.Sort is .NET's introsort: items that
//! compare equal end up where .NET puts them).

use super::{Cmp, Combat};

pub fn index_of<T: PartialEq>(l: &[T], x: &T) -> i32 {
    l.iter().position(|y| y == x).map_or(-1, |i| i as i32)
}
pub fn remove<T: PartialEq>(l: &mut Vec<T>, x: &T) -> bool {
    match l.iter().position(|y| y == x) {
        Some(i) => {
            l.remove(i);
            true
        }
        None => false,
    }
}
pub fn remove_at<T>(l: &mut Vec<T>, i: i32) {
    l.remove(i as usize);
}
pub fn remove_range<T>(l: &mut Vec<T>, start: i32, count: i32) {
    l.drain(start as usize..(start + count) as usize);
}
pub fn insert_range<T>(l: &mut Vec<T>, at: i32, items: Vec<T>) {
    l.splice(at as usize..at as usize, items);
}
pub fn get_range<T: Clone>(l: &[T], start: i32, count: i32) -> Vec<T> {
    l[start as usize..(start + count) as usize].to_vec()
}
/// CollectionsMarshal.SetCount: the list gets this length; new slots hold the default.
pub fn set_count<T: Default + Clone>(l: &mut Vec<T>, n: i32) {
    l.resize(n as usize, T::default());
}
pub fn remove_all<T: Clone>(l: &mut Vec<T>, mut f: impl FnMut(T, &mut Combat) -> bool, c: &mut Combat) -> i32 {
    let before = l.len();
    let mut kept = Vec::with_capacity(before);
    for x in l.drain(..) {
        if !f(x.clone(), c) {
            kept.push(x);
        }
    }
    *l = kept;
    (before - l.len()) as i32
}
pub fn for_each<T>(l: Vec<T>, mut f: impl FnMut(T, &mut Combat), c: &mut Combat) {
    for x in l {
        f(x, c);
    }
}
/// List.Sort(): by the items' own order (IComparable).
pub fn sort<T: Cmp + Clone>(l: &mut [T], c: &mut Combat) {
    intro_sort(l, &mut |a: &T, b: &T, c: &mut Combat| a.cmp_net(b, c), c);
}
/// List.Sort(comparison).
pub fn sort_by<T: Clone>(l: &mut [T], mut f: impl FnMut(T, T, &mut Combat) -> i32, c: &mut Combat) {
    intro_sort(l, &mut |a: &T, b: &T, c: &mut Combat| f(a.clone(), b.clone(), c), c);
}

// .NET's ArraySortHelper<T>.IntroSort (dotnet/runtime, MIT), step for step, so that equal items land where .NET puts them.

fn swap_if_greater<T>(keys: &mut [T], cmp: &mut dyn FnMut(&T, &T, &mut Combat) -> i32, c: &mut Combat, i: usize, j: usize) {
    if cmp(&keys[i], &keys[j], c) > 0 {
        keys.swap(i, j);
    }
}

fn intro_sort<T: Clone>(keys: &mut [T], cmp: &mut dyn FnMut(&T, &T, &mut Combat) -> i32, c: &mut Combat) {
    if keys.len() > 1 {
        let depth = 2 * (keys.len().ilog2() as i32 + 1);
        intro_sort_inner(keys, depth, cmp, c);
    }
}

fn intro_sort_inner<T: Clone>(keys: &mut [T], mut depth: i32, cmp: &mut dyn FnMut(&T, &T, &mut Combat) -> i32, c: &mut Combat) {
    let mut size = keys.len();
    while size > 1 {
        if size <= 16 {
            if size == 2 {
                swap_if_greater(keys, cmp, c, 0, 1);
                return;
            }
            if size == 3 {
                swap_if_greater(keys, cmp, c, 0, 1);
                swap_if_greater(keys, cmp, c, 0, 2);
                swap_if_greater(keys, cmp, c, 1, 2);
                return;
            }
            insertion_sort(&mut keys[..size], cmp, c);
            return;
        }
        if depth == 0 {
            heap_sort(&mut keys[..size], cmp, c);
            return;
        }
        depth -= 1;
        let p = pick_pivot_and_partition(&mut keys[..size], cmp, c);
        intro_sort_inner(&mut keys[p + 1..size], depth, cmp, c);
        size = p;
    }
}

fn pick_pivot_and_partition<T: Clone>(keys: &mut [T], cmp: &mut dyn FnMut(&T, &T, &mut Combat) -> i32, c: &mut Combat) -> usize {
    let hi = keys.len() - 1;
    let middle = hi >> 1;
    swap_if_greater(keys, cmp, c, 0, middle);
    swap_if_greater(keys, cmp, c, 0, hi);
    swap_if_greater(keys, cmp, c, middle, hi);
    let pivot = keys[middle].clone();
    keys.swap(middle, hi - 1);
    let (mut left, mut right) = (0usize, hi - 1);
    while left < right {
        loop {
            left += 1;
            if cmp(&keys[left], &pivot, c) >= 0 {
                break;
            }
        }
        loop {
            right -= 1;
            if cmp(&pivot, &keys[right], c) >= 0 {
                break;
            }
        }
        if left >= right {
            break;
        }
        keys.swap(left, right);
    }
    if left != hi - 1 {
        keys.swap(left, hi - 1);
    }
    left
}

fn heap_sort<T: Clone>(keys: &mut [T], cmp: &mut dyn FnMut(&T, &T, &mut Combat) -> i32, c: &mut Combat) {
    let n = keys.len();
    for i in (1..=n >> 1).rev() {
        down_heap(keys, i, n, cmp, c);
    }
    for i in (2..=n).rev() {
        keys.swap(0, i - 1);
        down_heap(keys, 1, i - 1, cmp, c);
    }
}

fn down_heap<T: Clone>(keys: &mut [T], mut i: usize, n: usize, cmp: &mut dyn FnMut(&T, &T, &mut Combat) -> i32, c: &mut Combat) {
    let d = keys[i - 1].clone();
    while i <= n >> 1 {
        let mut child = 2 * i;
        if child < n && cmp(&keys[child - 1], &keys[child], c) < 0 {
            child += 1;
        }
        if cmp(&d, &keys[child - 1], c) >= 0 {
            break;
        }
        keys[i - 1] = keys[child - 1].clone();
        i = child;
    }
    keys[i - 1] = d;
}

fn insertion_sort<T: Clone>(keys: &mut [T], cmp: &mut dyn FnMut(&T, &T, &mut Combat) -> i32, c: &mut Combat) {
    for i in 0..keys.len() - 1 {
        let t = keys[i + 1].clone();
        let mut j = i as isize;
        while j >= 0 && cmp(&t, &keys[j as usize], c) < 0 {
            keys[j as usize + 1] = keys[j as usize].clone();
            j -= 1;
        }
        keys[(j + 1) as usize] = t;
    }
}
