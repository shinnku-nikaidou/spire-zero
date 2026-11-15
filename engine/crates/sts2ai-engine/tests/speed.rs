//! Not a check: what the engine's own operations cost, on states in the middle of combats of a dump (whole runs:
//! `sim parity --dump DIR --runs N`). Run by hand, with nothing else running:
//!
//!     STS2AI_ENGINE_FIXTURES=<dump> cargo test --release --test speed -- --ignored --nocapture
//!
//! For each sampled state: the import, a copy of the world, an action (a play, the end of the turn), a search node
//! (the world after an action with its status and keys, by `sz_world_step` and by a copy and an apply), the
//! observation and its parts, the plays, the two keys, all through the C ABI as the C# side calls them; what a copy
//! allocates; and, arena by arena, what the copy's time and bytes go to and how much of it the rest of the combat
//! never changes.
//!
//! `spin` does one kind of call over the same states again and again and samples its own stack meanwhile, a thousand
//! times a second of its CPU time; engine/sample.py turns the file of stacks into a report by function. Built with
//! line tables, the functions rustc inlined are told apart too:
//!
//!     STS2AI_SPEED_SPIN=play|end_turn|observe|node|legal|clone STS2AI_SPEED_SECONDS=20 STS2AI_SPEED_STACKS=<file> \
//!         CARGO_PROFILE_RELEASE_DEBUG=line-tables-only cargo test --release --test speed spin -- --ignored --nocapture
//!     engine/sample.py <file>
//!
//! `grown` counts, arena by arena, the objects one action of a kind makes (`STS2AI_SPEED_GROWN=play|end_turn`).

use std::alloc::{GlobalAlloc, Layout, System};
use std::collections::BTreeMap;
use std::sync::atomic::{AtomicU64, Ordering::Relaxed};
use std::time::Instant;
use sts2ai_engine::ffi;
use sts2ai_engine::ffi::World as _;
use sts2ai_engine::world::Live;

// ---------------------------------------------------------------------------------------------------------------------
// What is allocated
// ---------------------------------------------------------------------------------------------------------------------

struct Counting;
static BYTES: AtomicU64 = AtomicU64::new(0);
static CALLS: AtomicU64 = AtomicU64::new(0);

unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        BYTES.fetch_add(layout.size() as u64, Relaxed);
        CALLS.fetch_add(1, Relaxed);
        unsafe { System.alloc(layout) }
    }
    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        unsafe { System.dealloc(ptr, layout) }
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

/// What `f` allocated: bytes and calls.
fn allocated<T>(f: impl FnOnce() -> T) -> (T, u64, u64) {
    let (bytes, calls) = (BYTES.load(Relaxed), CALLS.load(Relaxed));
    let value = f();
    (
        value,
        BYTES.load(Relaxed) - bytes,
        CALLS.load(Relaxed) - calls,
    )
}

// ---------------------------------------------------------------------------------------------------------------------
// A world through the C ABI
// ---------------------------------------------------------------------------------------------------------------------

struct World(u64);

fn take(buf: ffi::SzBuf) -> Vec<u8> {
    let bytes = unsafe { std::slice::from_raw_parts(buf.ptr, buf.len) }.to_vec();
    unsafe { ffi::sz_buf_free(buf) };
    bytes
}

impl World {
    fn import(export: &[u8]) -> Option<World> {
        match unsafe { ffi::sz_world_import(export.as_ptr(), export.len()) } {
            0 => None,
            world => Some(World(world)),
        }
    }
    fn copy(&self) -> World {
        World(unsafe { ffi::sz_world_clone(self.0) })
    }
    fn apply(&mut self, action: &str) -> bool {
        unsafe { ffi::sz_world_apply(self.0, action.as_ptr(), action.len()) == 0 }
    }
    fn text(&self, call: unsafe extern "C" fn(u64, *mut ffi::SzBuf) -> i32) -> Vec<u8> {
        let mut buf = ffi::SzBuf::EMPTY;
        assert_eq!(unsafe { call(self.0, &mut buf) }, 0);
        take(buf)
    }
}

