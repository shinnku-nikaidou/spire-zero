//! The trainers' batches built from their samples' packed tokens (learn/common.py `pack`: zlib over JSON), for V
//! (learn/v/net.py) and the combat net (learn/combat/net.py): looked up in the tables and padded, as the Python did
//! element by element, to the same numbers bit for bit. Each array goes back as a CPU tensor over its own bytes.
//!
//! What follows Python's rules on purpose: a number stored in a float32 array is the JSON number as a double, then
//! rounded once (as torch stores a Python int or float); an id a table does not list is row 0, and so is an id that is
//! not a string (null, a number); a list stored in a row of a fixed size is that size, or one number spread over it
//! (torch's broadcasting); a negative position counts from the end. Anything else the Python would have refused is an
//! error.

use std::collections::HashMap;

use flate2::{Decompress, FlushDecompress, Status};
use pyo3::exceptions::PyValueError;
use pyo3::prelude::*;
use pyo3::types::{PyByteArray, PyBytes, PyDict};
use serde_json::Value;

type Table = HashMap<String, i64>;
type Index = HashMap<String, Table>;
type R<T> = Result<T, String>;

const V_NUMBERS: usize = 3; // a deck card's damage, block and growth (v/net.py N_NUMBERS)
const C_PILES: usize = 4; // combat/spec.py PILES
const C_NUMBERS: usize = 6; // combat/spec.py NUMBERS
const C_ENEMY_SCALARS: usize = 6; // combat/spec.py ENEMY_SCALARS
const C_ALLY_SCALARS: usize = 4;
const C_SLOTS: usize = 4; // combat/spec.py SLOTS
const C_BAGS: [(&str, &str); 3] = [
    ("relics", "relic_mask"),
    ("potions", "potion_mask"),
    ("context", "context_mask"),
];
const V_BAGS: [(&str, &str); 4] = [
    ("relics", "relic_mask"),
    ("potions", "potion_mask"),
    ("context", "context_mask"),
    ("map", "map_mask"),
];

enum Data {
    F32(Vec<f32>),
    I64(Vec<i64>),
    Bool(Vec<bool>),
}

struct Arr {
    shape: Vec<usize>,
    data: Data,
}

impl Arr {
    fn f32(shape: &[usize]) -> Arr {
        Arr {
            shape: shape.to_vec(),
            data: Data::F32(vec![0.0; shape.iter().product()]),
        }
    }

    fn i64(shape: &[usize]) -> Arr {
        Arr {
            shape: shape.to_vec(),
            data: Data::I64(vec![0; shape.iter().product()]),
        }
    }

    /// The flat position of AT, a prefix of the shape's positions (the rest 0).
    fn off(&self, at: &[usize]) -> usize {
        let mut o = 0;
        for (d, &n) in self.shape.iter().enumerate() {
            let i = at.get(d).copied().unwrap_or(0);
            debug_assert!(i < n);
            o = o * n + i;
        }
        o
    }

    fn set_f(&mut self, at: &[usize], x: f32) {
        let o = self.off(at);
        match &mut self.data {
            Data::F32(v) => v[o] = x,
            _ => unreachable!(),
        }
    }

    fn set_i(&mut self, at: &[usize], x: i64) {
        let o = self.off(at);
        match &mut self.data {
            Data::I64(v) => v[o] = x,
            _ => unreachable!(),
        }
    }

    /// The last dimension at AT (the other positions) set from XS: as many numbers, or one spread over it.
    fn row_f(&mut self, at: &[usize], xs: &[Value], what: &str) -> R<()> {
        let n = *self.shape.last().unwrap();
        if xs.len() != n && xs.len() != 1 {
            return Err(format!("{what}: {} numbers where {n} go", xs.len()));
        }
        let o = self.off(at);
        let vals = xs.iter().map(num).collect::<R<Vec<f32>>>()?;
        match &mut self.data {
            Data::F32(v) => {
                for k in 0..n {
                    v[o + k] = vals[if vals.len() == 1 { 0 } else { k }];
                }
            }
            _ => unreachable!(),
        }
        Ok(())
    }

