//! The search's C ABI, beside the world's (src/ffi.rs), as src/Sts2Ai.Engine/Native.cs calls it: the combat net
//! loaded once, then a turn's search from a world, each in the calling thread. V stays on the C# side: the search asks
//! it through `SzPrice`, from the thread that called.

use std::ffi::c_void;
use std::sync::Arc;
use std::sync::atomic::{AtomicI32, Ordering};

use super::leaf::{Leaf, Net, NetFile};
use super::solver::Solver;
use super::state::{Action, Crossed, Crossing, Place, Status};
use crate::ffi::World;
use crate::protocol::CombatObservation;
use crate::world::Live;
use std::collections::HashMap;
use std::sync::Mutex;
use super::state::State;
use super::threads::{Stop, Threads};
use super::{beam, pimc, read};
use crate::ffi::{SzBuf, call, engine_world, give, text};
use crate::protocol::{self, RunChange};

/// C: `sz_price`. V of the run the search stands in, with the change `json` points at over it (`sz_world_run`'s form),
/// written to `value`; 0 when V gave it.
pub type SzPrice = unsafe extern "C" fn(ctx: *mut c_void, json: *const u8, len: usize, value: *mut f64) -> i32;

thread_local! {
    /// Where the search this thread runs is told to stop: a step stops once it is not 0.
    static STOP: std::cell::Cell<*const AtomicI32> = const { std::cell::Cell::new(std::ptr::null()) };
}

/// The error a search stopped by `stop` fails with.
pub const STOPPED: &str = "the search was told to stop";

/// Runs `body` with this thread's search told to stop by `stop` (null: never).
pub fn with_stop<T>(stop: *const AtomicI32, body: impl FnOnce() -> T) -> T {
    let before = STOP.with(|s| s.replace(stop));
    let result = body();
    STOP.with(|s| s.set(before));
    result
}

/// A panic's message.
pub fn panic_text(payload: Box<dyn std::any::Any + Send>) -> String {
    match payload.downcast::<String>() {
        Ok(text) => *text,
        Err(payload) => payload.downcast::<&'static str>().map(|text| text.to_string()).unwrap_or_else(|_| "a panic with no message".into()),
    }
}

/// Whether this thread's search was told to stop.
pub fn stopped() -> bool {
    STOP.with(|stop| {
        let at = stop.get();
        !at.is_null() && unsafe { &*at }.load(Ordering::Relaxed) != 0
    })
}

/// The net of a handle.
unsafe fn net<'a>(id: u64) -> Result<&'a Net, String> {
    if id == 0 {
        return Err("no net: the handle is 0".into());
    }
    Ok(unsafe { &*(id as *const Net) })
}

/// V asked of the C# side.
fn pricer(price: SzPrice, ctx: *mut c_void) -> impl Fn(&RunChange) -> Result<f64, String> {
    let ctx = ctx as usize;
    move |change: &RunChange| {
        let mut json = Vec::with_capacity(4096);
        protocol::run_json(change, &mut json);
        let mut value = 0.0;
        if unsafe { price(ctx as *mut c_void, json.as_ptr(), json.len(), &mut value) } != 0 {
            return Err("V failed on the C# side".into());
        }
        Ok(value)
    }
}

/// The combat net: its graph (ONNX), the rest of its file as JSON (`name`, `buckets`, `loss_min`, `lags`, `vocab`,
/// `potion_groups`), read by the ONNX Runtime library at `ort`; 0 on error. Used from any number of threads at once.
///
/// # Safety
/// Each pointer points at its length of readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_net_load(graph: *const u8, graph_len: usize, file: *const u8, file_len: usize, ort: *const u8, ort_len: usize) -> u64 {
    call(0, || {
        if graph.is_null() {
            return Err("the graph: a null pointer".into());
        }
        let graph = unsafe { std::slice::from_raw_parts(graph, graph_len) }.to_vec();
        let file: NetFile = serde_json::from_str(unsafe { text(file, file_len, "the net's file") }?).map_err(|e| format!("the net's file: {e}"))?;
        let net = Net::load(file, graph, unsafe { text(ort, ort_len, "the ONNX Runtime library") }?)?;
        Ok(Box::into_raw(Box::new(net)) as u64)
    })
}

