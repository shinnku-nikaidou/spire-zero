//! .NET's collections, with the order .NET gives them. HashSet and Dictionary enumerate their slots in order; a removed
//! slot is reused by the next add, most recently freed first. That is reproduced here, because game code walks them.

use std::collections::VecDeque;
use std::sync::Arc;

/// Anything a foreach can walk, as the list of its items in .NET's order.
pub trait IntoVec<T> {
    fn into_vec(self) -> Vec<T>;
}

impl<T> IntoVec<T> for Vec<T> {
    fn into_vec(self) -> Vec<T> {
        self
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct KeyValuePair<K, V> {
    pub key: K,
    pub value: V,
}

/// The slots of a HashSet or Dictionary. A copy shares them with the collection it was copied from, and the one of
/// the two that is written to first takes slots of its own: the game hands its collections around by reference and
/// the translated code by value, and most of those values are only read. One with nothing in it holds nothing.
#[derive(Clone, Debug)]
struct Slots<T>(Option<Arc<Held<T>>>);

#[derive(Clone, Debug)]
struct Held<T> {
    slots: Vec<Option<T>>,
    /// Freed slots; the last one is reused first.
    free: Vec<usize>,
}

impl<T> Default for Slots<T> {
    fn default() -> Self {
        Slots(None)
    }
}

impl<T> Slots<T> {
    fn of(slots: Vec<Option<T>>, free: Vec<usize>) -> Self {
        Slots(Some(Arc::new(Held { slots, free })))
    }
    /// Every slot in order, the freed ones empty.
    fn slots(&self) -> &[Option<T>] {
        match &self.0 {
            Some(held) => &held.slots,
            None => &[],
        }
    }
    fn len(&self) -> usize {
        match &self.0 {
            Some(held) => held.slots.len() - held.free.len(),
            None => 0,
        }
    }
    fn clear(&mut self) {
        self.0 = None;
    }
    fn iter(&self) -> impl Iterator<Item = &T> {
        self.slots().iter().flatten()
    }
}

impl<T: Clone> Slots<T> {
    /// The slots to write to: this collection's own.
    fn held(&mut self) -> &mut Held<T> {
        Arc::make_mut(self.0.get_or_insert_with(|| {
            Arc::new(Held {
                slots: Vec::new(),
                free: Vec::new(),
            })
        }))
    }
    fn put(&mut self, v: T) {
        let held = self.held();
        match held.free.pop() {
            Some(i) => held.slots[i] = Some(v),
            None => held.slots.push(Some(v)),
        }
    }
    fn release(&mut self, i: usize) -> T {
        let held = self.held();
        held.free.push(i);
        held.slots[i].take().unwrap()
    }
    /// What the slots hold, in order.
    fn into_items(self) -> Vec<T> {
        match self.0.map(Arc::try_unwrap) {
            None => Vec::new(),
            Some(Ok(held)) => held.slots.into_iter().flatten().collect(),
            Some(Err(shared)) => shared.slots.iter().flatten().cloned().collect(),
        }
    }
}

/// HashSet<T>.
#[derive(Clone, Debug)]
pub struct Set<T>(Slots<T>);

impl<T> Slots<T> {
    /// The slots of a collection of the game as it stands: each item with the slot it is in, in enumeration order,
    /// and the freed slots in the order they will be filled again. Every slot is in exactly one of the two.
    fn from_layout(items: Vec<T>, at: Vec<usize>, free: Vec<usize>) -> Result<Self, String> {
        if items.len() != at.len() {
            return Err(format!("{} items at {} slots", items.len(), at.len()));
        }
        let count = items.len() + free.len();
        let mut slots: Vec<Option<T>> = Vec::new();
        slots.resize_with(count, || None);
        let mut last = None;
        for (item, i) in items.into_iter().zip(at) {
            if i >= count || last.is_some_and(|l| i <= l) {
                return Err(format!("an item at slot {i}: slots go up, below {count}"));
            }
            last = Some(i);
            slots[i] = Some(item);
        }
        let mut seen = vec![false; count];
        for &i in &free {
            if i >= count || slots[i].is_some() || seen[i] {
                return Err(format!("a freed slot {i} that is not free, or twice"));
            }
            seen[i] = true;
        }
        // The last of `free` here is the one filled first.
        Ok(Slots::of(slots, free.into_iter().rev().collect()))
    }
}

impl<T> Set<T> {
    /// The set that enumerates these items in this order, with no freed slot.
    pub fn from_items(items: Vec<T>) -> Self {
        Set(Slots::of(items.into_iter().map(Some).collect(), Vec::new()))
    }
    /// The set with these items in these slots and these freed slots (the next one to be filled first).
    pub fn from_layout(items: Vec<T>, at: Vec<usize>, free: Vec<usize>) -> Result<Self, String> {
        Ok(Set(Slots::from_layout(items, at, free)?))
    }
}

impl<T> Default for Set<T> {
    fn default() -> Self {
        Set(Slots::default())
    }
}

impl<T: PartialEq + Clone> Set<T> {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn from_vec(items: Vec<T>) -> Self {
        let mut s = Self::default();
        for x in items {
            s.add(x);
        }
        s
    }
    fn find(&self, x: &T) -> Option<usize> {
        self.0.slots().iter().position(|s| s.as_ref() == Some(x))
    }
    pub fn add(&mut self, x: T) -> bool {
        if self.find(&x).is_some() {
            return false;
        }
        self.0.put(x);
        true
    }
    pub fn contains(&self, x: &T) -> bool {
        self.find(x).is_some()
    }
    pub fn remove(&mut self, x: &T) -> bool {
        match self.find(x) {
            Some(i) => {
                self.0.release(i);
                true
            }
            None => false,
        }
    }
    pub fn len(&self) -> usize {
        self.0.len()
    }
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
    pub fn clear(&mut self) {
        self.0.clear()
    }
    pub fn union_with(&mut self, other: Vec<T>) {
        for x in other {
            self.add(x);
        }
    }
    pub fn except_with(&mut self, other: Vec<T>) {
        for x in &other {
            self.remove(x);
        }
    }
    pub fn intersect_with(&mut self, other: Vec<T>) {
        for i in 0..self.0.slots().len() {
            if self.0.slots()[i].as_ref().is_some_and(|x| !other.contains(x)) {
                self.0.release(i);
            }
        }
    }
    pub fn set_equals(&self, other: &Vec<T>) -> bool {
        other.iter().all(|x| self.contains(x)) && self.0.iter().all(|x| other.contains(x))
    }
    pub fn overlaps(&self, other: &Vec<T>) -> bool {
        other.iter().any(|x| self.contains(x))
    }
}

impl<T: Clone> IntoVec<T> for Set<T> {
    fn into_vec(self) -> Vec<T> {
        self.0.into_items()
    }
}

impl<T: PartialEq> PartialEq for Set<T> {
    fn eq(&self, other: &Self) -> bool {
        self.0.slots() == other.0.slots()
    }
}

/// Dictionary<K, V>.
#[derive(Clone, Debug)]
pub struct Dict<K, V>(Slots<(K, V)>);

impl<K, V> Dict<K, V> {
    /// The dictionary that enumerates these pairs in this order, with no freed slot.
    pub fn from_pairs(pairs: Vec<(K, V)>) -> Self {
        Dict(Slots::of(pairs.into_iter().map(Some).collect(), Vec::new()))
    }
    /// The dictionary with these pairs in these slots and these freed slots (the next one to be filled first).
    pub fn from_layout(
        pairs: Vec<(K, V)>,
        at: Vec<usize>,
        free: Vec<usize>,
    ) -> Result<Self, String> {
        Ok(Dict(Slots::from_layout(pairs, at, free)?))
    }
}

impl<K, V> Default for Dict<K, V> {
    fn default() -> Self {
        Dict(Slots::default())
    }
}

impl<K: PartialEq + Clone, V: Clone> Dict<K, V> {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn from_vec(items: Vec<KeyValuePair<K, V>>) -> Self {
        let mut d = Self::default();
        for kv in items {
            d.add(kv.key, kv.value);
        }
        d
    }
    fn find(&self, k: &K) -> Option<usize> {
        self.0
            .slots()
            .iter()
            .position(|s| s.as_ref().is_some_and(|(key, _)| key == k))
    }
    /// Add: the key must be new.
    pub fn add(&mut self, k: K, v: V) {
        assert!(
            self.find(&k).is_none(),
            "an item with the same key has already been added"
        );
        self.0.put((k, v));
    }
    pub fn try_add(&mut self, k: K, v: V) -> bool {
        if self.find(&k).is_some() {
            return false;
        }
        self.0.put((k, v));
        true
    }
    /// dict[k] = v.
    pub fn set(&mut self, k: K, v: V) {
        match self.find(&k) {
            Some(i) => self.0.held().slots[i].as_mut().unwrap().1 = v,
            None => self.0.put((k, v)),
        }
    }
    /// dict[k]: the key must be there.
    pub fn get(&self, k: &K) -> V {
        self.try_get(k)
            .expect("the given key was not present in the dictionary")
    }
    /// The value of a key, to change in place (`dict[k].Add(x)`). Panics where C# throws KeyNotFoundException.
    pub fn get_mut(&mut self, k: &K) -> &mut V {
        self.0
            .held()
            .slots
            .iter_mut()
            .flatten()
            .find(|(key, _)| key == k)
            .map(|(_, v)| v)
            .expect("the key is not in the dictionary")
    }
    pub fn try_get(&self, k: &K) -> Option<V> {
        self.find(k)
            .map(|i| self.0.slots()[i].as_ref().unwrap().1.clone())
    }
    pub fn contains_key(&self, k: &K) -> bool {
        self.find(k).is_some()
    }
    pub fn remove(&mut self, k: &K) -> bool {
        self.take(k).is_some()
    }
    /// Remove(key, out value).
    pub fn take(&mut self, k: &K) -> Option<V> {
        self.find(k).map(|i| self.0.release(i).1)
    }
    pub fn keys(&self) -> Vec<K> {
        self.0.iter().map(|(k, _)| k.clone()).collect()
    }
    pub fn values(&self) -> Vec<V> {
        self.0.iter().map(|(_, v)| v.clone()).collect()
    }
    pub fn len(&self) -> usize {
        self.0.len()
    }
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
    pub fn clear(&mut self) {
        self.0.clear()
    }
}

impl<K: PartialEq + Clone, V: Clone + PartialEq> Dict<K, V> {
    pub fn contains_value(&self, v: &V) -> bool {
        self.0.iter().any(|(_, x)| x == v)
    }
}

impl<K: Clone, V: Clone> IntoVec<KeyValuePair<K, V>> for Dict<K, V> {
    fn into_vec(self) -> Vec<KeyValuePair<K, V>> {
        self.0
            .into_items()
            .into_iter()
            .map(|(key, value)| KeyValuePair { key, value })
            .collect()
    }
}

impl<K: PartialEq, V: PartialEq> PartialEq for Dict<K, V> {
    fn eq(&self, other: &Self) -> bool {
        self.0.slots() == other.0.slots()
    }
}

/// Queue<T>.
#[derive(Clone, Debug, PartialEq)]
pub struct Queue<T>(VecDeque<T>);

impl<T> Default for Queue<T> {
    fn default() -> Self {
        Queue(VecDeque::new())
    }
}

impl<T> Queue<T> {
    /// The queue that gives these items in this order.
    pub fn from_items(items: Vec<T>) -> Self {
        Queue(items.into())
    }
}

impl<T: Clone> Queue<T> {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn from_vec(items: Vec<T>) -> Self {
        Queue(items.into())
    }
    pub fn enqueue(&mut self, x: T) {
        self.0.push_back(x)
    }
    pub fn dequeue(&mut self) -> T {
        self.0.pop_front().expect("queue empty")
    }
    pub fn try_dequeue(&mut self) -> Option<T> {
        self.0.pop_front()
    }
    pub fn peek(&self) -> T {
        self.0.front().expect("queue empty").clone()
    }
    pub fn try_peek(&self) -> Option<T> {
        self.0.front().cloned()
    }
    pub fn len(&self) -> usize {
        self.0.len()
    }
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
    pub fn clear(&mut self) {
        self.0.clear()
    }
}

impl<T: Clone + PartialEq> Queue<T> {
    pub fn contains(&self, x: &T) -> bool {
        self.0.contains(x)
    }
}

impl<T> IntoVec<T> for Queue<T> {
    fn into_vec(self) -> Vec<T> {
        self.0.into()
    }
}

/// Stack<T>. A foreach walks it from the top.
#[derive(Clone, Debug, PartialEq)]
pub struct Stack<T>(Vec<T>);

impl<T> Default for Stack<T> {
    fn default() -> Self {
        Stack(Vec::new())
    }
}

impl<T> Stack<T> {
    /// The stack of these items, the last one on top.
    pub fn from_bottom(items: Vec<T>) -> Self {
        Stack(items)
    }
}

impl<T: Clone> Stack<T> {
    pub fn new() -> Self {
        Self::default()
    }
    /// new Stack<T>(items): pushed in order, so the last item is on top.
    pub fn from_vec(items: Vec<T>) -> Self {
        Stack(items)
    }
    pub fn push(&mut self, x: T) {
        self.0.push(x)
    }
    pub fn pop(&mut self) -> T {
        self.0.pop().expect("stack empty")
    }
    pub fn try_pop(&mut self) -> Option<T> {
        self.0.pop()
    }
    pub fn peek(&self) -> T {
        self.0.last().expect("stack empty").clone()
    }
    pub fn try_peek(&self) -> Option<T> {
        self.0.last().cloned()
    }
    pub fn len(&self) -> usize {
        self.0.len()
    }
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
    pub fn clear(&mut self) {
        self.0.clear()
    }
}

impl<T: Clone + PartialEq> Stack<T> {
    pub fn contains(&self, x: &T) -> bool {
        self.0.contains(x)
    }
}

impl<T> IntoVec<T> for Stack<T> {
    fn into_vec(mut self) -> Vec<T> {
        self.0.reverse();
        self.0
    }
}

/// One group of GroupBy.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Group<K, T> {
    pub key: K,
    pub items: Vec<T>,
}

impl<K, T> IntoVec<T> for Group<K, T> {
    fn into_vec(self) -> Vec<T> {
        self.items
    }
}

/// IOrderedEnumerable<T>: the items in order, and which of them compared equal on every key so far (ThenBy orders only
/// within those). The sort is stable, as .NET's.
#[derive(Clone, Debug)]
pub struct Ordered<T> {
    pub items: Vec<T>,
    /// One number per item, never decreasing: items with the same number are tied.
    pub rank: Vec<u32>,
}

impl<T> IntoVec<T> for Ordered<T> {
    fn into_vec(self) -> Vec<T> {
        self.items
    }
}

#[cfg(test)]
mod layout_tests {
    use super::*;

    /// A set read with freed slots fills them first, the next one first, as .NET's does.
    #[test]
    fn freed_slots_are_filled_in_their_order() {
        // {1, 2, 3} after Remove(1) and Remove(2): slot 2 holds 3; the free list is 1, then 0.
        let mut set = Set::from_layout(vec![3], vec![2], vec![1, 0]).unwrap();
        set.add(7);
        set.add(8);
        set.add(9);
        assert_eq!(set.into_vec(), vec![8, 7, 3, 9]);
        let mut dict = Dict::from_layout(Vec::<(i32, i32)>::new(), vec![], vec![1, 0]).unwrap();
        dict.set(1, 10);
        dict.set(2, 20);
        assert_eq!(dict.keys(), vec![2, 1]);
    }

    #[test]
    fn a_layout_that_breaks_the_rule_is_refused() {
        assert!(Set::from_layout(vec![1], vec![0], vec![0]).is_err());
        assert!(Set::from_layout(vec![1, 2], vec![1, 0], vec![]).is_err());
        assert!(Set::from_layout(vec![1], vec![2], vec![0]).is_err());
    }
}