    fn bytes(&self) -> (Vec<u8>, &'static str) {
        match &self.data {
            Data::F32(v) => (v.iter().flat_map(|x| x.to_ne_bytes()).collect(), "float32"),
            Data::I64(v) => (v.iter().flat_map(|x| x.to_ne_bytes()).collect(), "int64"),
            Data::Bool(v) => (v.iter().map(|&x| x as u8).collect(), "bool"),
        }
    }
}

/// A JSON number (or a bool, as Python's ints) as float32: as a double, rounded once.
fn num(v: &Value) -> R<f32> {
    match v {
        Value::Number(n) => n
            .as_f64()
            .map(|x| x as f32)
            .ok_or_else(|| format!("number {n} out of range")),
        Value::Bool(b) => Ok(if *b { 1.0 } else { 0.0 }),
        _ => Err(format!("{v} is no number")),
    }
}

fn int(v: &Value) -> R<i64> {
    match v {
        Value::Number(n) => n.as_i64().ok_or_else(|| format!("{n} is no integer")),
        Value::Bool(b) => Ok(*b as i64),
        _ => Err(format!("{v} is no integer")),
    }
}

/// A position as Python takes one: negative from the end.
fn pos(v: &Value, n: usize) -> R<usize> {
    let i = int(v)?;
    let j = if i < 0 { i + n as i64 } else { i };
    if j < 0 || j >= n as i64 {
        return Err(format!("position {i} of {n}"));
    }
    Ok(j as usize)
}

/// Python's truth of a value.
fn truthy(v: &Value) -> bool {
    match v {
        Value::Null => false,
        Value::Bool(b) => *b,
        Value::Number(n) => n.as_f64() != Some(0.0),
        Value::String(s) => !s.is_empty(),
        Value::Array(a) => !a.is_empty(),
        Value::Object(o) => !o.is_empty(),
    }
}

/// A table's row of an id (Python's `index[t].get(id, 0)`; the tables' ids are strings).
fn look(t: &Table, v: &Value) -> R<i64> {
    match v {
        Value::String(s) => Ok(t.get(s).copied().unwrap_or(0)),
        Value::Array(_) | Value::Object(_) => Err(format!("{v} as an id")),
        _ => Ok(0),
    }
}

fn table<'a>(index: &'a Index, name: &str) -> R<&'a Table> {
    index.get(name).ok_or_else(|| format!("no table {name}"))
}

fn arr<'a>(v: &'a Value, what: &str) -> R<&'a Vec<Value>> {
    v.as_array().ok_or_else(|| format!("{what} is no list"))
}

fn field<'a>(v: &'a Value, key: &str) -> R<&'a Value> {
    v.get(key).ok_or_else(|| format!("no {key}"))
}

fn list<'a>(v: &'a Value, key: &str) -> R<&'a Vec<Value>> {
    arr(field(v, key)?, key)
}

/// The I-th element of a list (Python's c[i]).
fn nth<'a>(v: &'a [Value], i: usize, what: &str) -> R<&'a Value> {
    v.get(i).ok_or_else(|| format!("{what} has no element {i}"))
}

/// Exactly N elements, as Python's unpacking of a list.
fn exact<'a>(v: &'a Value, n: usize, what: &str) -> R<&'a Vec<Value>> {
    let a = arr(v, what)?;
    if a.len() != n {
        return Err(format!("{what} has {} elements, not {n}", a.len()));
    }
    Ok(a)
}

fn width<T>(rows: &[&Vec<T>]) -> usize {
    rows.iter().map(|r| r.len()).max().unwrap_or(0).max(1)
}

/// A sample's tokens (zlib over JSON), with Z's state and BUF reused from sample to sample.
fn unpack(z: &mut Decompress, buf: &mut Vec<u8>, b: &[u8]) -> R<Value> {
    z.reset(true);
    buf.clear();
    loop {
        let (read, wrote) = (z.total_in(), z.total_out());
        buf.reserve(4 * b.len().max(256));
        let status = z
            .decompress_vec(&b[read as usize..], buf, FlushDecompress::Finish)
            .map_err(|e| format!("zlib: {e}"))?;
        if status == Status::StreamEnd {
            break;
        }
        if (z.total_in(), z.total_out()) == (read, wrote) {
            return Err("zlib: the stream ends early".to_string());
        }
    }
    serde_json::from_slice(buf).map_err(|e| format!("json: {e}"))
}