/// Frees a net no search uses any more.
///
/// # Safety
/// `net` is a live net of this library or 0, and is not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_net_free(net: u64) {
    if net != 0 {
        drop(unsafe { Box::from_raw(net as *mut Net) });
    }
}

/// The leaf's outcome of the state `world` stands at, with the record of enemy moves `history` (JSON), as
/// `CombatNet.Outcome` gives it: `out` gets the value, the chance to win and the HP. `base` tells apart the runs V
/// prices over (the run given at the world's import); `price` asks V with `ctx`. 0 when it worked.
///
/// # Safety
/// `net` and `world` are live handles, `world` used by no other thread during the call; `history` points at `len`
/// readable bytes; `out` at 3 doubles.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_net_outcome(
    net: u64, world: u64, history: *const u8, len: usize, base: u64, price: SzPrice, ctx: *mut c_void, out: *mut f64,
) -> i32 {
    call(1, || {
        let net = unsafe { self::net(net) }?;
        let (world, sample) = unsafe { engine_world(world) }?;
        let history = read::history(unsafe { text(history, len, "the history") }?.as_bytes())?;
        let mut state = State::new(world.clone(), sample);
        let price = pricer(price, ctx);
        let leaf = net.leaf(base, &price);
        let leaf_state = super::leaf::LeafState {
            status: state.status,
            obs: state.obs.clone(),
            run: state.run(),
            history: Arc::new(history),
        };
        let outcome = leaf.outcome(&leaf_state)?;
        unsafe {
            *out = outcome.value;
            *out.add(1) = outcome.win;
            *out.add(2) = outcome.hp;
        }
        Ok(0)
    })
}

/// NoSL's search of a decision (`pimc::search`) from the real combat `world` stands at, between the player's actions, as
/// `asked` gives it (`read::pimc_asked`'s JSON): its worlds drawn there and searched in `threads` threads, every state
/// read in NoSL where `nosl` is not 0; `out` gets each world's line after each first action (`read::write_lines`).
/// `base`, `price` and `ctx` as for `sz_net_outcome`: V is asked through `price` from the search's threads, any number
/// at once. 0 when it worked, 1 on error.
///
/// # Safety
/// As for `sz_net_outcome`; `asked` points at `len` readable bytes; `out` points at an `sz_buf` to fill.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_pimc(
    net: u64, world: u64, asked: *const u8, len: usize, base: u64, price: SzPrice, ctx: *mut c_void, nosl: i32, threads: i32, out: *mut SzBuf,
) -> i32 {
    if out.is_null() {
        return call(1, || Err("no buffer to fill: a null pointer".into()));
    }
    unsafe { *out = SzBuf::EMPTY };
    call(1, || {
        let net = unsafe { self::net(net) }?;
        let (world, _) = unsafe { engine_world(world) }?;
        let asked = read::pimc_asked(unsafe { text(asked, len, "the NoSL search asked for") }?.as_bytes())?;
        let price = pricer(price, ctx);
        let leaf_of = || net.leaf_in(base, &price, nosl != 0);
        let worlds = pimc::search(world, &asked, threads.max(1) as usize, &leaf_of)?;
        let mut json = Vec::with_capacity(16 * 1024);
        read::write_lines(&worlds, &mut json);
        give(json, out);
        Ok(0)
    })
}

/// Whether the int at `stop` says to stop.
fn stopped_by(stop: *const AtomicI32) -> bool {
    !stop.is_null() && unsafe { &*stop }.load(Ordering::Relaxed) != 0
}

