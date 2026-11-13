//! A stand-in for libsts2ai_engine with the C ABI of engine/DESIGN.md, for the tests of the C# side (EngineWorldTests),
//! which build it with rustc alone. Its combat is a list of the actions taken:
//!  - the import's bytes are the encounter's id, and "refuse" cannot be imported;
//!  - "play:9" fails (no such card); "play:7" wins, "potion:0" loses; each "end_turn" is a round;
//!  - each action costs 1 HP of 70, down to 0.
//! `sz_test_live` counts the worlds not yet freed.

use std::cell::RefCell;
use std::sync::atomic::{AtomicI64, Ordering};

static LIVE: AtomicI64 = AtomicI64::new(0);

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

    fn status(&self) -> i32 {
        if self.steps.iter().any(|s| s == "play:7") {
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
    let encounter = unsafe { text(json, len) };
    if encounter == "refuse" {
        fail("the stand-in takes no combat called refuse");
        return 0;
    }
    make(World { encounter, steps: vec![] })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_clone(id: u64) -> u64 {
    make(unsafe { world(id) }.clone())
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_free(id: u64) {
    drop(unsafe { Box::from_raw(id as *mut World) });
    LIVE.fetch_sub(1, Ordering::SeqCst);
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_apply(id: u64, action: *const u8, len: usize) -> i32 {
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
    unsafe { world(id) }.status()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_legal(id: u64, out: *mut Buf) -> i32 {
    let over = unsafe { world(id) }.status() != 0;
    give(if over { String::new() } else { "play:7\npotion:0:2\nend_turn\n".to_string() }, out)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_observe(id: u64, out: *mut Buf) -> i32 {
    let w = unsafe { world(id) };
    give(
        format!(
            concat!(
                r#"{{"encounter":"{}","act":0,"ascension":0,"round":{},"player":{{"uid":1,"character":"SILENT","hp":{},"max_hp":70,"#,
                r#""block":0,"energy":3,"max_energy":3,"stars":0,"gold":99,"powers":[],"orb_slots":0,"orbs":[]}},"hand":[],"draw":[],"#,
                r#""discard":[],"exhaust":[],"potions":[null],"relics":[],"allies":[],"enemies":[]}}"#
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
    let w = unsafe { world(id) };
    give(
        format!(
            r#"{{"hp":{},"max_hp":70,"gold":99,"deck":[{{"id":"STRIKE_SILENT"}}],"relics":[{{"id":"RING_OF_THE_SNAKE"}}],"potions":[null]}}"#,
            w.hp()
        ),
        out,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_hidden_key(id: u64) -> u64 {
    unsafe { world(id) }.steps.len() as u64
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_state_key(id: u64) -> u64 {
    unsafe { world(id) }.round() as u64
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