type Out = Vec<(String, Arr)>;

/// Lists of ids as one padded array (at least one wide) and its mask (net.py `padded`).
fn padded(out: &mut Out, names: (&str, &str), t: &Table, rows: &[&Vec<Value>]) -> R<usize> {
    let w = width(rows);
    let (mut ids, mut mask) = (Arr::i64(&[rows.len(), w]), Arr::f32(&[rows.len(), w]));
    for (b, r) in rows.iter().enumerate() {
        for (k, x) in r.iter().enumerate() {
            ids.set_i(&[b, k], look(t, x)?);
            mask.set_f(&[b, k], 1.0);
        }
    }
    out.push((names.0.to_string(), ids));
    out.push((names.1.to_string(), mask));
    Ok(w)
}

/// Lists of numbers as one padded array, at least one wide (v/net.py `numbers`).
fn numbers(out: &mut Out, name: &str, rows: &[&Vec<Value>]) -> R<()> {
    let mut a = Arr::f32(&[rows.len(), width(rows)]);
    for (b, r) in rows.iter().enumerate() {
        for (k, x) in r.iter().enumerate() {
            a.set_f(&[b, k], num(x)?);
        }
    }
    out.push((name.to_string(), a));
    Ok(())
}

/// Each relic's numbers and status, R_N wide (the relics' order).
fn relic_rows(
    out: &mut Out,
    prefix: &str,
    status_t: &Table,
    toks: &[(&Vec<Value>, &Vec<Value>)],
    r_n: usize,
) -> R<()> {
    let n = toks.len();
    let (mut values, mut status) = (Arr::f32(&[n, r_n, 3]), Arr::i64(&[n, r_n]));
    for (b, (vals, stats)) in toks.iter().enumerate() {
        if vals.len() != stats.len() {
            return Err(format!(
                "{} relic values and {} statuses",
                vals.len(),
                stats.len()
            ));
        }
        if vals.len() > r_n {
            return Err(format!("{} relic values for {r_n} relics", vals.len()));
        }
        for (k, (v, s)) in vals.iter().zip(stats.iter()).enumerate() {
            values.row_f(&[b, k], arr(v, "relic values")?, "relic values")?;
            status.set_i(&[b, k], look(status_t, s)?);
        }
    }
    out.push((format!("{prefix}relic_values"), values));
    out.push((format!("{prefix}relic_status"), status));
    Ok(())
}