impl Drop for World {
    fn drop(&mut self) {
        unsafe { ffi::sz_world_free(self.0) };
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// The numbers
// ---------------------------------------------------------------------------------------------------------------------

/// Microseconds of one call, the least of a few rounds of `n` calls each: what the call costs when nothing is in its way.
fn micros(n: usize, mut f: impl FnMut()) -> f64 {
    let mut best = f64::MAX;
    for _ in 0..5 {
        let start = Instant::now();
        for _ in 0..n {
            f();
        }
        best = best.min(start.elapsed().as_secs_f64() * 1e6 / n as f64);
    }
    best
}

#[derive(Default)]
struct Series(Vec<f64>);

impl Series {
    fn add(&mut self, value: f64) {
        self.0.push(value);
    }
    fn line(&mut self, what: &str, unit: &str) -> String {
        if self.0.is_empty() {
            return format!("{what:<34} no sample");
        }
        self.0.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let at = |q: f64| self.0[((self.0.len() - 1) as f64 * q).round() as usize];
        let mean = self.0.iter().sum::<f64>() / self.0.len() as f64;
        format!(
            "{what:<34} mean {mean:>10.1}   median {:>10.1}   p10 {:>10.1}   p90 {:>10.1}   max {:>10.1} {unit}  ({} samples)",
            at(0.5),
            at(0.1),
            at(0.9),
            at(1.0),
            self.0.len()
        )
    }
}

fn kind_of(action: &str) -> &'static str {
    match action.split(':').next().unwrap_or("") {
        "end_turn" => "apply: end_turn",
        "play" => "apply: play a card",
        "potion" => "apply: use a potion",
        "pick" => "apply: answer a prompt",
        _ => "apply: other",
    }
}

/// The dump's folder and its combats, every so many of them: about sixty.
fn sampled() -> (std::path::PathBuf, Vec<std::path::PathBuf>) {
    let root = std::path::PathBuf::from(
        std::env::var_os("STS2AI_ENGINE_FIXTURES").expect("STS2AI_ENGINE_FIXTURES"),
    );
    let mut folders: Vec<_> = std::fs::read_dir(&root)
        .unwrap()
        .map(|e| e.unwrap().path())
        .filter(|p| p.is_dir())
        .collect();
    folders.sort();
    let every = (folders.len() / 60).max(1);
    (root, folders.into_iter().step_by(every).collect())
}

/// The decision in the middle of a combat, at a place where the engine takes a combat in: its number and its export.
fn middle(folder: &std::path::Path) -> Option<(usize, Vec<u8>)> {
    let combat: serde_json::Value =
        serde_json::from_str(&std::fs::read_to_string(folder.join("combat.json")).unwrap())
            .unwrap();
    let decisions = combat["decisions"].as_array().unwrap();
    let with_export: Vec<usize> = (0..decisions.len() - 1)
        .filter(|k| {
            folder.join(format!("{k}.export.json")).exists()
                && decisions[*k]["prompt"].as_bool() != Some(true)
        })
        .collect();
    let k = *with_export.get(with_export.len() / 2)?;
    Some((
        k,
        std::fs::read(folder.join(format!("{k}.export.json"))).unwrap(),
    ))
}

// ---------------------------------------------------------------------------------------------------------------------
// Sampling the test's own stack
// ---------------------------------------------------------------------------------------------------------------------

/// A sampling profiler with nothing but the C library: a timer of the process's CPU time interrupts the working
/// thread, and the handler notes the return addresses on its stack. The addresses are written as they are, with where
/// the program was loaded; engine/sample.py names them.
mod stacks {
    use std::cell::Cell;
    use std::ffi::c_void;
    use std::io::Write;
    use std::sync::atomic::{AtomicPtr, AtomicUsize, Ordering::Relaxed};

    #[repr(C)]
    struct Timeval {
        seconds: i64,
        micros: i64,
    }

    #[repr(C)]
    struct Itimerval {
        interval: Timeval,
        value: Timeval,
    }

    unsafe extern "C" {
        fn backtrace(buffer: *mut *mut c_void, size: i32) -> i32;
        fn signal(signal: i32, handler: usize) -> usize;
        fn setitimer(which: i32, new: *const Itimerval, old: *mut Itimerval) -> i32;
    }

    /// Linux, x86-64 and aarch64 alike.
    const SIGPROF: i32 = 27;
    const ITIMER_PROF: i32 = 2;
    const DEPTH: usize = 128;
    /// Room for the samples: each is its length and its addresses.
    const ROOM: usize = 6_000_000;

    static NOTED: AtomicPtr<usize> = AtomicPtr::new(std::ptr::null_mut());
    static USED: AtomicUsize = AtomicUsize::new(0);

