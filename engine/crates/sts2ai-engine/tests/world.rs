//! Combats of the real game, replayed in the engine: import a decision of a dump (`sim parity --dump`, the folder
//! STS2AI_ENGINE_FIXTURES names), give the engine the actions the game was given, and it must go the same way.

use std::path::PathBuf;

fn combats() -> Vec<PathBuf> {
    let root = std::env::var_os("STS2AI_ENGINE_FIXTURES")
        .expect("STS2AI_ENGINE_FIXTURES is not set: it names the folder `sim parity --dump` wrote");
    let mut folders: Vec<PathBuf> = std::fs::read_dir(&root)
        .expect("the fixtures folder")
        .map(|e| e.unwrap().path())
        .filter(|p| p.is_dir())
        .collect();
    folders.sort();
    assert!(!folders.is_empty(), "no combat in the fixtures folder");
    folders
}

/// Every export of the dump is read.
#[test]
fn every_export_is_read() {
    let mut read = 0;
    for folder in combats() {
        for k in 0.. {
            let path = folder.join(format!("{k}.export.json"));
            if !path.exists() {
                if folder.join(format!("{k}.observation.json")).exists() {
                    continue;
                }
                break;
            }
            let text = std::fs::read_to_string(&path).unwrap();
            if let Err(e) = sts2ai_engine::import::import(&text) {
                panic!("{}: {e}", path.display());
            }
            read += 1;
        }
    }
    assert!(read > 0);
    eprintln!("{read} exports read");
}

/// The decisions of a combat of the dump: the action the game was given at each (None at the last).
fn actions(folder: &std::path::Path) -> Vec<Option<String>> {
    let text = std::fs::read_to_string(folder.join("combat.json")).unwrap();
    let doc: serde_json::Value = serde_json::from_str(&text).unwrap();
    doc["decisions"]
        .as_array()
        .unwrap()
        .iter()
        .map(|d| d["action"].as_str().map(str::to_owned))
        .collect()
}

/// Every combat of the dump is played through from its first decision with the actions the game was given: no action
/// is refused and nothing the engine lacks is reached. What stops a combat is counted by its message.
#[test]
fn every_combat_is_played_through() {
    use std::collections::BTreeMap;
    use sts2ai_engine::ffi::World;
    let only = std::env::var("STS2AI_ENGINE_ONLY").ok();
    let mut stopped: BTreeMap<String, (usize, String)> = BTreeMap::new();
    let (mut played, mut whole, mut steps) = (0, 0, 0);
    // One combat asked for by name is looked at closely: its panic is printed with where it came from.
    if only.is_none() {
        std::panic::set_hook(Box::new(|_| {}));
    }
    for folder in combats() {
        let name = folder.file_name().unwrap().to_string_lossy().to_string();
        if only.as_ref().is_some_and(|o| !name.contains(o.as_str())) {
            continue;
        }
        played += 1;
        let actions = actions(&folder);
        let export = std::fs::read_to_string(folder.join("0.export.json")).unwrap();
        let text_of = |panic: Box<dyn std::any::Any + Send>| {
            panic
                .downcast_ref::<String>()
                .cloned()
                .or(panic.downcast_ref::<&str>().map(|s| s.to_string()))
                .unwrap_or_default()
        };
        let result = std::panic::catch_unwind(|| {
            // A panic while the export is read (a member the import runs that is not translated) stops the combat too.
            let mut world = match std::panic::catch_unwind(|| sts2ai_engine::world::Live::import(&export)) {
                Ok(Ok(world)) => world,
                Ok(Err(e)) => return Err((0, format!("import: {e}"))),
                Err(panic) => return Err((0, format!("import: panic: {}", text_of(panic)))),
            };
            for (k, action) in actions.iter().enumerate() {
                let Some(action) = action else { break };
                let step =
                    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| world.apply(action)));
                match step {
                    Ok(Ok(())) => {}
                    Ok(Err(e)) => return Err((k, format!("refused: {e}"))),
                    Err(panic) => return Err((k, format!("panic: {}", text_of(panic)))),
                }
            }
            Ok::<usize, (usize, String)>(actions.len() - 1)
        });
        match result {
            Ok(Ok(n)) => {
                whole += 1;
                steps += n;
            }
            Ok(Err((k, message))) => {
                steps += k;
                let key: String = message.chars().take(200).collect();
                let entry = stopped
                    .entry(key)
                    .or_insert((0, format!("{name} at decision {k}")));
                entry.0 += 1;
            }
            Err(_) => panic!("{name}: the test itself failed"),
        }
    }
    let _ = std::panic::take_hook();
    let mut by_count: Vec<_> = stopped.iter().collect();
    by_count.sort_by(|a, b| b.1.0.cmp(&a.1.0));
    eprintln!("{whole} of {played} combats played through, {steps} actions taken");
    for (message, (count, first)) in by_count {
        eprintln!("x{count}: {message} [first: {first}]");
    }
    assert_eq!(whole, played, "combats that stopped");
}
