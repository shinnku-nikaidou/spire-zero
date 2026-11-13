//! The engine's JSON writer and its hashes, held against real combats of the game with no game and no engine there:
//! every record of the dump `sim parity --dump` writes (its README.md has the layout) is read into the protocol's
//! records, written back, and hashed. `STS2AI_ENGINE_FIXTURES` names the dump's folder; the test fails without it.

mod support;

use sts2ai_engine::protocol::{self, Fnv};
use support::dump::{Fixture, Tally, json_difference, sweep};
use support::{json, views};

fn fnv_of_text(text: &str) -> u64 {
    let mut hash = Fnv::new();
    protocol::string(text, &mut hash);
    hash.0
}

fn protocol_record(fixture: &Fixture, k: usize) -> Result<(), String> {
    let record = &fixture.records[k];
    let bytes = fixture.bytes(k, "observation.json");
    let tree = json::parse(std::str::from_utf8(&bytes).map_err(|e| e.to_string())?)?;
    let observation = views::observation(&tree)?;
    let mut written = Vec::new();
    protocol::observation_json(&observation, &mut written);
    if let Some(difference) = json_difference(&bytes, &written) {
        return Err(format!("observation written back{difference}"));
    }
    if observation.prompt.is_some() != record.prompt {
        return Err("prompt: combat.json and the observation disagree".into());
    }

    let bytes = fixture.bytes(k, "run.json");
    let run = views::run(&json::parse(std::str::from_utf8(&bytes).map_err(|e| e.to_string())?)?)?;
    let mut written = Vec::new();
    protocol::run_json(&run, &mut written);
    if let Some(difference) = json_difference(&bytes, &written) {
        return Err(format!("run written back{difference}"));
    }

    let Some((hidden_key, state_key)) = record.keys else {
        return Ok(());
    };
    let key = protocol::state_key(&observation);
    if key != state_key {
        return Err(format!("state_key: {state_key} != {key}"));
    }
    let hidden = String::from_utf8(fixture.bytes(k, "hidden.txt")).map_err(|e| e.to_string())?;
    let mut lines = hidden.split('\n');
    let (first, full) = (lines.next().unwrap_or(""), lines.next().ok_or("hidden.txt has one line")?);
    if fnv_of_text(full) != hidden_key {
        return Err(format!(
            "hidden_key: {hidden_key} != {} (the hash of hidden.txt's second line)",
            fnv_of_text(full)
        ));
    }
    if !full.starts_with(&format!("{}#", fnv_of_text(first))) {
        return Err("hidden.txt: the second line does not start with the hash of the first".into());
    }
    Ok(())
}

/// The JSON writer, the state key and the hash of a text, against every record of the dump: an observation read into
/// the protocol's records writes back to its own bytes, and hashes to the keys the game's side computed.
#[test]
fn protocol() {
    sweep(|fixture| {
        let mut tally = Tally::default();
        for k in 0..fixture.records.len() {
            match protocol_record(fixture, k) {
                Ok(()) => tally.passed += 1,
                Err(difference) => tally.failed.push((format!("{} record {k}", fixture.name), difference)),
            }
        }
        tally
    })
    .finish("protocol");
}

/// The text of a JSON string hashes as `ProtocolJson.Hash` of a C# string does: values of .NET 9 itself.
#[test]
fn text_hash_is_dotnets() {
    for (text, hash) in [
        ("", 561953828980956709u64),
        ("a", 15287227144955461354),
        ("{\"id\":\"X\"}|1,2,3:MOVE,", 15508434510772812715),
        ("\u{e9}+<>&'`\\\n", 3700537252188539741),
    ] {
        assert_eq!(fnv_of_text(text), hash, "{text:?}");
    }
    let mut out = Vec::new();
    protocol::string(
        "\u{0}\u{8}\t\n\u{b}\u{c}\r\u{1f} \"&'+<>\\`\u{7f}\u{80}\u{4e2d}\u{1f600}~",
        &mut out,
    );
    assert_eq!(
        String::from_utf8(out).unwrap(),
        r#""\u0000\b\t\n\u000B\f\r\u001F \u0022\u0026\u0027\u002B\u003C\u003E\\\u0060\u007F\u0080\u4E2D\uD83D\uDE00~""#
    );
}

/// Not a check: what writing an observation and hashing it to the state key cost, per observation of the dump, once
/// the observation has been read off the combat.
///
///     cargo test --release --test protocol costs -- --ignored --nocapture
#[test]
#[ignore = "a measurement, run by hand"]
fn costs() {
    let observations = std::sync::Mutex::new(Vec::new());
    sweep(|fixture| {
        for k in 0..fixture.records.len() {
            let bytes = fixture.bytes(k, "observation.json");
            let tree = json::parse(std::str::from_utf8(&bytes).unwrap()).unwrap();
            observations.lock().unwrap().push((views::observation(&tree).unwrap(), bytes.len()));
        }
        Tally::default()
    });
    let observations = observations.into_inner().unwrap();
    // Each observation many times in a row, as search asks of the one it stands at.
    let rounds = 200;
    let observations = &observations[..observations.len().min(500)];
    let started = std::time::Instant::now();
    let mut written = 0;
    let mut json = Vec::with_capacity(8192);
    for (observation, _) in observations {
        for _ in 0..rounds {
            json.clear();
            protocol::observation_json(observation, &mut json);
            written += json.len();
        }
    }
    let write = started.elapsed();
    let started = std::time::Instant::now();
    let mut mixed = 0u64;
    for (observation, _) in observations {
        for _ in 0..rounds {
            mixed = mixed.wrapping_add(protocol::state_key(observation));
        }
    }
    let key = started.elapsed();
    let calls = (rounds * observations.len()) as f64;
    println!(
        "{} observations, {:.0} bytes each: writing one {:.2} us, its state key {:.2} us ({mixed:x})",
        observations.len(),
        written as f64 / calls,
        write.as_secs_f64() * 1e6 / calls,
        key.as_secs_f64() * 1e6 / calls
    );
}
