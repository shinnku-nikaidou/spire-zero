//! A stand-in for libsts2ai_engine with the C ABI of engine/DESIGN.md, for the tests of the C# side (EngineWorldTests),
//! which build it with rustc alone. Its combat is a list of the actions taken:
//!  - the import's bytes are the encounter's id, and "refuse" cannot be imported;
//!  - "play:9" fails (no such card); "play:7" wins, "potion:0" loses; each "end_turn" is a round;
//!  - each action costs 1 HP of 70, down to 0;
//!  - a combat called "LONG" is won by "play:7" from round 3 on only; before, the play is a step like any other;
//!  - a combat called "keyless" has no keys: both are 0, with an error, and a step from it fails;
//!  - every call on a world forgets the thread's last error first, as the engine does.
//! `sz_test_live` counts the worlds not yet freed, `sz_test_steps` and `sz_test_clones` the steps and clones made.

use std::cell::RefCell;
use std::sync::atomic::{AtomicI64, Ordering};

static LIVE: AtomicI64 = AtomicI64::new(0);
static STEPS: AtomicI64 = AtomicI64::new(0);
static CLONES: AtomicI64 = AtomicI64::new(0);

thread_local! {
    static ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

#[derive(Clone)]
struct World {
    encounter: String,
    steps: Vec<String>,
}

impl World {
    fn round(&self) -> usize {
        1 + self.steps.iter().filter(|s| *s == "end_turn").count()
    }

    /// Whether the combat is won: by "play:7", in "LONG" by one played in round 3 or later.
    fn won(&self) -> bool {
        let mut round = 1;
        for step in &self.steps {
            if step == "end_turn" {
                round += 1;
            } else if step == "play:7" && (self.encounter != "LONG" || round >= 3) {
                return true;
            }
        }
        false
    }

    fn status(&self) -> i32 {
        if self.won() {
            1
        } else if self.steps.iter().any(|s| s == "potion:0") {
            2
        } else {
            0
        }
    }

    fn hp(&self) -> usize {
        70usize.saturating_sub(self.steps.len())
    }
}

#[repr(C)]
pub struct Buf {
    ptr: *mut u8,
    len: usize,
}

fn give(text: String, out: *mut Buf) -> i32 {
    let bytes = text.into_bytes().into_boxed_slice();
    let len = bytes.len();
    let ptr = Box::into_raw(bytes) as *mut u8;
    unsafe { *out = Buf { ptr, len } };
    0
}

fn fail(message: &str) {
    ERROR.with(|e| *e.borrow_mut() = message.to_string());
}

/// A call on a world starts with no error said.
fn begin() {
    ERROR.with(|e| e.borrow_mut().clear());
}

/// A key has no value that says "failed": it is 0 with an error.
fn key(w: &World, key: u64) -> u64 {
    if w.encounter == "keyless" {
        fail("the stand-in has no key for a combat called keyless");
        return 0;
    }
    key
}

fn make(world: World) -> u64 {
    LIVE.fetch_add(1, Ordering::SeqCst);
    Box::into_raw(Box::new(world)) as u64
}

unsafe fn world<'a>(id: u64) -> &'a mut World {
    unsafe { &mut *(id as *mut World) }
}

unsafe fn text(bytes: *const u8, len: usize) -> String {
    String::from_utf8_lossy(unsafe { std::slice::from_raw_parts(bytes, len) }).into_owned()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_import(json: *const u8, len: usize) -> u64 {
    begin();
    let encounter = unsafe { text(json, len) };
    if encounter == "refuse" {
        fail("the stand-in takes no combat called refuse");
        return 0;
    }
    make(World { encounter, steps: vec![] })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_clone(id: u64) -> u64 {
    begin();
    CLONES.fetch_add(1, Ordering::SeqCst);
    make(unsafe { world(id) }.clone())
}

#[unsafe(no_mangle)]
pub extern "C" fn sz_test_clones() -> i64 {
    CLONES.load(Ordering::SeqCst)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_free(id: u64) {
    drop(unsafe { Box::from_raw(id as *mut World) });
    LIVE.fetch_sub(1, Ordering::SeqCst);
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_apply(id: u64, action: *const u8, len: usize) -> i32 {
    begin();
    let action = unsafe { text(action, len) };
    if action == "play:9" {
        fail("no card 9 in hand");
        return 1;
    }
    unsafe { world(id) }.steps.push(action);
    0
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_status(id: u64) -> i32 {
    begin();
    unsafe { world(id) }.status()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_legal(id: u64, out: *mut Buf) -> i32 {
    begin();
    let over = unsafe { world(id) }.status() != 0;
    give(if over { String::new() } else { "play:7\npotion:0:2\nend_turn\n".to_string() }, out)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_observe(id: u64, out: *mut Buf) -> i32 {
    begin();
    let w = unsafe { world(id) };
    give(
        format!(
            concat!(
                r#"{{"encounter":"{}","act":0,"ascension":0,"round":{},"player":{{"uid":1,"character":"SILENT","hp":{},"max_hp":70,"#,
                r#""block":0,"energy":3,"max_energy":3,"stars":0,"gold":99,"powers":[],"orb_slots":0,"orbs":[]}},"#,
                r#""hand":[{{"id":"STRIKE_SILENT","uid":7}}],"draw":[],"#,
                r#""discard":[],"exhaust":[],"potions":["FIRE_POTION"],"relics":[],"allies":[],"enemies":[]}}"#
            ),
            w.encounter,
            w.round(),
            w.hp()
        ),
        out,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_run(id: u64, out: *mut Buf) -> i32 {
    begin();
    let w = unsafe { world(id) };
    give(
        format!(
            r#"{{"hp":{},"max_hp":70,"gold":99,"deck":[{{"id":"STRIKE_SILENT"}}],"relics":[{{"id":"RING_OF_THE_SNAKE"}}],"potions":[null]}}"#,
            w.hp()
        ),
        out,
    )
}

fn hidden_key(w: &World) -> u64 {
    key(w, w.steps.len() as u64)
}

/// What the observation shows: the encounter, the round and the HP.
fn state_key(w: &World) -> u64 {
    let name = w.encounter.bytes().fold(0u64, |sum, b| sum.wrapping_mul(31).wrapping_add(b as u64));
    key(w, name.wrapping_mul(1_000_003) ^ (w.round() * 1000 + w.hp()) as u64)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_hidden_key(id: u64) -> u64 {
    begin();
    hidden_key(unsafe { world(id) })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_state_key(id: u64) -> u64 {
    begin();
    state_key(unsafe { world(id) })
}

#[repr(C)]
pub struct Node {
    status: i32,
    round: i32,
    prompt: i32,
    state_key: u64,
    hidden_key: u64,
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_step(id: u64, action: *const u8, len: usize, node: *mut Node) -> u64 {
    begin();
    let action = unsafe { text(action, len) };
    if action == "play:9" {
        fail("no card 9 in hand");
        return 0;
    }
    let mut after = unsafe { world(id) }.clone();
    after.steps.push(action);
    let status = after.status();
    let (state, hidden) = if status == 0 { (state_key(&after), hidden_key(&after)) } else { (0, 0) };
    if after.encounter == "keyless" {
        return 0;
    }
    unsafe { *node = Node { status, round: after.round() as i32, prompt: 0, state_key: state, hidden_key: hidden } };
    STEPS.fetch_add(1, Ordering::SeqCst);
    make(after)
}

#[unsafe(no_mangle)]
pub extern "C" fn sz_test_steps() -> i64 {
    STEPS.load(Ordering::SeqCst)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_buf_free(buf: Buf) {
    drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(buf.ptr, buf.len)) });
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_last_error(out: *mut Buf) -> i32 {
    give(ERROR.with(|e| e.borrow().clone()), out)
}

#[unsafe(no_mangle)]
pub extern "C" fn sz_test_live() -> i64 {
    LIVE.load(Ordering::SeqCst)
}