/// Lists of deck cards ([id, up, modifiers, amounts, [damage, block, growth]]) as V's card inputs (v/net.py
/// `card_rows`), with PREFIX.
fn card_rows(out: &mut Out, index: &Index, prefix: &str, decks: &[&Vec<Value>]) -> R<()> {
    let (cards_t, mods_t) = (table(index, "cards")?, table(index, "modifiers")?);
    let n = decks.len();
    let w = width(decks);
    let mut m_n = 1;
    for d in decks {
        for c in d.iter() {
            m_n = m_n.max(arr(nth(arr(c, "card")?, 2, "card")?, "modifiers")?.len());
        }
    }
    let mut cards = Arr::i64(&[n, w]);
    let (mut ups, mut mask) = (Arr::f32(&[n, w]), Arr::f32(&[n, w]));
    let (mut mods, mut mod_mask, mut amounts) = (
        Arr::i64(&[n, w, m_n]),
        Arr::f32(&[n, w, m_n]),
        Arr::f32(&[n, w, m_n]),
    );
    let mut nums = Arr::f32(&[n, w, V_NUMBERS]);
    for (b, d) in decks.iter().enumerate() {
        for (k, c) in d.iter().enumerate() {
            let c = arr(c, "card")?;
            cards.set_i(&[b, k], look(cards_t, nth(c, 0, "card")?)?);
            ups.set_f(&[b, k], num(nth(c, 1, "card")?)?);
            mask.set_f(&[b, k], 1.0);
            for (j, m) in arr(nth(c, 2, "card")?, "modifiers")?.iter().enumerate() {
                mods.set_i(&[b, k, j], look(mods_t, m)?);
                mod_mask.set_f(&[b, k, j], 1.0);
            }
            let am = arr(nth(c, 3, "card")?, "amounts")?;
            if am.len() > m_n {
                return Err(format!("{} amounts for {m_n} modifiers", am.len()));
            }
            for (j, x) in am.iter().enumerate() {
                amounts.set_f(&[b, k, j], num(x)?);
            }
            let ns = arr(nth(c, 4, "card")?, "card numbers")?;
            if ns.len() != V_NUMBERS {
                return Err(format!("a card's {} numbers", ns.len()));
            }
            nums.row_f(&[b, k], ns, "card numbers")?;
        }
    }
    for (name, a) in [
        ("cards", cards),
        ("ups", ups),
        ("card_mask", mask),
        ("mods", mods),
        ("mod_mask", mod_mask),
        ("mod_amounts", amounts),
        ("numbers", nums),
    ] {
        out.push((format!("{prefix}{name}"), a));
    }
    Ok(())
}

const EMPTY: &Vec<Value> = &Vec::new();

/// A list of a pending option, empty for none.
fn pending_list<'a>(x: Option<&'a Value>, key: &str) -> R<&'a Vec<Value>> {
    match x {
        None => Ok(EMPTY),
        Some(v) => list(v, key),
    }
}

/// The event option each run has picked and not yet played out (v/net.py `pending_rows`), empty for none.
fn pending_rows(out: &mut Out, index: &Index, toks: &[Value]) -> R<()> {
    let p = "pend_";
    let pend: Vec<Option<&Value>> = toks.iter().map(|t| t.get("pending")).collect();
    let rows = |key: &str| {
        pend.iter()
            .map(|x| pending_list(*x, key))
            .collect::<R<Vec<_>>>()
    };
    card_rows(out, index, p, &rows("cards")?)?;
    let pending_t = table(index, "pending")?;
    let mut keys = Vec::with_capacity(pend.len());
    for x in &pend {
        keys.push(match x {
            None => Vec::new(),
            Some(v) => match field(v, "key")? {
                Value::Null => Vec::new(),
                k => vec![k.clone()],
            },
        });
    }
    padded(
        out,
        ("pending", "pending_mask"),
        pending_t,
        &keys.iter().collect::<Vec<_>>(),
    )?;
    numbers(out, &format!("{p}cards_owned"), &rows("cards_owned")?)?;
    let relics = rows("relics")?;
    let r_n = padded(
        out,
        ("pend_relics", "pend_relic_mask"),
        table(index, "relics")?,
        &relics,
    )?;
    let vals = rows("relic_values")?;
    let stats = rows("relic_status")?;
    let pairs: Vec<_> = vals.into_iter().zip(stats).collect();
    relic_rows(out, p, table(index, "relic_status")?, &pairs, r_n)?;
    numbers(out, &format!("{p}relics_owned"), &rows("relics_owned")?)?;
    padded(
        out,
        ("pend_potions", "pend_potion_mask"),
        table(index, "potions")?,
        &rows("potions")?,
    )?;
    numbers(out, &format!("{p}potions_owned"), &rows("potions_owned")?)?;
    padded(
        out,
        ("pend_vars", "pend_var_mask"),
        table(index, "event_vars")?,
        &rows("numbers")?,
    )?;
    numbers(out, &format!("{p}var_values"), &rows("number_values")?)?;
    Ok(())
}

