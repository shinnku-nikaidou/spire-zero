//! The C ABI of libsts2ai_engine (engine/DESIGN.md, "The C ABI"), as src/Sts2Ai.Engine/Native.cs calls it. A world is
//! an opaque handle; a call on one world touches no other, so different worlds are used from different threads, and a
//! world may be freed from another thread than the one that used it. Every function catches panics: a failed call
//! says so by its result, and `sz_last_error` gives what was wrong, to the thread that made the call.
//!
//! What a world shows is read off its combat here (observe.rs, legal.rs, keys.rs, prompt.rs). What a world does
//! (taking a combat in, taking an action) is the rules side's, behind `World`.

use crate::r#gen as g;
use crate::r#gen::kinds::*;
use crate::keys;
use crate::legal;
use crate::observe;
use crate::prompt::{self, Asked};
use crate::protocol::{self, CombatObservation};
use crate::rt::{self, Combat};
use std::cell::{Cell, RefCell};
use std::panic::{self, AssertUnwindSafe};
use std::sync::Once;

// ---------------------------------------------------------------------------------------------------------------------
// A world
// ---------------------------------------------------------------------------------------------------------------------

/// What the C ABI needs of a world: the rules side. Everything a world shows is read off `combat` and `asked`.
pub trait World: Clone + Send + Sized + 'static {
    /// A world standing where a live combat stood, from the bytes `EngineExport` wrote of it.
    fn import(export: &str) -> Result<Self, String>;

    /// The world after the action ("play:10:1", "potion:0", "end_turn", or "pick:0,2" at a card prompt), where the
    /// player is asked again: their turn, a card prompt, or the combat's end. This world stays where it stands.
    fn step(&mut self, action: &str) -> Result<Self, String>;

    /// Takes the action: this world becomes the one after it.
    fn apply(&mut self, action: &str) -> Result<(), String>;

    /// The combat as it stands. At a card prompt, as it stood when the running action stopped there.
    fn combat(&mut self) -> &mut Combat;

    /// The card prompt the world waits on, if any.
    fn asked(&self) -> Option<&Asked>;
}

/// The world behind the C ABI.
type Engine = crate::world::Live;

/// A world and what was read of it where it stands.
struct Handle<W: World> {
    world: W,
    shown: Option<Shown>,
    /// Whether the world is one sample of what a NoSL player cannot see, rather than the one exact combat of SL: its
    /// hidden key is then `HiddenState.Key`, not `Full`. Every world made from it is one too.
    sample: bool,
}

/// What was read of a world where it stands: the observation, which the state key is the hash of too, and the draw
/// pile's card keys in the pile's order, which the hidden key is made of.
struct Shown {
    observation: CombatObservation,
    draw: Vec<Vec<u8>>,
}

impl<W: World> Handle<W> {
    fn new(world: W, sample: bool) -> Handle<W> {
        Handle {
            world,
            shown: None,
            sample,
        }
    }

    /// C#: RunSession.Combat. The combat as a decision shows it, with the prompt it waits on.
    fn shown(&mut self) -> &Shown {
        if self.shown.is_none() {
            let asked = self.world.asked().cloned();
            let c = self.world.combat();
            let (st, player) = live(c);
            let (mut observation, draw) = observe::seen(st, player, c);
            observation.prompt = asked.map(|asked| prompt::canonical(&asked, c).0);
            self.shown = Some(Shown { observation, draw });
        }
        self.shown.as_ref().expect("just read")
    }
}

impl<W: World> Handle<W> {
    /// What search reads of every state it reaches. Once the combat is over there is nothing to tell apart: the keys
    /// are 0 and no observation is read.
    fn node(&mut self) -> SzNode {
        let prompt = self.world.asked().is_some() as i32;
        let c = self.world.combat();
        let (st, player) = live(c);
        let status = legal::status(player, c) as i32;
        let round = g::combat::combat_state::round_number(st, c);
        if status != 0 {
            return SzNode {
                status,
                round,
                prompt,
                state_key: 0,
                hidden_key: 0,
            };
        }
        let hidden_key = self.hidden_key();
        let shown = self.shown.as_ref().expect("just read");
        SzNode {
            status,
            round,
            prompt,
            state_key: protocol::state_key(&shown.observation),
            hidden_key,
        }
    }

    /// `ICombatWorld.HiddenKey`: `HiddenState.Full` for the SL world, `HiddenState.Key` for a NoSL sample.
    fn hidden_key(&mut self) -> u64 {
        self.shown();
        let draw = &self.shown.as_ref().expect("just read").draw;
        let c = self.world.combat();
        let (st, player) = live(c);
        if self.sample {
            keys::key(st, player, draw, c)
        } else {
            keys::full(st, player, draw, c)
        }
    }
}