/// The SL planner's plan (`beam::search`) from the state `world` stands at, as `asked` gives it (`read::plan_asked`'s
/// JSON), its turns searched in `threads` threads; `out` gets what the beam found (`read::write_found`), which C#'s
/// PlanOf.Found makes the plan of. `base`, `price` and `ctx` as for `sz_net_outcome`: V is asked through `price` from
/// the search's threads, any number at once. The search stops where it stands once the int at `stop` is not 0 (none:
/// null), and says 2. 0 when it worked, 1 on error.
///
/// # Safety
/// As for `sz_net_outcome`; `asked` points at `len` readable bytes; `stop` is null or an int that lives through the
/// call; `out` points at an `sz_buf` to fill.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_plan(
    net: u64, world: u64, asked: *const u8, len: usize, base: u64, price: SzPrice, ctx: *mut c_void, threads: i32, stop: *const AtomicI32,
    out: *mut SzBuf,
) -> i32 {
    unsafe {
        planned(net, asked, len, base, price, ctx, threads, stop, out, || {
            let (world, sample) = engine_world(world)?;
            Ok(State::new(world.clone(), sample))
        })
    }
}

/// C: `sz_cross`. What stands after a path of answers from a plan's card prompt (`path`: the answers' texts, a JSON
/// array), written to `sink` with `sz_sink_put`: a line of JSON (`status`, `hidden_key`, `observation`, `run`, its
/// decimals as strings so their scale is kept), then where the player can play again the combat written out for the
/// engine (`EngineExport`'s bytes). 0 when it worked.
pub type SzCross = unsafe extern "C" fn(ctx: *mut c_void, path: *const u8, len: usize, sink: *mut c_void) -> i32;

/// Adds bytes to a sink an `sz_cross` is given.
///
/// # Safety
/// `sink` is the one the engine gave the callback, during it; `bytes` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_sink_put(sink: *mut c_void, bytes: *const u8, len: usize) {
    if !sink.is_null() && !bytes.is_null() {
        unsafe { (*(sink as *mut Vec<u8>)).extend_from_slice(std::slice::from_raw_parts(bytes, len)) };
    }
}

/// The SL planner's plan from a card prompt that the node's clone stands at (C#: EnginePlanner's plan on a PromptRoot):
/// each path of answers is crossed by `cross` (with `cross_ctx`), from any of the search's threads, until the player can
/// play again, and the engine's from there. The rest as for `sz_plan`.
///
/// # Safety
/// As for `sz_plan`; `cross` with `cross_ctx` is callable from any thread during the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_plan_crossed(
    net: u64, cross: SzCross, cross_ctx: *mut c_void, asked: *const u8, len: usize, base: u64, price: SzPrice, ctx: *mut c_void, threads: i32,
    stop: *const AtomicI32, out: *mut SzBuf,
) -> i32 {
    unsafe {
        planned(net, asked, len, base, price, ctx, threads, stop, out, || {
            let crossing: Arc<dyn Crossing> = Arc::new(Callback {
                cross,
                ctx: cross_ctx as usize,
                places: Mutex::new(HashMap::new()),
            });
            Ok(State::at(crossing.at(&[])?, &crossing))
        })
    }
}

/// A plan's search from the root `root` makes.
unsafe fn planned(
    net: u64, asked: *const u8, len: usize, base: u64, price: SzPrice, ctx: *mut c_void, threads: i32, stop: *const AtomicI32, out: *mut SzBuf,
    root: impl FnOnce() -> Result<State, String>,
) -> i32 {
    if out.is_null() {
        return call(1, || Err("no buffer to fill: a null pointer".into()));
    }
    unsafe { *out = SzBuf::EMPTY };
    let result = with_stop(stop, || call(1, || {
        let net = unsafe { self::net(net) }?;
        let asked = read::plan_asked(unsafe { text(asked, len, "the plan asked for") }?.as_bytes())?;
        let price = pricer(price, ctx);
        let root = root()?;
        let searcher = Threads {
            root: &root,
            net,
            base,
            price: &price,
            threads: threads.max(1) as usize,
            stop: Stop(stop),
        };
        let found = beam::search(&asked.budget, Arc::new(asked.history), &searcher)?;
        let mut json = Vec::with_capacity(4096);
        read::write_found(&found, &mut json);
        give(json, out);
        Ok(0)
    }));
    if result != 0 && stopped_by(stop) { 2 } else { result }
}

/// What stands after a path of answers, as the C# side said: a prompt or the combat's end crossed, or the combat
/// written out, which each step there takes in anew (C#: PromptRoot's Place).
enum Reply {
    Crossed(Arc<Crossed>),
    Export(String),
}