fn v_build(index: &Index, toks: &[Value]) -> R<Out> {
    let mut out = Vec::new();
    let col = |key: &str| toks.iter().map(|t| list(t, key)).collect::<R<Vec<_>>>();
    card_rows(&mut out, index, "", &col("cards")?)?;
    for (t, mask) in V_BAGS {
        padded(&mut out, (t, mask), table(index, t)?, &col(t)?)?;
    }
    let r_n = width(&col("relics")?);
    let pairs: Vec<_> = col("relic_values")?
        .into_iter()
        .zip(col("relic_status")?)
        .collect();
    relic_rows(&mut out, "", table(index, "relic_status")?, &pairs, r_n)?;
    padded(
        &mut out,
        ("held", "held_mask"),
        table(index, "cards")?,
        &col("held")?,
    )?;
    pending_rows(&mut out, index, toks)?;
    Ok(out)
}

/// Powers ([id, amount, stacks, card or null, moved number]) as ids, amounts, mask, stacks, cards, card mask and
/// numbers, padded (combat/net.py `power_tensors`).
fn power_rows(index: &Index, rows: &[&Vec<Value>]) -> R<[Arr; 7]> {
    let (powers_t, cards_t) = (table(index, "powers")?, table(index, "cards")?);
    let (n, w) = (rows.len(), width(rows));
    let mut a = [
        Arr::i64(&[n, w]),
        Arr::f32(&[n, w]),
        Arr::f32(&[n, w]),
        Arr::f32(&[n, w]),
        Arr::i64(&[n, w]),
        Arr::f32(&[n, w]),
        Arr::f32(&[n, w]),
    ];
    for (b, r) in rows.iter().enumerate() {
        for (k, p) in r.iter().enumerate() {
            let p = exact(p, 5, "power")?;
            a[0].set_i(&[b, k], look(powers_t, &p[0])?);
            a[1].set_f(&[b, k], num(&p[1])?);
            a[2].set_f(&[b, k], 1.0);
            a[3].set_f(&[b, k], num(&p[2])?);
            a[6].set_f(&[b, k], num(&p[4])?);
            if truthy(&p[3]) {
                a[4].set_i(&[b, k], look(cards_t, &p[3])?);
                a[5].set_f(&[b, k], 1.0);
            }
        }
    }
    Ok(a)
}

/// Rows of numbers as torch.tensor reads a list of lists: float32 if any is a float, bool if all are bools, else
/// int64 (an empty one float32).
fn inferred(rows: &[&Vec<Value>]) -> R<Arr> {
    let n = rows.len();
    let l = rows[0].len();
    if rows.iter().any(|r| r.len() != l) {
        return Err("rows of different lengths".to_string());
    }
    let all: Vec<&Value> = rows.iter().flat_map(|r| r.iter()).collect();
    for v in &all {
        if !matches!(v, Value::Number(_) | Value::Bool(_)) {
            return Err(format!("{v} is no number"));
        }
    }
    let float = all
        .iter()
        .any(|v| matches!(v, Value::Number(x) if x.is_f64()));
    let shape = [n, l];
    if all.is_empty() || float {
        let vals = all.iter().map(|v| num(v)).collect::<R<Vec<f32>>>()?;
        return Ok(Arr {
            shape: shape.to_vec(),
            data: Data::F32(vals),
        });
    }
    if all.iter().all(|v| v.is_boolean()) {
        return Ok(Arr {
            shape: shape.to_vec(),
            data: Data::Bool(all.iter().map(|v| v.as_bool().unwrap()).collect()),
        });
    }
    let vals = all.iter().map(|v| int(v)).collect::<R<Vec<i64>>>()?;
    Ok(Arr {
        shape: shape.to_vec(),
        data: Data::I64(vals),
    })
}

