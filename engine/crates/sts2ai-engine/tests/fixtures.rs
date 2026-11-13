//! The engine held against real combats of the game, with no game there: the dump `sim parity --dump` writes (its
//! README.md has the layout). `STS2AI_ENGINE_FIXTURES` names the dump's folder; every test here fails without it.
//! Each world is used through the C ABI, as the C# side uses one.
//!
//!  - `import`: every record with an export is imported, and what the world shows is the record.
//!  - `step`: a world imported at a combat's first record takes the combat's actions, and shows each next record.
//!  - `continued`: the same from the world imported at every record, to the combat's end.

mod support;

use sts2ai_engine::ffi;
use support::dump::{Fixture, Tally, json_difference, sweep};

// ---------------------------------------------------------------------------------------------------------------------
// A world, through the C ABI
// ---------------------------------------------------------------------------------------------------------------------

/// A world of the engine, as the C# side holds one.
struct World(u64);

fn last_error() -> String {
    let mut buf = ffi::SzBuf::EMPTY;
    assert_eq!(unsafe { ffi::sz_last_error(&mut buf) }, 0, "sz_last_error");
    String::from_utf8_lossy(&take(buf)).into_owned()
}

fn take(buf: ffi::SzBuf) -> Vec<u8> {
    let bytes = unsafe { std::slice::from_raw_parts(buf.ptr, buf.len) }.to_vec();
    unsafe { ffi::sz_buf_free(buf) };
    bytes
}

impl World {
    fn import(export: &[u8]) -> Result<World, String> {
        match unsafe { ffi::sz_world_import(export.as_ptr(), export.len()) } {
            0 => Err(format!("error: sz_world_import: {}", last_error())),
            world => Ok(World(world)),
        }
    }

    fn try_clone(&self) -> Result<World, String> {
        match unsafe { ffi::sz_world_clone(self.0) } {
            0 => Err(format!("error: sz_world_clone: {}", last_error())),
            world => Ok(World(world)),
        }
    }

    fn apply(&mut self, action: &str) -> Result<(), String> {
        match unsafe { ffi::sz_world_apply(self.0, action.as_ptr(), action.len()) } {
            0 => Ok(()),
            _ => Err(format!("error: sz_world_apply: {}", last_error())),
        }
    }

    fn status(&self) -> Result<i32, String> {
        match unsafe { ffi::sz_world_status(self.0) } {
            status @ 0..=2 => Ok(status),
            _ => Err(format!("error: sz_world_status: {}", last_error())),
        }
    }

    fn text(
        &self,
        name: &str,
        call: unsafe extern "C" fn(u64, *mut ffi::SzBuf) -> i32,
    ) -> Result<Vec<u8>, String> {
        let mut buf = ffi::SzBuf::EMPTY;
        match unsafe { call(self.0, &mut buf) } {
            0 => Ok(take(buf)),
            _ => Err(format!("error: {name}: {}", last_error())),
        }
    }

    /// A key, which has no way to say it failed but the thread's error: 0 with an error is a failure.
    fn key(&self, name: &str, call: unsafe extern "C" fn(u64) -> u64) -> Result<u64, String> {
        match unsafe { call(self.0) } {
            0 if !last_error().is_empty() => Err(format!("error: {name}: {}", last_error())),
            key => Ok(key),
        }
    }
}

impl Drop for World {
    fn drop(&mut self) {
        unsafe { ffi::sz_world_free(self.0) };
    }
}

/// Whether the world shows record `k`: the first thing that differs, in the order the lockstep check reports
/// (observation, plays, status), then the run and the two keys. A lost combat is compared by its status alone.
fn shows(world: &World, fixture: &Fixture, k: usize) -> Result<(), String> {
    let record = &fixture.records[k];
    let status = world.status()?;
    if record.status == 2 {
        return if status == 2 {
            Ok(())
        } else {
            Err(format!("status: 2 != {status}"))
        };
    }
    let observation = world.text("sz_world_observe", ffi::sz_world_observe)?;
    if let Some(difference) = json_difference(&fixture.bytes(k, "observation.json"), &observation) {
        return Err(format!("observation{difference}"));
    }
    let legal = world.text("sz_world_legal", ffi::sz_world_legal)?;
    if legal != record.legal.as_bytes() {
        let (expected, actual): (Vec<&str>, String) = (
            record.legal.lines().collect(),
            String::from_utf8_lossy(&legal).into_owned(),
        );
        let actual: Vec<&str> = actual.lines().collect();
        let at = expected
            .iter()
            .zip(&actual)
            .position(|(a, b)| a != b)
            .unwrap_or(expected.len().min(actual.len()));
        return Err(format!(
            "legal[{at}]: {:?} != {:?}",
            expected.get(at),
            actual.get(at)
        ));
    }
    if status != record.status {
        return Err(format!("status: {} != {status}", record.status));
    }
    let run = world.text("sz_world_run", ffi::sz_world_run)?;
    if let Some(difference) = json_difference(&fixture.bytes(k, "run.json"), &run) {
        return Err(format!("run{difference}"));
    }
    if let Some((hidden_key, state_key)) = record.keys {
        let key = world.key("sz_world_state_key", ffi::sz_world_state_key)?;
        if key != state_key {
            return Err(format!("state_key: {state_key} != {key}"));
        }
        let key = world.key("sz_world_hidden_key", ffi::sz_world_hidden_key)?;
        if key != hidden_key {
            return Err(format!("hidden_key: {hidden_key} != {key}"));
        }
    }
    Ok(())
}