    thread_local! {
        /// Whether this is the thread whose stacks are wanted: the test harness's own threads only wait.
        static WANTED: Cell<bool> = const { Cell::new(false) };
    }

    extern "C" fn tick(_: i32) {
        if !WANTED.with(Cell::get) {
            return;
        }
        let mut frames = [std::ptr::null_mut::<c_void>(); DEPTH];
        let n = unsafe { backtrace(frames.as_mut_ptr(), DEPTH as i32) } as usize;
        let at = USED.fetch_add(n + 1, Relaxed);
        let noted = NOTED.load(Relaxed);
        if noted.is_null() || at + n + 1 > ROOM {
            return;
        }
        unsafe {
            *noted.add(at) = n;
            for (i, frame) in frames[..n].iter().enumerate() {
                *noted.add(at + 1 + i) = *frame as usize;
            }
        }
    }

    fn timer(micros: i64) {
        let every = Itimerval {
            interval: Timeval { seconds: 0, micros },
            value: Timeval { seconds: 0, micros },
        };
        unsafe { setitimer(ITIMER_PROF, &every, std::ptr::null_mut()) };
    }

    /// Samples the calling thread from now on, a thousand times a second of the process's CPU time.
    pub fn start() {
        // The first call of `backtrace` readies the unwinder, which a signal handler must not be the one to do.
        let mut frames = [std::ptr::null_mut::<c_void>(); 4];
        unsafe { backtrace(frames.as_mut_ptr(), 4) };
        NOTED.store(Box::leak(vec![0usize; ROOM].into_boxed_slice()).as_mut_ptr(), Relaxed);
        WANTED.with(|wanted| wanted.set(true));
        unsafe { signal(SIGPROF, tick as extern "C" fn(i32) as usize) };
        timer(1000);
    }