/// C: `sz_node`. What `sz_world_step` says of the world it made.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct SzNode {
    /// As `sz_world_status`.
    pub status: i32,
    /// The observation's round.
    pub round: i32,
    /// 1 where a card prompt waits.
    pub prompt: i32,
    /// As `sz_world_state_key`; 0 once the combat is over.
    pub state_key: u64,
    /// As `sz_world_hidden_key`; 0 once the combat is over.
    pub hidden_key: u64,
}

/// C#: NodeWorld.Combat. The live combat's state, and the player the world is for: the game's own "me".
pub(crate) fn live(c: &mut Combat) -> (CombatState, Player) {
    let manager = g::combat::combat_manager::instance(c);
    let st = g::combat::combat_manager::debug_only_get_state(manager, c);
    if rt::is_null(&st) {
        panic!("no combat state");
    }
    let player = g::context::local_context::get_me__i_combat_state(st, c);
    if rt::is_null(&player) {
        panic!("the combat has no player with the local net id");
    }
    (st, player)
}

// ---------------------------------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------------------------------

thread_local! {
    /// What was wrong with this thread's last failed call; empty after a call that worked.
    static ERROR: RefCell<String> = const { RefCell::new(String::new()) };
    /// Whether this thread is inside a call of the C ABI, whose panics are errors of the call.
    static IN_CALL: Cell<bool> = const { Cell::new(false) };
    /// Where this thread's last panic inside a call was raised.
    static PANICKED_AT: RefCell<String> = const { RefCell::new(String::new()) };
}

/// A panic inside a call is the call's error, with the place it was raised, and is not printed; any other panic goes
/// to the hook that was there.
fn hook_panics() {
    static HOOKED: Once = Once::new();
    HOOKED.call_once(|| {
        let before = panic::take_hook();
        panic::set_hook(Box::new(move |info| {
            if IN_CALL.with(Cell::get) {
                let at = info.location().map(|l| format!("{}:{}", l.file(), l.line())).unwrap_or_default();
                PANICKED_AT.with(|place| *place.borrow_mut() = at);
            } else {
                before(info);
            }
        }));
    });
}

fn fail(message: String) {
    ERROR.with(|error| *error.borrow_mut() = message);
}