/// The C# side's crossing, each path asked once.
struct Callback {
    cross: SzCross,
    ctx: usize,
    places: Mutex<HashMap<String, Arc<Reply>>>,
}

#[derive(serde::Deserialize)]
struct CrossedJson {
    status: i32,
    hidden_key: u64,
    observation: CombatObservation,
    run: RunChange,
}

impl Callback {
    fn ask(&self, path: &[Action]) -> Result<Reply, String> {
        let texts: Vec<String> = path.iter().map(|a| a.text()).collect();
        let asked = serde_json::to_vec(&texts).map_err(|e| e.to_string())?;
        let mut sink: Vec<u8> = Vec::new();
        let said = unsafe { (self.cross)(self.ctx as *mut c_void, asked.as_ptr(), asked.len(), &mut sink as *mut Vec<u8> as *mut c_void) };
        if said != 0 {
            return Err(format!("the C# side could not cross [{}]", texts.join(" ")));
        }
        let line = sink.iter().position(|&b| b == b'\n').unwrap_or(sink.len());
        let head: CrossedJson = serde_json::from_slice(&sink[..line]).map_err(|e| format!("a crossed prompt: {e}"))?;
        let export = sink.get(line + 1..).unwrap_or(&[]);
        if !export.is_empty() {
            return Ok(Reply::Export(String::from_utf8(export.to_vec()).map_err(|e| format!("the export is not UTF-8: {e}"))?));
        }
        Ok(Reply::Crossed(Arc::new(Crossed {
            path: path.to_vec(),
            status: match head.status {
                0 => Status::InProgress,
                1 => Status::Won,
                _ => Status::Lost,
            },
            obs: Arc::new(head.observation),
            run: head.run,
            hidden_key: head.hidden_key,
        })))
    }
}

impl Crossing for Callback {
    fn at(&self, path: &[Action]) -> Result<Place, String> {
        let key: Vec<String> = path.iter().map(|a| a.text()).collect();
        let key = key.join(" ");
        let known = self.places.lock().expect("places").get(&key).cloned();
        let reply = match known {
            Some(reply) => reply,
            None => {
                let reply = Arc::new(self.ask(path)?);
                self.places.lock().expect("places").entry(key).or_insert(reply).clone()
            }
        };
        Ok(match &*reply {
            Reply::Crossed(crossed) => Place::Crossed(crossed.clone()),
            Reply::Export(text) => Place::Engine(Box::new(<Live as World>::import(text)?)),
        })
    }
}

/// The searches of a call from one world, in `threads` threads.
unsafe fn threads<'a>(
    net: &'a Net, world: u64, base: u64, price: &'a super::leaf::Price<'a>, threads: i32, stop: *const AtomicI32,
) -> Result<(State, impl Fn(&'a State) -> Threads<'a>), String> {
    let (world, sample) = unsafe { engine_world(world) }?;
    Ok((State::new(world.clone(), sample), searcher(net, base, price, threads, stop)))
}

/// The searches of a call from a card prompt the node's clone stands at, after `path` (JSON: the answers from the
/// prompt the solver began at), each path from there crossed by `cross` (with `cross_ctx`), in `threads` threads.
unsafe fn crossed<'a>(
    net: &'a Net, cross: SzCross, cross_ctx: *mut c_void, path: *const u8, len: usize, base: u64, price: &'a super::leaf::Price<'a>,
    threads: i32, stop: *const AtomicI32,
) -> Result<(State, impl Fn(&'a State) -> Threads<'a>), String> {
    let texts: Vec<String> =
        serde_json::from_str(unsafe { text(path, len, "the path of answers") }?).map_err(|e| format!("the path of answers: {e}"))?;
    let path = texts.iter().map(|t| Action::parse(t)).collect::<Result<Vec<Action>, String>>()?;
    let crossing: Arc<dyn Crossing> = Arc::new(Callback {
        cross,
        ctx: cross_ctx as usize,
        places: Mutex::new(HashMap::new()),
    });
    Ok((State::at(crossing.at(&path)?, &crossing), searcher(net, base, price, threads, stop)))
}

fn searcher<'a>(net: &'a Net, base: u64, price: &'a super::leaf::Price<'a>, threads: i32, stop: *const AtomicI32) -> impl Fn(&'a State) -> Threads<'a> {
    let count = threads.max(1) as usize;
    move |root: &'a State| Threads {
        root,
        net,
        base,
        price,
        threads: count,
        stop: Stop(stop),
    }
}