    /// Stops sampling and writes the samples: the program's mappings (which file each address is of), then one line of
    /// addresses a sample, the innermost first.
    pub fn write(path: &str) {
        timer(0);
        WANTED.with(|wanted| wanted.set(false));
        let used = USED.load(Relaxed).min(ROOM);
        let noted = unsafe { std::slice::from_raw_parts(NOTED.load(Relaxed), used) };
        let mut out = std::io::BufWriter::new(std::fs::File::create(path).expect(path));
        for line in std::fs::read_to_string("/proc/self/maps").unwrap().lines() {
            if line.split_whitespace().nth(5).is_some_and(|file| file.starts_with('/')) {
                writeln!(out, "map {line}").unwrap();
            }
        }
        let (mut at, mut samples) = (0, 0);
        while at < used && at + 1 + noted[at] <= used {
            let n = noted[at];
            let line: Vec<String> = noted[at + 1..at + 1 + n].iter().map(|a| format!("{a:x}")).collect();
            writeln!(out, "{}", line.join(" ")).unwrap();
            at += n + 1;
            samples += 1;
        }
        eprintln!("spin: {samples} stacks in {path}");
    }
}

/// One kind of call, over the sampled states, for as long as asked, sampling its own stack if asked where to write
/// the stacks. Every call is on a fresh copy of its state (a world keeps its observation once made, and an action
/// changes it), so the copy and its release are in the samples too, under `sz_world_clone` and `sz_world_free`.
/// `node` is what search asks of a state it reaches: the observation, the plays and the two keys.
#[test]
#[ignore = "for a profiler, run by hand"]
fn spin() {
    let what = std::env::var("STS2AI_SPEED_SPIN").expect("STS2AI_SPEED_SPIN");
    let seconds: f64 = std::env::var("STS2AI_SPEED_SECONDS")
        .map(|s| s.parse().expect("STS2AI_SPEED_SECONDS"))
        .unwrap_or(60.0);
    let (_, folders) = sampled();
    // Each state with the actions of the kind asked for.
    let mut states: Vec<(World, Vec<String>)> = Vec::new();
    for folder in &folders {
        let Some((_, export)) = middle(folder) else {
            continue;
        };
        let Some(world) = World::import(&export) else {
            continue;
        };
        let legal = String::from_utf8(world.text(ffi::sz_world_legal)).unwrap();
        let actions: Vec<String> = legal
            .lines()
            .filter(|l| l.split(':').next() == Some(what.as_str()))
            .map(str::to_string)
            .collect();
        states.push((world, actions));
    }
    eprintln!(
        "spin: {what} over {} states for {seconds} s, process {}",
        states.len(),
        std::process::id()
    );
    let stacks = std::env::var("STS2AI_SPEED_STACKS").ok();
    if stacks.is_some() {
        stacks::start();
    }
    let start = Instant::now();
    let mut calls = 0u64;
    while start.elapsed().as_secs_f64() < seconds {
        for (world, actions) in &states {
            match what.as_str() {
                "play" | "end_turn" => {
                    for action in actions {
                        let mut copy = world.copy();
                        assert!(copy.apply(action), "{action}");
                        calls += 1;
                    }
                }
                "observe" => drop(world.copy().text(ffi::sz_world_observe)),
                "legal" => drop(world.copy().text(ffi::sz_world_legal)),
                "node" => {
                    let copy = world.copy();
                    drop(copy.text(ffi::sz_world_observe));
                    drop(copy.text(ffi::sz_world_legal));
                    unsafe { ffi::sz_world_state_key(copy.0) };
                    unsafe { ffi::sz_world_hidden_key(copy.0) };
                }
                "clone" => drop(world.copy()),
                other => panic!(
                    "STS2AI_SPEED_SPIN={other}: play, end_turn, observe, node, legal or clone"
                ),
            }
            if actions.is_empty() {
                calls += 1;
            }
        }
    }
    let spent = start.elapsed().as_secs_f64();
    if let Some(path) = stacks {
        stacks::write(&path);
    }
    eprintln!(
        "spin: {calls} calls in {spent:.1} s, {:.1} us a call with its copy",
        spent * 1e6 / calls as f64
    );
}

/// What one action leaves in the arenas: for each arena, how many objects the actions of the kind asked for add,
/// summed over the sampled states (`STS2AI_SPEED_GROWN=play|end_turn`).
#[test]
#[ignore = "a measurement, run by hand"]
fn grown() {
    let what = std::env::var("STS2AI_SPEED_GROWN").expect("STS2AI_SPEED_GROWN");
    let (_, folders) = sampled();
    let mut added: BTreeMap<&'static str, (usize, usize)> = BTreeMap::new();
    let mut actions_taken = 0usize;
    for folder in &folders {
        let Some((_, export)) = middle(folder) else {
            continue;
        };
        let Some(world) = World::import(&export) else {
            continue;
        };
        let legal = String::from_utf8(world.text(ffi::sz_world_legal)).unwrap();
        let text = String::from_utf8(export).unwrap();
        let Ok(live) = Live::import(&text) else {
            continue;
        };
        for action in legal.lines().filter(|l| l.split(':').next() == Some(what.as_str())) {
            let mut before: BTreeMap<&'static str, usize> = BTreeMap::new();
            let mut copy = live.clone();
            copy.combat().a.each(&mut |name, arena| {
                before.insert(name, arena.len());
            });
            if copy.apply(action).is_err() {
                continue;
            }
            actions_taken += 1;
            copy.combat().a.each(&mut |name, arena| {
                let e = added.entry(name).or_default();
                e.0 += arena.len().saturating_sub(before[name]);
                e.1 += arena.len();
            });
        }
    }
    let mut rows: Vec<_> = added.into_iter().filter(|r| r.1.0 > 0).collect();
    rows.sort_by_key(|r| std::cmp::Reverse(r.1.0));
    println!("{what}: {actions_taken} actions; objects added an action, and held after it, by arena:");
    for (name, (n, held)) in rows.iter().take(25) {
        println!("  {:>9.1} {:>9.1}  {name}", *n as f64 / actions_taken as f64, *held as f64 / actions_taken as f64);
    }
}

/// What an observation's time goes to, each part on fresh copies of the combat: the views as a whole (what the player
/// sees, computed from the game's objects), of them the cards of the four piles alone, and writing the views as JSON.
fn observation_parts(live: &Live, series: &mut BTreeMap<&'static str, Series>) {
    use sts2ai_engine::r#gen as g;
    use sts2ai_engine::{observe, protocol};
    let who = |c: &mut sts2ai_engine::rt::Combat| {
        let manager = g::combat::combat_manager::instance(c);
        let st = g::combat::combat_manager::debug_only_get_state(manager, c);
        (st, g::context::local_context::get_me__i_combat_state(st, c))
    };
    let fresh: Vec<Live> = (0..200).map(|_| live.clone()).collect();
    let mut next = fresh.into_iter();
    let mut views = None;
    series
        .entry("observe: the views")
        .or_default()
        .add(micros(4, || {
            let mut copy = next.next().unwrap();
            let c = copy.combat();
            let (st, player) = who(c);
            views = Some(observe::combat(st, player, c));
        }));
    series
        .entry("observe: of them the cards")
        .or_default()
        .add(micros(4, || {
            let mut copy = next.next().unwrap();
            let c = copy.combat();
            let (_, player) = who(c);
            let pcs = g::entities_players::player::player_combat_state(player, c);
            let piles = [
                g::entities_players::player_combat_state::hand(pcs, c),
                g::entities_players::player_combat_state::draw_pile(pcs, c),
                g::entities_players::player_combat_state::discard_pile(pcs, c),
                g::entities_players::player_combat_state::exhaust_pile(pcs, c),
            ];
            for (at, pile) in piles.into_iter().enumerate() {
                for card in g::entities_cards::card_pile::cards(pile, c) {
                    std::hint::black_box(observe::card(card, at == 0, c));
                }
            }
        }));
    // Of a card's view, the game's getters that ask the combat's listeners.
    type Getter = fn(sts2ai_engine::r#gen::kinds::CardModel, &mut sts2ai_engine::rt::Combat);
    let getters: [(&'static str, Getter); 5] = [
        ("observe: the cards' energy cost", |card, c| {
            let cost = g::models::card_model::energy_cost(card, c);
            if !g::entities_cards::card_energy_cost::costs_x(cost, c) {
                std::hint::black_box(g::entities_cards::card_energy_cost::get_with_modifiers(
                    cost,
                    sts2ai_engine::r#gen::enums::CostModifiers::All,
                    c,
                ));
            }
        }),
        ("observe: the cards' star cost", |card, c| {
            if g::dispatch::card_model::canonical_star_cost(card, c) >= 0 {
                std::hint::black_box(g::models::card_model::get_star_cost_with_modifiers(card, c));
            }
        }),
        ("observe: the cards' keywords", |card, c| {
            std::hint::black_box(g::models::card_model::keywords(card, c));
        }),
        ("observe: the cards' replay count", |card, c| {
            std::hint::black_box(g::models::card_model::get_enchanted_replay_count(card, c));
        }),
        ("observe: the cards' calculated number", |card, c| {
            let vars = g::models::card_model::dynamic_vars(card, c);
            let values = g::localization_dynamic_vars::dynamic_var_set::values(vars, c);
            let first = values
                .into_iter()
                .find(|v| sts2ai_engine::rt::is(*v, sts2ai_engine::r#gen::kinds::Kind::CalculatedVar, c));
            if let Some(v) = first {
                std::hint::black_box(g::localization_dynamic_vars::calculated_var::calculate(
                    v,
                    sts2ai_engine::rt::Obj::NULL,
                    c,
                ));
            }
        }),
    ];
    for (name, getter) in getters {
        series.entry(name).or_default().add(micros(4, || {
            let mut copy = next.next().unwrap();
            let c = copy.combat();
            let (_, player) = who(c);
            let pcs = g::entities_players::player::player_combat_state(player, c);
            let piles = [
                g::entities_players::player_combat_state::hand(pcs, c),
                g::entities_players::player_combat_state::draw_pile(pcs, c),
                g::entities_players::player_combat_state::discard_pile(pcs, c),
                g::entities_players::player_combat_state::exhaust_pile(pcs, c),
            ];
            for pile in piles {
                for card in g::entities_cards::card_pile::cards(pile, c) {
                    getter(card, c);
                }
            }
        }));
    }
    let views = views.unwrap();
    series
        .entry("observe: the cards, how many")
        .or_default()
        .add(
            (views.hand.len() + views.draw.len() + views.discard.len() + views.exhaust.len())
                as f64,
        );
    series
        .entry("observe: JSON")
        .or_default()
        .add(micros(20, || {
            let mut json = Vec::with_capacity(4096);
            protocol::observation_json(&views, &mut json);
            std::hint::black_box(json);
        }));
}

#[test]
#[ignore = "a measurement, run by hand"]
fn speed() {
    let (root, folders) = sampled();
    let mut series: BTreeMap<&'static str, Series> = BTreeMap::new();
    let mut arenas: BTreeMap<&'static str, (usize, f64, u64, u64, usize)> = BTreeMap::new();
    let (mut states, mut holding, mut steady_bytes, mut all_bytes) = (0usize, 0usize, 0u64, 0u64);
    let mut inline = 0usize;
    for folder in folders.iter() {
        let Some((k, export)) = middle(folder) else {
            continue;
        };
        let combat: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(folder.join("combat.json")).unwrap())
                .unwrap();
        let decisions = combat["decisions"].as_array().unwrap();
        let Some(world) = World::import(&export) else {
            continue;
        };
        states += 1;
        series
            .entry("export")
            .or_default()
            .add(export.len() as f64 / 1024.0);
        series
            .entry("import")
            .or_default()
            .add(micros(3, || drop(World::import(&export))));
        series
            .entry("clone")
            .or_default()
            .add(micros(200, || drop(world.copy())));
        let (_, bytes, calls) = allocated(|| world.copy());
        series
            .entry("clone: allocated")
            .or_default()
            .add(bytes as f64 / 1024.0);
        series
            .entry("clone: allocations")
            .or_default()
            .add(calls as f64);
        // A world's observation is kept once made: a fresh copy each time is what a search node pays.
        let fresh: Vec<World> = (0..50).map(|_| world.copy()).collect();
        let mut next = fresh.iter().cycle();
        series.entry("observe").or_default().add(micros(10, || {
            drop(next.next().unwrap().text(ffi::sz_world_observe))
        }));
        series.entry("legal").or_default().add(micros(10, || {
            drop(next.next().unwrap().text(ffi::sz_world_legal))
        }));
        series.entry("state key").or_default().add(micros(10, || {
            unsafe { ffi::sz_world_state_key(next.next().unwrap().0) };
        }));
        series.entry("hidden key").or_default().add(micros(10, || {
            unsafe { ffi::sz_world_hidden_key(next.next().unwrap().0) };
        }));
        series
            .entry("observation")
            .or_default()
            .add(world.text(ffi::sz_world_observe).len() as f64 / 1024.0);
        // Every play the state allows, each on a copy of its own (the copy is not timed).
        let legal = String::from_utf8(world.text(ffi::sz_world_legal)).unwrap();
        for action in legal.lines().filter(|l| !l.is_empty()) {
            let mut best = f64::MAX;
            for _ in 0..3 {
                let mut copy = world.copy();
                let start = Instant::now();
                let taken = copy.apply(action);
                let spent = start.elapsed().as_secs_f64() * 1e6;
                if taken {
                    best = best.min(spent);
                }
            }
            if best < f64::MAX {
                series.entry(kind_of(action)).or_default().add(best);
            }
        }

        // A search node, two ways: the world after a play with its status and its two keys, and its release. One call
        // that makes it of the world before, or a copy, the action in it, and the three questions.
        for action in legal.lines().filter(|l| !l.is_empty()) {
            let (mut stepped, mut copied) = (f64::MAX, f64::MAX);
            for _ in 0..3 {
                let start = Instant::now();
                let mut node = ffi::SzNode::default();
                let after = unsafe {
                    ffi::sz_world_step(world.0, action.as_ptr(), action.len(), &mut node)
                };
                if after != 0 {
                    drop(World(after));
                    stepped = stepped.min(start.elapsed().as_secs_f64() * 1e6);
                }
                let start = Instant::now();
                let mut copy = world.copy();
                if copy.apply(action) {
                    if unsafe { ffi::sz_world_status(copy.0) } == 0 {
                        unsafe { ffi::sz_world_state_key(copy.0) };
                        unsafe { ffi::sz_world_hidden_key(copy.0) };
                    }
                    drop(copy);
                    copied = copied.min(start.elapsed().as_secs_f64() * 1e6);
                }
            }
            let (by_step, by_copy) = match action.split(':').next() {
                Some("end_turn") => ("node, one step: end_turn", "node, copy and apply: end_turn"),
                Some("potion") => ("node, one step: use a potion", "node, copy and apply: use a potion"),
                _ => ("node, one step: play a card", "node, copy and apply: play a card"),
            };
            if stepped < f64::MAX && copied < f64::MAX {
                series.entry(by_step).or_default().add(stepped);
                series.entry(by_copy).or_default().add(copied);
            }
        }

        // Arena by arena: what a copy of each costs, and whether the rest of the combat changes it.
        let text = String::from_utf8(export).unwrap();
        let mut live = Live::import(&text).unwrap();
        observation_parts(&live, &mut series);
        let mut before: BTreeMap<&'static str, String> = BTreeMap::new();
        inline = std::mem::size_of_val(live.combat());
        live.combat().a.each(&mut |name, arena| {
            before.insert(name, arena.text());
            if arena.is_empty() {
                return;
            }
            holding += 1;
            let spent = micros(20, || arena.copy());
            let (_, bytes, calls) = allocated(|| arena.copy());
            let entry = arenas.entry(name).or_default();
            entry.0 += arena.len();
            entry.1 += spent;
            entry.2 += bytes;
            entry.3 += calls;
        });
        for decision in &decisions[k..] {
            let Some(action) = decision["action"].as_str() else {
                break;
            };
            if live.apply(action).is_err() {
                break;
            }
        }
        live.combat().a.each(&mut |name, arena| {
            if before[name] == "[]" {
                return;
            }
            let bytes = arenas[name].2;
            let _ = bytes;
            if arena.text() == before[name] {
                arenas.get_mut(name).unwrap().4 += 1;
            }
        });
    }
    // Which share of a copy's bytes is in arenas that the rest of the combat left as they were, in every state.
    for (_, _, bytes, _, _) in arenas.values() {
        all_bytes += bytes;
    }
    println!(
        "{states} states, each the middle decision of a combat of {}",
        root.display()
    );
    println!(
        "a world's own size (without what it points to): {inline} bytes; arenas that hold an object: {:.1} of them a state",
        holding as f64 / states as f64
    );
    println!();
    println!("microseconds a call, kilobytes, counts:");
    for (what, unit) in [
        ("export", "KB"),
        ("import", "us"),
        ("clone", "us"),
        ("clone: allocated", "KB"),
        ("clone: allocations", ""),
        ("apply: play a card", "us"),
        ("apply: use a potion", "us"),
        ("apply: end_turn", "us"),
        ("node, copy and apply: play a card", "us"),
        ("node, one step: play a card", "us"),
        ("node, copy and apply: use a potion", "us"),
        ("node, one step: use a potion", "us"),
        ("node, copy and apply: end_turn", "us"),
        ("node, one step: end_turn", "us"),
        ("observe", "us"),
        ("observe: the views", "us"),
        ("observe: of them the cards", "us"),
        ("observe: the cards' energy cost", "us"),
        ("observe: the cards' star cost", "us"),
        ("observe: the cards' keywords", "us"),
        ("observe: the cards' replay count", "us"),
        ("observe: the cards' calculated number", "us"),
        ("observe: the cards, how many", ""),
        ("observe: JSON", "us"),
        ("observation", "KB"),
        ("legal", "us"),
        ("state key", "us"),
        ("hidden key", "us"),
    ] {
        if let Some(s) = series.get_mut(what) {
            println!("  {}", s.line(what, unit));
        }
    }
    println!();
    println!(
        "a copy, arena by arena (sums over the {states} states; `steady`: in how many of the states the rest of the combat left the arena as it was):"
    );
    let mut rows: Vec<_> = arenas.iter().collect();
    rows.sort_by_key(|r| std::cmp::Reverse(r.1.2));
    let total_time: f64 = rows.iter().map(|r| r.1.1).sum();
    println!(
        "  {:<44} {:>9} {:>10} {:>7} {:>11} {:>7} {:>8}",
        "arena", "objects", "KB", "% KB", "allocations", "% time", "steady"
    );
    for (name, (objects, time, bytes, calls, steady)) in &rows {
        if *steady == states {
            steady_bytes += bytes;
        }
        println!(
            "  {name:<44} {objects:>9} {:>10.1} {:>6.1}% {calls:>11} {:>6.1}% {steady:>5}/{states}",
            *bytes as f64 / 1024.0,
            100.0 * *bytes as f64 / all_bytes as f64,
            100.0 * time / total_time
        );
    }
    println!();
    println!(
        "arenas the rest of the combat never changed in any state hold {:.1}% of a copy's bytes ({:.1} KB of {:.1} KB a state)",
        100.0 * steady_bytes as f64 / all_bytes as f64,
        steady_bytes as f64 / 1024.0 / states as f64,
        all_bytes as f64 / 1024.0 / states as f64
    );
}