/// Runs a call of the C ABI: its error, or its panic, becomes this thread's error and `failed`.
pub(crate) fn call<T>(failed: T, body: impl FnOnce() -> Result<T, String>) -> T {
    hook_panics();
    ERROR.with(|error| error.borrow_mut().clear());
    IN_CALL.with(|inside| inside.set(true));
    let result = panic::catch_unwind(AssertUnwindSafe(body));
    IN_CALL.with(|inside| inside.set(false));
    match result {
        Ok(Ok(value)) => value,
        Ok(Err(message)) => {
            fail(message);
            failed
        }
        Err(payload) => {
            let message = match payload.downcast::<String>() {
                Ok(text) => *text,
                Err(payload) => payload
                    .downcast::<&'static str>()
                    .map(|text| text.to_string())
                    .unwrap_or_else(|_| "a panic with no message".into()),
            };
            let at = PANICKED_AT.with(|place| std::mem::take(&mut *place.borrow_mut()));
            fail(if at.is_empty() { message } else { format!("{message} ({at})") });
            failed
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Handles and buffers
// ---------------------------------------------------------------------------------------------------------------------

/// C: `sz_buf`. Bytes the engine allocated, given back with `sz_buf_free`.
#[repr(C)]
pub struct SzBuf {
    pub ptr: *mut u8,
    pub len: usize,
}

impl SzBuf {
    pub const EMPTY: SzBuf = SzBuf {
        ptr: std::ptr::null_mut(),
        len: 0,
    };
}

pub(crate) fn give(bytes: Vec<u8>, out: *mut SzBuf) {
    let bytes = bytes.into_boxed_slice();
    let len = bytes.len();
    let ptr = Box::into_raw(bytes) as *mut u8;
    unsafe { *out = SzBuf { ptr, len } };
}

fn make(world: Engine, sample: bool) -> u64 {
    Box::into_raw(Box::new(Handle::new(world, sample))) as u64
}

/// The world of a handle. One world is used by one thread at a time (Native.cs).
unsafe fn world<'a>(id: u64) -> Result<&'a mut Handle<Engine>, String> {
    if id == 0 {
        return Err("no world: the handle is 0".into());
    }
    Ok(unsafe { &mut *(id as *mut Handle<Engine>) })
}

/// The world behind a handle, and whether it is a NoSL sample, for the search (search/ffi.rs).
pub(crate) unsafe fn engine_world<'a>(id: u64) -> Result<(&'a Engine, bool), String> {
    let handle = unsafe { world(id) }?;
    Ok((&handle.world, handle.sample))
}

pub(crate) unsafe fn text<'a>(bytes: *const u8, len: usize, what: &str) -> Result<&'a str, String> {
    if bytes.is_null() {
        return Err(format!("{what}: a null pointer"));
    }
    std::str::from_utf8(unsafe { std::slice::from_raw_parts(bytes, len) }).map_err(|e| format!("{what} is not UTF-8: {e}"))
}

/// A call that fills a buffer: 0 and the bytes, or 1 and an empty buffer.
unsafe fn fill(id: u64, out: *mut SzBuf, body: impl FnOnce(&mut Handle<Engine>) -> Vec<u8>) -> i32 {
    if out.is_null() {
        return call(1, || Err("no buffer to fill: a null pointer".into()));
    }
    unsafe { *out = SzBuf::EMPTY };
    call(1, || {
        let bytes = body(unsafe { world(id) }?);
        give(bytes, out);
        Ok(0)
    })
}

// ---------------------------------------------------------------------------------------------------------------------
// The functions
// ---------------------------------------------------------------------------------------------------------------------

/// A world from a live combat as `EngineExport` wrote it; 0 on error. `sample` is 1 for a combat built from what a
/// NoSL player sees and determinized, one sample of what they cannot see, whose states are told apart by
/// `HiddenState.Key`; 0 for the SL world, the one exact combat, told apart by `HiddenState.Full`. Every world made from
/// it (`sz_world_clone`, `sz_world_step`) is of the same kind.
///
/// # Safety
/// `json` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_import(json: *const u8, len: usize, sample: i32) -> u64 {
    call(0, || Ok(make(Engine::import(unsafe { text(json, len, "the export") }?)?, sample != 0)))
}

/// The SL world of a combat written out as it begins, before its first turn (world.rs, `Live::begin`), where the
/// combat's start has run: where the player is first asked; 0 on error.
///
/// # Safety
/// `json` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_begin(json: *const u8, len: usize) -> u64 {
    call(0, || Ok(make(Engine::begin(unsafe { text(json, len, "the export") }?)?, false)))
}

/// The draw pile's public record (JSON, as an export's `sim.draw_log`) as the host holds it where the world stands
/// now: what a NoSL world drawn here reads (`sz_pimc` at a card prompt). The world took the actions since its export
/// as the game did, so the host's record is the one of this state. 0 when it was set.
///
/// # Safety
/// `world` as for `sz_world_clone`; `json` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_draw_log(world: u64, json: *const u8, len: usize) -> i32 {
    call(1, || {
        let handle = unsafe { self::world(world) }?;
        let value: serde_json::Value =
            serde_json::from_str(unsafe { text(json, len, "the draw pile's record") }?).map_err(|e| format!("the draw pile's record is not JSON: {e}"))?;
        handle.world.combat().x.draw_log = Some(std::sync::Arc::new(crate::drawlog::DrawLog::parse(&value)));
        Ok(0)
    })
}

/// An independent world standing where this one stands; 0 on error.
///
/// # Safety
/// `world` is a live handle of this library, used by no other thread during the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_clone(world: u64) -> u64 {
    call(0, || {
        let handle = unsafe { self::world(world) }?;
        Ok(make(handle.world.clone(), handle.sample))
    })
}

/// A NoSL world from this one: a copy with everything the player cannot see drawn again from `seed`
/// (determinize.rs), a sample whose states are told apart by `HiddenState.Key`; 0 on error. A world between the
/// player's actions is drawn as it stands; one at a card prompt where the prompt takes its answer, from the draw pile's
/// record it holds there (`sz_world_draw_log`; world.rs, `Live::draw_at_prompt`). `world` stays as it is.
///
/// # Safety
/// `world` as for `sz_world_clone`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_determinize(world: u64, seed: u64) -> u64 {
    call(0, || {
        let handle = unsafe { self::world(world) }?;
        let mut copy = handle.world.clone();
        if copy.asked().is_some() {
            copy.draw_at_prompt(seed)?;
        } else {
            crate::determinize::determinize(copy.combat(), seed)?;
        }
        Ok(make(copy, true))
    })
}