fn combat_build(index: &Index, toks: &[Value]) -> R<Out> {
    let n = toks.len();
    let col = |key: &str| toks.iter().map(|t| list(t, key)).collect::<R<Vec<_>>>();
    let (cards_t, mods_t) = (table(index, "cards")?, table(index, "modifiers")?);

    let decks = col("cards")?;
    let w = width(&decks);
    let mut m_n = 1;
    for d in &decks {
        for c in d.iter() {
            m_n = m_n.max(arr(nth(arr(c, "card")?, 3, "card")?, "modifiers")?.len());
        }
    }
    let mut cards = Arr::i64(&[n, w]);
    let (mut ups, mut costs, mut damages) =
        (Arr::f32(&[n, w]), Arr::f32(&[n, w]), Arr::f32(&[n, w]));
    let mut piles = Arr::f32(&[n, w, C_PILES]);
    let (mut mods, mut mod_mask, mut mod_amounts) = (
        Arr::i64(&[n, w, m_n]),
        Arr::f32(&[n, w, m_n]),
        Arr::f32(&[n, w, m_n]),
    );
    let mut nums = Arr::f32(&[n, w, C_NUMBERS]);
    for (b, d) in decks.iter().enumerate() {
        for (k, c) in d.iter().enumerate() {
            let c = exact(c, 8, "card")?;
            cards.set_i(&[b, k], look(cards_t, &c[0])?);
            ups.set_f(&[b, k], num(&c[1])?);
            piles.set_f(&[b, k, pos(&c[2], C_PILES)?], 1.0);
            costs.set_f(&[b, k], num(&c[4])?);
            damages.set_f(&[b, k], num(&c[6])?);
            nums.row_f(&[b, k], arr(&c[7], "card numbers")?, "card numbers")?;
            for (j, m) in arr(&c[3], "modifiers")?.iter().enumerate() {
                mods.set_i(&[b, k, j], look(mods_t, m)?);
                mod_mask.set_f(&[b, k, j], 1.0);
            }
            let am = arr(&c[5], "amounts")?;
            if am.len() > m_n {
                return Err(format!("{} amounts for {m_n} modifiers", am.len()));
            }
            for (j, x) in am.iter().enumerate() {
                mod_amounts.set_f(&[b, k, j], num(x)?);
            }
        }
    }
    let [p_ids, p_amounts, p_mask, p_stacks, p_cards, p_card_mask, p_details] =
        power_rows(index, &col("powers")?)?;

    let (enemies_t, powers_t, intents_t) = (
        table(index, "enemies")?,
        table(index, "powers")?,
        table(index, "intents")?,
    );
    let foes = col("enemies")?;
    let e_n = width(&foes);
    let (mut q_n, mut i_n) = (1, 1);
    for f in &foes {
        for e in f.iter() {
            q_n = q_n.max(list(e, "powers")?.len());
            for s in list(e, "slots")? {
                i_n = i_n.max(arr(s, "slot")?.len());
            }
        }
    }
    let (mut enemies, mut enemy_mask) = (Arr::i64(&[n, e_n]), Arr::f32(&[n, e_n]));
    let mut enemy_scalars = Arr::f32(&[n, e_n, C_ENEMY_SCALARS]);
    let (mut ep_ids, mut ep_amounts, mut ep_mask) = (
        Arr::i64(&[n, e_n, q_n]),
        Arr::f32(&[n, e_n, q_n]),
        Arr::f32(&[n, e_n, q_n]),
    );
    let (mut ep_stacks, mut ep_cards, mut ep_card_mask) = (
        Arr::f32(&[n, e_n, q_n]),
        Arr::i64(&[n, e_n, q_n]),
        Arr::f32(&[n, e_n, q_n]),
    );
    let mut ep_details = Arr::f32(&[n, e_n, q_n]);
    let mut intents = Arr::i64(&[n, e_n, C_SLOTS, i_n]);
    let (mut intent_values, mut intent_mask) = (
        Arr::f32(&[n, e_n, C_SLOTS, i_n, 2]),
        Arr::f32(&[n, e_n, C_SLOTS, i_n]),
    );
    for (b, f) in foes.iter().enumerate() {
        for (k, e) in f.iter().enumerate() {
            enemies.set_i(&[b, k], look(enemies_t, field(e, "id")?)?);
            enemy_mask.set_f(&[b, k], 1.0);
            enemy_scalars.row_f(&[b, k], list(e, "scalars")?, "enemy scalars")?;
            for (j, p) in list(e, "powers")?.iter().enumerate() {
                let p = exact(p, 5, "power")?;
                ep_ids.set_i(&[b, k, j], look(powers_t, &p[0])?);
                ep_amounts.set_f(&[b, k, j], num(&p[1])?);
                ep_mask.set_f(&[b, k, j], 1.0);
                ep_stacks.set_f(&[b, k, j], num(&p[2])?);
                ep_details.set_f(&[b, k, j], num(&p[4])?);
                if truthy(&p[3]) {
                    ep_cards.set_i(&[b, k, j], look(cards_t, &p[3])?);
                    ep_card_mask.set_f(&[b, k, j], 1.0);
                }
            }
            for (s, slot) in list(e, "slots")?.iter().take(C_SLOTS).enumerate() {
                for (j, x) in arr(slot, "slot")?.iter().enumerate() {
                    let x = exact(x, 3, "intent")?;
                    intents.set_i(&[b, k, s, j], look(intents_t, &x[0])?);
                    intent_mask.set_f(&[b, k, s, j], 1.0);
                    intent_values.set_f(&[b, k, s, j, 0], num(&x[1])?);
                    intent_values.set_f(&[b, k, s, j, 1], num(&x[2])?);
                }
            }
        }
    }

    let mut bags = Vec::new();
    for (t, mask) in C_BAGS {
        padded(&mut bags, (t, mask), table(index, t)?, &col(t)?)?;
    }
    let mut orbs = Vec::new();
    let o_w = padded(
        &mut orbs,
        ("orbs", "orb_mask"),
        table(index, "orbs")?,
        &col("orbs")?,
    )?;
    let mut orb_values = Arr::f32(&[n, o_w, 2]);
    for (b, vals) in col("orb_values")?.iter().enumerate() {
        if vals.len() > o_w {
            return Err(format!("{} orb values for {o_w} orbs", vals.len()));
        }
        for (k, v) in vals.iter().enumerate() {
            orb_values.row_f(&[b, k], arr(v, "orb values")?, "orb values")?;
        }
    }

    let allies_t = table(index, "allies")?;
    let friends = col("allies")?;
    let a_n = width(&friends);
    let mut aq_n = 1;
    for f in &friends {
        for a in f.iter() {
            aq_n = aq_n.max(list(a, "powers")?.len());
        }
    }
    let (mut allies, mut ally_mask) = (Arr::i64(&[n, a_n]), Arr::f32(&[n, a_n]));
    let mut ally_scalars = Arr::f32(&[n, a_n, C_ALLY_SCALARS]);
    let (mut ap_ids, mut ap_amounts, mut ap_mask) = (
        Arr::i64(&[n, a_n, aq_n]),
        Arr::f32(&[n, a_n, aq_n]),
        Arr::f32(&[n, a_n, aq_n]),
    );
    for (b, f) in friends.iter().enumerate() {
        for (k, a) in f.iter().enumerate() {
            allies.set_i(&[b, k], look(allies_t, field(a, "id")?)?);
            ally_scalars.row_f(&[b, k], list(a, "scalars")?, "ally scalars")?;
            ally_mask.set_f(&[b, k], 1.0);
            for (j, p) in list(a, "powers")?.iter().enumerate() {
                let p = exact(p, 5, "power")?;
                ap_ids.set_i(&[b, k, j], look(powers_t, &p[0])?);
                ap_amounts.set_f(&[b, k, j], num(&p[1])?);
                ap_mask.set_f(&[b, k, j], 1.0);
            }
        }
    }

    let mut relics = Vec::new();
    let r_n = width(&col("relics")?);
    let pairs: Vec<_> = col("relic_values")?
        .into_iter()
        .zip(col("relic_status")?)
        .collect();
    relic_rows(&mut relics, "", table(index, "relic_status")?, &pairs, r_n)?;
    let player = inferred(&col("player")?)?;
    let extra = inferred(&col("extra")?)?;

    let mut out: Out = vec![
        ("cards".into(), cards),
        ("ups".into(), ups),
        ("piles".into(), piles),
        ("mods".into(), mods),
        ("mod_mask".into(), mod_mask),
        ("powers".into(), p_ids),
        ("power_amounts".into(), p_amounts),
        ("power_mask".into(), p_mask),
        ("player".into(), player),
        ("enemies".into(), enemies),
        ("enemy_scalars".into(), enemy_scalars),
        ("enemy_mask".into(), enemy_mask),
        ("enemy_powers".into(), ep_ids),
        ("enemy_power_amounts".into(), ep_amounts),
        ("enemy_power_mask".into(), ep_mask),
        ("intents".into(), intents),
        ("intent_values".into(), intent_values),
        ("intent_mask".into(), intent_mask),
    ];
    out.extend(bags);
    out.extend([
        ("power_stacks".into(), p_stacks),
        ("power_cards".into(), p_cards),
        ("power_card_mask".into(), p_card_mask),
        ("enemy_power_stacks".into(), ep_stacks),
        ("enemy_power_cards".into(), ep_cards),
        ("enemy_power_card_mask".into(), ep_card_mask),
    ]);
    out.extend(orbs);
    out.extend([
        ("allies".into(), allies),
        ("ally_scalars".into(), ally_scalars),
        ("ally_mask".into(), ally_mask),
        ("ally_powers".into(), ap_ids),
        ("ally_power_amounts".into(), ap_amounts),
        ("ally_power_mask".into(), ap_mask),
        ("extra".into(), extra),
        ("costs".into(), costs),
        ("mod_amounts".into(), mod_amounts),
    ]);
    out.extend(relics);
    out.extend([
        ("damages".into(), damages),
        ("orb_values".into(), orb_values),
        ("numbers".into(), nums),
        ("power_details".into(), p_details),
        ("enemy_power_details".into(), ep_details),
    ]);
    Ok(out)
}

