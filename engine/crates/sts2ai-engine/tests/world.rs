//! Combats of the real game, replayed in the engine: import a decision of a dump (`sim parity --dump`, the folder
//! STS2AI_ENGINE_FIXTURES names), give the engine the actions the game was given, and it must go the same way.

use std::path::PathBuf;

fn combats() -> Vec<PathBuf> {
    let root = std::env::var_os("STS2AI_ENGINE_FIXTURES")
        .expect("STS2AI_ENGINE_FIXTURES is not set: it names the folder `sim parity --dump` wrote");
    let mut folders: Vec<PathBuf> =
        std::fs::read_dir(&root).expect("the fixtures folder").map(|e| e.unwrap().path()).filter(|p| p.is_dir()).collect();
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