/// Frees a world. Any thread may free a world no thread is using.
///
/// # Safety
/// `world` is a live handle of this library or 0, and is not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_free(world: u64) {
    if world != 0 {
        // A world is plain data: dropping it runs none of the game's code.
        drop(unsafe { Box::from_raw(world as *mut Handle<Engine>) });
    }
}

/// Takes an action, in `CombatAction`'s text form; 0 when it was taken.
///
/// # Safety
/// `world` as for `sz_world_clone`; `action` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_apply(world: u64, action: *const u8, len: usize) -> i32 {
    call(1, || {
        let handle = unsafe { self::world(world) }?;
        let action = unsafe { text(action, len, "the action") }?;
        handle.shown = None;
        handle.world.apply(action)?;
        Ok(0)
    })
}

/// The world after an action, made from `world`, which stays as it is and is freed on its own; 0 on error. `node` is
/// filled with what search reads of every state: one copy of the combat a state, where a clone and an apply are two.
///
/// # Safety
/// `world` and `action` as for `sz_world_apply`; `node` points at an `sz_node` to fill.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_step(world: u64, action: *const u8, len: usize, node: *mut SzNode) -> u64 {
    call(0, || {
        if node.is_null() {
            return Err("no node to fill: a null pointer".into());
        }
        let handle = unsafe { self::world(world) }?;
        let action = unsafe { text(action, len, "the action") }?;
        let mut after = Handle::new(handle.world.step(action)?, handle.sample);
        unsafe { *node = after.node() };
        Ok(Box::into_raw(Box::new(after)) as u64)
    })
}

/// 0 in progress, 1 won, 2 lost; -1 on error.
///
/// # Safety
/// `world` as for `sz_world_clone`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_status(world: u64) -> i32 {
    call(-1, || {
        let c = unsafe { self::world(world) }?.world.combat();
        let (_, player) = live(c);
        Ok(legal::status(player, c) as i32)
    })
}

/// The plays the game allows, each on a line, in the game's order; none at a card prompt or once the combat is over.
///
/// # Safety
/// `world` as for `sz_world_clone`; `out` points at an `sz_buf` to fill.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_legal(world: u64, out: *mut SzBuf) -> i32 {
    unsafe {
        fill(world, out, |handle| {
            let at_prompt = handle.world.asked().is_some();
            let c = handle.world.combat();
            let (st, player) = live(c);
            protocol::legal_text(&legal::plays(st, player, at_prompt, c))
        })
    }
}

/// The observation's JSON, byte for byte what `ProtocolJson` writes of the real combat's.
///
/// # Safety
/// As for `sz_world_legal`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_observe(world: u64, out: *mut SzBuf) -> i32 {
    unsafe {
        fill(world, out, |handle| {
            let mut json = Vec::with_capacity(4096);
            protocol::observation_json(&handle.shown().observation, &mut json);
            json
        })
    }
}

/// What the combat has changed of the run: `{"hp","max_hp","gold","deck","relics","potions"}` in ProtocolJson's form.
///
/// # Safety
/// As for `sz_world_legal`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_run(world: u64, out: *mut SzBuf) -> i32 {
    unsafe {
        fill(world, out, |handle| {
            let c = handle.world.combat();
            let (_, player) = live(c);
            let mut json = Vec::with_capacity(2048);
            protocol::run_json(&observe::run(player, c), &mut json);
            json
        })
    }
}

/// `ICombatWorld.HiddenKey`: of the SL world `HiddenState.Full`, of a NoSL sample `HiddenState.Key`
/// (`sz_world_import`). 0 with this thread's error set on error.
///
/// # Safety
/// `world` as for `sz_world_clone`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_hidden_key(world: u64) -> u64 {
    call(0, || Ok(unsafe { self::world(world) }?.hidden_key()))
}

/// `PimcBrain.StateKey` of the world's observation. 0 with this thread's error set on error.
///
/// # Safety
/// `world` as for `sz_world_clone`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_world_state_key(world: u64) -> u64 {
    call(0, || Ok(protocol::state_key(&unsafe { self::world(world) }?.shown().observation)))
}

/// Gives back a buffer one of the calls above filled.
///
/// # Safety
/// `buf` is a buffer of this library that was not freed before, or an empty one.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_buf_free(buf: SzBuf) {
    if !buf.ptr.is_null() {
        drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(buf.ptr, buf.len)) });
    }
}

/// This thread's last error: what the last call that failed on this thread said, or nothing after one that worked.
/// Always 0.
///
/// # Safety
/// `out` points at an `sz_buf` to fill.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_last_error(out: *mut SzBuf) -> i32 {
    give(ERROR.with(|error| error.borrow().clone()).into_bytes(), out);
    0
}