/// The solver of a handle.
unsafe fn solver<'a>(id: u64) -> Result<&'a mut Solver, String> {
    if id == 0 {
        return Err("no solver: the handle is 0".into());
    }
    Ok(unsafe { &mut *(id as *mut Solver) })
}

/// A solver (C#: SlSolver) from the record of enemy moves at its root (JSON), the pick's turn decay, and the states it
/// searches at a time; 0 on error. One thread uses it at a time.
///
/// # Safety
/// `history` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_new(history: *const u8, len: usize, decay: f64, batch: i32) -> u64 {
    call(0, || {
        let history = read::history(unsafe { text(history, len, "the history") }?.as_bytes())?;
        Ok(Box::into_raw(Box::new(Solver::new(Arc::new(history), decay, batch.max(1) as usize))) as u64)
    })
}

/// xhigh's early stop for the solver's runs from now on (`Solver::set_stop`; 0, none). 0 when it worked.
///
/// # Safety
/// `solver` is live, used by no other thread during the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_stop(solver: u64, stop: i64) -> i32 {
    call(1, || {
        unsafe { self::solver(solver) }?.set_stop(stop);
        Ok(0)
    })
}

/// Frees a solver.
///
/// # Safety
/// `solver` is a live solver of this library or 0, and is not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_free(solver: u64) {
    if solver != 0 {
        drop(unsafe { Box::from_raw(solver as *mut Solver) });
    }
}

/// C#: SlSolver.Seed, with the line (a `TurnEnd`'s JSON) and the value its loss died from. 0 when it worked.
///
/// # Safety
/// `solver` is live, used by no other thread during the call; `line` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_seed(solver: u64, line: *const u8, len: usize, from: f64) -> i32 {
    call(1, || {
        let solver = unsafe { self::solver(solver) }?;
        solver.seed(read::end(unsafe { text(line, len, "the line") }?.as_bytes())?, from);
        Ok(0)
    })
}

/// C#: SlSolver.Run: at least `nodes` more nodes, the turns searched from `world`, where the solver's root stands, in
/// `threads` threads. The rest as for `sz_plan`; 2 where it was told to stop.
///
/// # Safety
/// As for `sz_plan`; `solver` is live, used by no other thread during the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_run(
    solver: u64, net: u64, world: u64, nodes: i64, base: u64, price: SzPrice, ctx: *mut c_void, threads: i32, stop: *const AtomicI32,
) -> i32 {
    let result = with_stop(stop, || call(1, || {
        let solver = unsafe { self::solver(solver) }?;
        let net = unsafe { self::net(net) }?;
        let price = pricer(price, ctx);
        let (root, searcher) = unsafe { self::threads(net, world, base, &price, threads, stop) }?;
        solver.run(&searcher(&root), nodes)?;
        Ok(0)
    }));
    if result != 0 && stopped_by(stop) { 2 } else { result }
}

/// `sz_solver_run` with the root at a card prompt the node's clone stands at (C#: EngineSolver.Crossed): the prompt
/// after `path` (JSON, the answers from the prompt the solver began at), each path from there crossed by `cross` as for
/// `sz_plan_crossed`. A solver that begins there crosses the prompt first (`Solver::run`).
///
/// # Safety
/// As for `sz_solver_run`; `path` points at `path_len` readable bytes; `cross` with `cross_ctx` is callable from any
/// thread during the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_run_crossed(
    solver: u64, net: u64, cross: SzCross, cross_ctx: *mut c_void, path: *const u8, path_len: usize, nodes: i64, base: u64, price: SzPrice,
    ctx: *mut c_void, threads: i32, stop: *const AtomicI32,
) -> i32 {
    let result = with_stop(stop, || call(1, || {
        let solver = unsafe { self::solver(solver) }?;
        let net = unsafe { self::net(net) }?;
        let price = pricer(price, ctx);
        let (root, searcher) = unsafe { self::crossed(net, cross, cross_ctx, path, path_len, base, &price, threads, stop) }?;
        solver.run(&searcher(&root), nodes)?;
        Ok(0)
    }));
    if result != 0 && stopped_by(stop) { 2 } else { result }
}