fn run<'py>(
    py: Python<'py>,
    tokens: Vec<Bound<'py, PyBytes>>,
    index: Index,
    build: fn(&Index, &[Value]) -> R<Out>,
) -> PyResult<Bound<'py, PyDict>> {
    if tokens.is_empty() {
        return Err(PyValueError::new_err("no samples"));
    }
    let (mut z, mut buf) = (Decompress::new(true), Vec::new());
    let toks = tokens
        .iter()
        .enumerate()
        .map(|(i, b)| {
            unpack(&mut z, &mut buf, b.as_bytes()).map_err(|e| format!("sample {i}: {e}"))
        })
        .collect::<R<Vec<Value>>>()
        .map_err(PyValueError::new_err)?;
    let out = build(&index, &toks).map_err(PyValueError::new_err)?;
    let torch = py.import("torch")?;
    let frombuffer = torch.getattr("frombuffer")?;
    let d = PyDict::new(py);
    for (name, a) in out {
        let (bytes, dtype) = a.bytes();
        let kw = PyDict::new(py);
        kw.set_item("dtype", torch.getattr(dtype)?)?;
        let t = frombuffer
            .call((PyByteArray::new(py, &bytes),), Some(&kw))?
            .call_method1("reshape", (a.shape.clone(),))?;
        d.set_item(name, t)?;
    }
    Ok(d)
}

/// V's inputs (v/spec.py INPUTS, but `state`) of samples' packed tokens, {name: tensor}.
#[pyfunction]
fn v_inputs<'py>(
    py: Python<'py>,
    tokens: Vec<Bound<'py, PyBytes>>,
    index: Index,
) -> PyResult<Bound<'py, PyDict>> {
    run(py, tokens, index, v_build)
}

/// The combat net's inputs (combat/spec.py INPUTS) of samples' packed tokens, {name: tensor}.
#[pyfunction]
fn combat_inputs<'py>(
    py: Python<'py>,
    tokens: Vec<Bound<'py, PyBytes>>,
    index: Index,
) -> PyResult<Bound<'py, PyDict>> {
    run(py, tokens, index, combat_build)
}

#[pymodule]
fn sts2_batch(m: &Bound<'_, PyModule>) -> PyResult<()> {
    m.add_function(wrap_pyfunction!(v_inputs, m)?)?;
    m.add_function(wrap_pyfunction!(combat_inputs, m)?)?;
    Ok(())
}
