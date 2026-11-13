//! The dump `sim parity --dump` writes (its README.md has the layout), and what a check of it found.

use super::json::{self, Json};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

// ---------------------------------------------------------------------------------------------------------------------
// The dump
// ---------------------------------------------------------------------------------------------------------------------

/// One record of a combat: a place where the player is asked, or the combat's end.
pub struct Record {
    pub status: i32,
    pub prompt: bool,
    /// The plays as `sz_world_legal` writes them.
    pub legal: String,
    /// None once the combat is over: search asks for neither key then.
    pub keys: Option<(u64, u64)>,
    /// None at the last record.
    pub action: Option<String>,
}

pub struct Fixture {
    pub name: String,
    pub folder: PathBuf,
    pub records: Vec<Record>,
}

impl Fixture {
    pub fn file(&self, k: usize, what: &str) -> PathBuf {
        self.folder.join(format!("{k}.{what}"))
    }

    pub fn bytes(&self, k: usize, what: &str) -> Vec<u8> {
        let path = self.file(k, what);
        std::fs::read(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
    }

    pub fn has_export(&self, k: usize) -> bool {
        self.file(k, "export.json").exists()
    }
}

fn dump() -> PathBuf {
    let Some(folder) = std::env::var_os("STS2AI_ENGINE_FIXTURES") else {
        panic!(
            "STS2AI_ENGINE_FIXTURES is not set: it names the folder `sim parity --dump` wrote (tmp/engine/fixtures), \
             and without it nothing is checked"
        );
    };
    let folder = PathBuf::from(folder);
    assert!(
        folder.is_dir(),
        "STS2AI_ENGINE_FIXTURES names {}, which is no folder",
        folder.display()
    );
    folder
}

fn fixtures() -> Vec<Fixture> {
    let root = dump();
    let mut folders: Vec<PathBuf> = std::fs::read_dir(&root)
        .unwrap_or_else(|e| panic!("{}: {e}", root.display()))
        .map(|entry| entry.expect("a folder's entry").path())
        .filter(|path| path.is_dir())
        .collect();
    folders.sort();
    assert!(!folders.is_empty(), "{} holds no combat", root.display());
    folders.iter().map(|folder| fixture(folder)).collect()
}

fn fixture(folder: &Path) -> Fixture {
    let path = folder.join("combat.json");
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    let combat = json::parse(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    assert_eq!(combat.member("format").number(), "1", "{}: the dump's format", path.display());
    let records = combat
        .member("decisions")
        .items()
        .iter()
        .map(|d| Record {
            status: match d.member("status").text() {
                "in_progress" => 0,
                "won" => 1,
                "lost" => 2,
                other => panic!("{}: status {other}", path.display()),
            },
            prompt: d.get("prompt").is_some_and(|p| *p == Json::Bool(true)),
            legal: d.member("legal").items().iter().map(|play| format!("{}\n", play.text())).collect(),
            keys: d.get("hidden_key").map(|hidden| (u64_of(hidden), u64_of(d.member("state_key")))),
            action: d.get("action").map(|a| a.text().to_string()),
        })
        .collect();
    Fixture {
        name: folder.file_name().expect("a folder's name").to_string_lossy().into_owned(),
        folder: folder.to_path_buf(),
        records,
    }
}

fn u64_of(number: &Json) -> u64 {
    number
        .number()
        .parse()
        .unwrap_or_else(|_| panic!("not a 64-bit number: {}", number.number()))
}

// ---------------------------------------------------------------------------------------------------------------------
// What a check found
// ---------------------------------------------------------------------------------------------------------------------

/// The checks of one test: how many records passed, and what the others differ in.
#[derive(Default)]
pub struct Tally {
    pub passed: usize,
    /// Records the check does not apply to (no export to import: a card prompt, the combat's end).
    pub skipped: usize,
    /// "<fixture> record <k>[ after ..]: <what differs>", in the order found.
    pub failed: Vec<(String, String)>,
}

impl Tally {
    pub fn add(&mut self, other: Tally) {
        self.passed += other.passed;
        self.skipped += other.skipped;
        self.failed.extend(other.failed);
    }

    /// Panics if any record failed, with the first failures and the kinds of difference by how many records have them.
    pub fn finish(self, what: &str) {
        let counts = format!(
            "{what}: {} passed, {} failed, {} skipped",
            self.passed,
            self.failed.len(),
            self.skipped
        );
        println!("{counts}");
        if self.failed.is_empty() {
            assert!(self.passed > 0, "{what}: the dump has no record to check");
            return;
        }
        let mut kinds: BTreeMap<String, usize> = BTreeMap::new();
        for (_, difference) in &self.failed {
            *kinds.entry(kind_of(difference)).or_default() += 1;
        }
        let mut kinds: Vec<(String, usize)> = kinds.into_iter().collect();
        kinds.sort_by(|a, b| b.1.cmp(&a.1));
        let mut report = format!("{counts}\n");
        for (kind, count) in kinds.iter().take(40) {
            report.push_str(&format!("{count:7}  {kind}\n"));
        }
        report.push_str("first failures:\n");
        for (at, difference) in self.failed.iter().take(10) {
            report.push_str(&format!("  {at}: {difference}\n"));
        }
        panic!("{report}");
    }
}

/// A difference without its values and indexes, so that the same kind of difference counts as one: a JSON path with
/// no index ("observation.hand[].cost"), or what a failed call said.
fn kind_of(difference: &str) -> String {
    if difference.starts_with("error: ") {
        return difference.chars().take(200).collect();
    }
    let end = difference.find(": ").unwrap_or(difference.len());
    let mut kind = String::new();
    let mut index = false;
    for ch in difference[..end].chars().take(160) {
        match ch {
            '[' => index = true,
            ']' => index = false,
            _ if index => continue,
            _ => {}
        }
        kind.push(ch);
    }
    kind
}

/// Runs `check` on every combat of the dump, in as many threads as the machine has: worlds share nothing.
pub fn sweep(check: impl Fn(&Fixture) -> Tally + Sync) -> Tally {
    let fixtures = fixtures();
    let next = Mutex::new(0usize);
    let total = Mutex::new(Tally::default());
    let threads = std::thread::available_parallelism().map_or(4, |n| n.get());
    std::thread::scope(|scope| {
        for _ in 0..threads {
            scope.spawn(|| {
                loop {
                    let at = {
                        let mut next = next.lock().unwrap();
                        *next += 1;
                        *next - 1
                    };
                    let Some(fixture) = fixtures.get(at) else { return };
                    let tally = check(fixture);
                    total.lock().unwrap().add(tally);
                }
            });
        }
    });
    let mut total = total.into_inner().unwrap();
    total.failed.sort();
    total
}

/// Where two JSON texts first differ, to go after the text's name: ".path[3].to: expected != actual" in the tree, or
/// the byte where trees that are equal are written apart. None for the same bytes.
pub fn json_difference(expected: &[u8], actual: &[u8]) -> Option<String> {
    if expected == actual {
        return None;
    }
    let read = |bytes: &[u8]| std::str::from_utf8(bytes).map_err(|e| e.to_string()).and_then(json::parse);
    let expected_tree = read(expected).unwrap_or_else(|e| panic!("the dump's own JSON does not read: {e}"));
    let actual_tree = match read(actual) {
        Ok(tree) => tree,
        Err(e) => return Some(format!(" is not JSON: {e}")),
    };
    if let Some(path) = json::difference(&expected_tree, &actual_tree, "") {
        return Some(path);
    }
    let at = expected
        .iter()
        .zip(actual)
        .position(|(a, b)| a != b)
        .unwrap_or(expected.len().min(actual.len()));
    let around =
        |bytes: &[u8]| String::from_utf8_lossy(&bytes[at.saturating_sub(20).min(bytes.len())..(at + 20).min(bytes.len())]).into_owned();
    Some(format!(
        " is the same tree written apart at byte {at}: {:?} != {:?}",
        around(expected),
        around(actual)
    ))
}