/// `sz_solver_advance` to a card prompt the node's clone stands at: the root moved by the actions played (answers),
/// to the prompt after `path` as for `sz_solver_run_crossed`.
///
/// # Safety
/// As for `sz_solver_run_crossed`; `asked` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_advance_crossed(
    solver: u64, net: u64, cross: SzCross, cross_ctx: *mut c_void, path: *const u8, path_len: usize, asked: *const u8, len: usize, base: u64,
    price: SzPrice, ctx: *mut c_void, threads: i32, stop: *const AtomicI32,
) -> i32 {
    let result = with_stop(stop, || call(1, || {
        let solver = unsafe { self::solver(solver) }?;
        let net = unsafe { self::net(net) }?;
        let asked = read::advanced(unsafe { text(asked, len, "the solver's move") }?.as_bytes())?;
        let price = pricer(price, ctx);
        let (root, searcher) = unsafe { self::crossed(net, cross, cross_ctx, path, path_len, base, &price, threads, stop) }?;
        solver.advance(&asked.played, Arc::new(asked.moves), &searcher(&root))?;
        Ok(0)
    }));
    if result != 0 && stopped_by(stop) { 2 } else { result }
}

/// C#: SlSolver.Advance: the root moved by the actions played (`{"played":[…],"moves":[…]}`, the record of enemy moves
/// there), to where `world` stands. The rest as for `sz_solver_run`.
///
/// # Safety
/// As for `sz_solver_run`; `asked` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_advance(
    solver: u64, net: u64, world: u64, asked: *const u8, len: usize, base: u64, price: SzPrice, ctx: *mut c_void, threads: i32,
    stop: *const AtomicI32,
) -> i32 {
    let result = with_stop(stop, || call(1, || {
        let solver = unsafe { self::solver(solver) }?;
        let net = unsafe { self::net(net) }?;
        let asked = read::advanced(unsafe { text(asked, len, "the solver's move") }?.as_bytes())?;
        let price = pricer(price, ctx);
        let (root, searcher) = unsafe { self::threads(net, world, base, &price, threads, stop) }?;
        solver.advance(&asked.played, Arc::new(asked.moves), &searcher(&root))?;
        Ok(0)
    }));
    if result != 0 && stopped_by(stop) { 2 } else { result }
}

/// C#: SlSolver.Result, up to C#'s PlanOf.Found: `out` gets what the solver found, as `sz_plan` gives it.
///
/// # Safety
/// `solver` is live, used by no other thread during the call; `out` points at an `sz_buf` to fill.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_result(solver: u64, out: *mut SzBuf) -> i32 {
    if out.is_null() {
        return call(1, || Err("no buffer to fill: a null pointer".into()));
    }
    unsafe { *out = SzBuf::EMPTY };
    call(1, || {
        let found = unsafe { self::solver(solver) }?.result()?;
        let mut json = Vec::with_capacity(4096);
        read::write_found(&found, &mut json);
        give(json, out);
        Ok(0)
    })
}

/// The solver's counts: `out` gets its nodes, the turns searched, the states it holds, the states left, 1 where its
/// pick is proved, and the nodes when the pick's first action last changed. 0 when it worked.
///
/// # Safety
/// `solver` is live, used by no other thread during the call; `out` points at 6 longs.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sz_solver_counts(solver: u64, out: *mut i64) -> i32 {
    call(1, || {
        let solver = unsafe { self::solver(solver) }?;
        let counts = [
            solver.nodes(),
            solver.searched() as i64,
            solver.states() as i64,
            solver.left() as i64,
            solver.proved() as i64,
            solver.settled(),
        ];
        for (i, count) in counts.iter().enumerate() {
            unsafe { *out.add(i) = *count };
        }
        Ok(0)
    })
}