/// Plays the world from record `from` along the combat's actions, and holds it against each next record. Returns how
/// many records it showed before the first that differs.
fn follow(mut world: World, fixture: &Fixture, from: usize, tally: &mut Tally, what: &str) {
    for k in from..fixture.records.len() - 1 {
        let action = fixture.records[k]
            .action
            .as_deref()
            .expect("only the last record has no action");
        let step = world
            .apply(action)
            .and_then(|()| shows(&world, fixture, k + 1));
        match step {
            Ok(()) => tally.passed += 1,
            Err(difference) => {
                tally.failed.push((
                    format!(
                        "{} record {} ({what} from {from}, after {action})",
                        fixture.name,
                        k + 1
                    ),
                    difference,
                ));
                return;
            }
        }
    }
}

/// The import check: at every record with an export, the world imported from it shows the record. A record with no
/// export (a card prompt, where the engine takes no combat in, or the combat's end) is skipped, and counted so.
#[test]
fn import() {
    sweep(|fixture| {
        let mut tally = Tally::default();
        for k in 0..fixture.records.len() {
            if !fixture.has_export(k) {
                tally.skipped += 1;
                continue;
            }
            match World::import(&fixture.bytes(k, "export.json"))
                .and_then(|world| shows(&world, fixture, k))
            {
                Ok(()) => tally.passed += 1,
                Err(difference) => tally
                    .failed
                    .push((format!("{} record {k}", fixture.name), difference)),
            }
        }
        tally
    })
    .finish("import");
}

/// The step check: the world imported at a combat's first record is carried through the whole combat, card prompts
/// included, and shows every record after each action. A combat that opens on a card prompt (a relic that asks before
/// the first hand) has no export there: its world is imported at the first record that has one, and the records
/// before it are counted as skipped.
#[test]
fn step() {
    sweep(|fixture| {
        let mut tally = Tally::default();
        let Some(first) = (0..fixture.records.len()).find(|&k| fixture.has_export(k)) else {
            tally.skipped += fixture.records.len();
            return tally;
        };
        tally.skipped += first;
        match World::import(&fixture.bytes(first, "export.json")) {
            Ok(world) => follow(world, fixture, first, &mut tally, "carried"),
            Err(difference) => tally
                .failed
                .push((format!("{} record {first}", fixture.name), difference)),
        }
        tally
    })
    .finish("step");
}

/// The continue check: the world imported at each record plays the rest of the combat, which shows what an export
/// left out of the state. Each one is a clone of the imported world, as search copies a world before it acts in it.
#[test]
fn continued() {
    sweep(|fixture| {
        let mut tally = Tally::default();
        for k in (0..fixture.records.len() - 1).filter(|&k| fixture.has_export(k)) {
            match World::import(&fixture.bytes(k, "export.json"))
                .and_then(|world| world.try_clone())
            {
                Ok(world) => follow(world, fixture, k, &mut tally, "continued"),
                Err(difference) => tally
                    .failed
                    .push((format!("{} record {k}", fixture.name), difference)),
            }
        }
        tally
    })
    .finish("continued");
}

/// Not a check: what stops each call of the C ABI on the dump's exports, by what the call said and how many exports
/// it stopped on. The list of what the read side still waits for.
///
///     cargo test --test fixtures blockers -- --ignored --nocapture
#[test]
#[ignore = "a survey, run by hand"]
fn blockers() {
    let said = std::sync::Mutex::new(std::collections::BTreeMap::<String, usize>::new());
    sweep(|fixture| {
        let mut tally = Tally::default();
        for k in (0..fixture.records.len()).filter(|&k| fixture.has_export(k)) {
            let mut errors = Vec::new();
            match World::import(&fixture.bytes(k, "export.json")) {
                Err(error) => errors.push(error),
                Ok(world) => {
                    errors.extend(world.status().err());
                    errors.extend(world.text("sz_world_legal", ffi::sz_world_legal).err());
                    errors.extend(world.text("sz_world_observe", ffi::sz_world_observe).err());
                    errors.extend(world.text("sz_world_run", ffi::sz_world_run).err());
                    errors.extend(
                        world
                            .key("sz_world_state_key", ffi::sz_world_state_key)
                            .err(),
                    );
                    errors.extend(
                        world
                            .key("sz_world_hidden_key", ffi::sz_world_hidden_key)
                            .err(),
                    );
                }
            }
            let mut said = said.lock().unwrap();
            for error in errors {
                *said.entry(error).or_default() += 1;
            }
            tally.passed += 1;
        }
        tally
    });
    let said = said.into_inner().unwrap();
    println!("{} kinds of error", said.len());
    for (error, exports) in said {
        println!("{exports:7}  {error}");
    }
}
