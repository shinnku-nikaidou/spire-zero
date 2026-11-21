//! The engine held against real combats of the game, with no game there: the dump `sim parity --dump` writes (its
//! README.md has the layout). `STS2AI_ENGINE_FIXTURES` names the dump's folder; every test here fails without it.
//! Each world is used through the C ABI, as the C# side uses one.
//!
//!  - `import`: every record with an export is imported, and what the world shows is the record.
//!  - `step`: a world imported at a combat's first record takes the combat's actions, and shows each next record.
//!    Each action makes the next world from the one before (`sz_world_step`), which still shows its own record.
//!  - `continued`: the same from the world imported at every record, to the combat's end, each action taken in place
//!    (`sz_world_apply`).
//!  - `sampled`: every record with an export imported as a NoSL sample tells its state apart by `HiddenState.Key`, the
//!    first line of the record's hidden.txt, and so does the world after its action.
//!  - `begin`: a combat written out as it began (opening.export.json) begun in the engine (`sz_world_begin`), its
//!    start's prompts answered as the game's were, shows the combat's first record, and is carried through the combat.

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
        World::import_as(export, 0)
    }

    /// A world from a combat written out as it began (`sz_world_begin`).
    fn begin(export: &[u8]) -> Result<World, String> {
        match unsafe { ffi::sz_world_begin(export.as_ptr(), export.len()) } {
            0 => Err(format!("error: sz_world_begin: {}", last_error())),
            world => Ok(World(world)),
        }
    }

    /// `sample`: 1 for a NoSL sample, 0 for the SL world (`sz_world_import`).
    fn import_as(export: &[u8], sample: i32) -> Result<World, String> {
        match unsafe { ffi::sz_world_import(export.as_ptr(), export.len(), sample) } {
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

    /// The world after the action, as `sz_world_step` makes it of this one, held against what it says of itself.
    fn step(&self, action: &str) -> Result<World, String> {
        let mut node = ffi::SzNode::default();
        let after = match unsafe { ffi::sz_world_step(self.0, action.as_ptr(), action.len(), &mut node) } {
            0 => return Err(format!("error: sz_world_step: {}", last_error())),
            world => World(world),
        };
        let status = after.status()?;
        let (state_key, hidden_key) = if status == 0 {
            (
                after.key("sz_world_state_key", ffi::sz_world_state_key)?,
                after.key("sz_world_hidden_key", ffi::sz_world_hidden_key)?,
            )
        } else {
            (0, 0)
        };
        let (round, prompt) = if status == 2 {
            // A lost combat's observation is not asked for.
            (node.round, node.prompt)
        } else {
            let observation: serde_json::Value =
                serde_json::from_slice(&after.text("sz_world_observe", ffi::sz_world_observe)?).map_err(|e| e.to_string())?;
            (
                observation["round"].as_i64().unwrap_or(-1) as i32,
                !observation["prompt"].is_null() as i32,
            )
        };
        let own = ffi::SzNode {
            status,
            round,
            prompt,
            state_key,
            hidden_key,
        };
        if node != own {
            return Err(format!("sz_world_step's node: {node:?} != {own:?}"));
        }
        Ok(after)
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

/// How a world takes an action: in place, or by making the next world of it.
#[derive(Clone, Copy, PartialEq)]
enum By {
    Apply,
    Step,
}

/// Plays the world from record `from` along the combat's actions, and holds it against each next record. Returns how
/// many records it showed before the first that differs. By `Step`, the world before an action is held against its
/// own record once more after the action: it stays as it is.
fn follow(mut world: World, fixture: &Fixture, from: usize, tally: &mut Tally, what: &str, by: By) {
    for k in from..fixture.records.len() - 1 {
        let action = fixture.records[k]
            .action
            .as_deref()
            .expect("only the last record has no action");
        let step = match by {
            By::Apply => world.apply(action),
            By::Step => world.step(action).and_then(|after| {
                let before = std::mem::replace(&mut world, after);
                shows(&before, fixture, k).map_err(|difference| format!("the world before the action, {difference}"))
            }),
        }
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

/// C#: ProtocolJson.Hash of a string, the hash of the text as a JSON string.
fn hash_of_text(text: &str) -> u64 {
    let mut hash = sts2ai_engine::protocol::Fnv::new();
    sts2ai_engine::protocol::string(text, &mut hash);
    hash.0
}

/// A NoSL sample's hidden key at record `k`: `HiddenState.Key`, the hash of hidden.txt's first line.
fn sample_key(world: &World, fixture: &Fixture, k: usize) -> Result<(), String> {
    if fixture.records[k].keys.is_none() {
        return Ok(());
    }
    let hidden = String::from_utf8(fixture.bytes(k, "hidden.txt")).map_err(|e| e.to_string())?;
    let expected = hash_of_text(hidden.split('\n').next().unwrap_or(""));
    let key = world.key("sz_world_hidden_key", ffi::sz_world_hidden_key)?;
    if key != expected {
        return Err(format!("sample hidden_key: {expected} != {key}"));
    }
    Ok(())
}

/// The sampled check: each export imported as a NoSL sample has `HiddenState.Key` for its hidden key, and so has the
/// world its action makes (a clone and a step keep the kind of world), where the next record has an export too.
#[test]
fn sampled() {
    sweep(|fixture| {
        let mut tally = Tally::default();
        for k in 0..fixture.records.len() {
            if !fixture.has_export(k) {
                tally.skipped += 1;
                continue;
            }
            let checked = World::import_as(&fixture.bytes(k, "export.json"), 1).and_then(|world| {
                sample_key(&world, fixture, k)?;
                let copy = world.try_clone()?;
                sample_key(&copy, fixture, k)?;
                match fixture.records[k].action.as_deref() {
                    Some(action) if fixture.records[k + 1].status == 0 => sample_key(&copy.step(action)?, fixture, k + 1),
                    _ => Ok(()),
                }
            });
            match checked {
                Ok(()) => tally.passed += 1,
                Err(difference) => tally
                    .failed
                    .push((format!("{} record {k}", fixture.name), difference)),
            }
        }
        tally
    })
    .finish("sampled");
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
            Ok(world) => follow(world, fixture, first, &mut tally, "carried", By::Step),
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
                Ok(world) => follow(world, fixture, k, &mut tally, "continued", By::Apply),
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

/// The begin check: a combat written out as it began (opening.export.json, in the game's
/// StartCombatInternal once the executor has finished what it ran) begun in the engine (`sz_world_begin`), the card prompts of its start answered as the game's
/// were (opening.answers.json, places in the game's lists: `given:`), shows the combat's first record, and is carried
/// from there through the whole combat as the step check carries its world. A combat with no opening is skipped.
#[test]
fn begin() {
    sweep(|fixture| {
        let mut tally = Tally::default();
        let Ok(export) = std::fs::read(fixture.folder.join("opening.export.json")) else {
            tally.skipped += fixture.records.len();
            return tally;
        };
        let answers: Vec<Vec<i32>> = match std::fs::read(fixture.folder.join("opening.answers.json")) {
            Ok(text) => serde_json::from_slice(&text).expect("opening.answers.json"),
            Err(_) => Vec::new(),
        };
        let begun = World::begin(&export).and_then(|mut world| {
            for answer in &answers {
                let given = format!("given:{}", answer.iter().map(|i| i.to_string()).collect::<Vec<_>>().join(","));
                world = world.step(&given)?;
            }
            shows(&world, fixture, 0).map(|()| world)
        });
        match begun {
            Ok(world) => {
                tally.passed += 1;
                follow(world, fixture, 0, &mut tally, "begun", By::Step);
            }
            Err(difference) => tally
                .failed
                .push((format!("{} record 0 (begun, {} answers)", fixture.name, answers.len()), difference)),
        }
        tally
    })
    .finish("begin");
}
