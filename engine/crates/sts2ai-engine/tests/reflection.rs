//! The read side's own sources held against what `port` generated, with no game and no dump:
//!
//!  - where Observe.cs finds fields by reflection, observe.rs names them by class; the lists there are the ones the
//!    schema gives for this version of the game;
//!  - a virtual member of the game is called through its dispatcher, never as the base class's implementation.

mod support;

use std::collections::BTreeSet;
use support::json::{self, Json};

const SOURCES: [&str; 5] = ["observe.rs", "legal.rs", "keys.rs", "prompt.rs", "ffi.rs"];

fn source(name: &str) -> String {
    let path = format!("{}/src/{name}", env!("CARGO_MANIFEST_DIR"));
    std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{path}: {e}"))
}

fn schema() -> Json {
    let text = source("gen/schema.json");
    json::parse(&text).unwrap_or_else(|e| panic!("gen/schema.json: {e}"))
}

/// The text between `from` and `to` after each place `from` stands in `text`, where `to` follows on the same line.
fn between<'a>(text: &'a str, from: &str, to: &str) -> Vec<&'a str> {
    let mut found = Vec::new();
    let mut rest = text;
    while let Some(at) = rest.find(from) {
        rest = &rest[at + from.len()..];
        let line = rest.split('\n').next().unwrap_or("");
        if let Some(end) = line.find(to) {
            found.push(&line[..end]);
        }
    }
    found
}

fn is_name(text: &str) -> bool {
    !text.is_empty() && text.chars().all(|ch| ch.is_ascii_alphanumeric() || ch == '_')
}

/// The classes of the schema below `root`, `root` not among them, each with the class right above it.
fn below<'a>(classes: &'a [Json], root: &str) -> Vec<&'a Json> {
    let base_of = |name: &str| classes.iter().find(|c| c.member("name").text() == name).map(|c| c.member("base"));
    classes
        .iter()
        .filter(|class| {
            let mut base = class.member("base");
            loop {
                match base {
                    Json::Text(name) if name == root => return true,
                    Json::Text(name) => match base_of(name) {
                        Some(above) => base = above,
                        None => return false,
                    },
                    _ => return false,
                }
            }
        })
        .collect()
}

fn fields(class: &Json) -> impl Iterator<Item = (&str, &str, &Json)> {
    class
        .member("fields")
        .items()
        .iter()
        .map(|f| (f.member("name").text(), f.member("cs").text(), f.member("type")))
}

fn once_flag(cs: &str, type_: &Json) -> bool {
    type_.member("kind").text() == "bool" && (cs.ends_with("ThisCombat") || cs.ends_with("ThisTurn"))
}

/// Observe.Spent reads `relic.GetType().GetFields(Instance | NonPublic)` of type bool named ...ThisCombat or
/// ...ThisTurn: the fields the relic's own class declares (and the ones it inherits that are not private). observe.rs
/// lists them as (class, field) arms; the schema must give the same list, and no such field on a class above.
#[test]
fn spent_names_the_once_flags_of_the_schema() {
    let schema = schema();
    let classes = schema.member("classes").items();
    let mut expected = BTreeSet::new();
    for class in below(classes, "RelicModel") {
        let name = class.member("name").text();
        let own: Vec<&str> = fields(class)
            .filter(|(_, cs, type_)| once_flag(cs, type_))
            .map(|(field, _, _)| field)
            .collect();
        if own.is_empty() {
            continue;
        }
        assert_eq!(
            class.member("base").text(),
            "RelicModel",
            "{name} has a once-a-combat flag and is not right below RelicModel: whether reflection sees an inherited flag depends on \
             its accessibility, which the schema does not say"
        );
        assert_eq!(
            own.len(),
            1,
            "{name} has several once-a-combat flags ({own:?}): Observe.Spent is true when any is"
        );
        expected.insert(format!("{name}.{}", own[0]));
    }
    for above in ["RelicModel", "AbstractModel"] {
        let class = classes
            .iter()
            .find(|c| c.member("name").text() == above)
            .unwrap_or_else(|| panic!("no class {above} in the schema"));
        assert!(
            !fields(class).any(|(_, cs, type_)| once_flag(cs, type_)),
            "{above} itself has a once-a-combat flag"
        );
    }

    let observe = source("observe.rs");
    let body = &observe[observe.find("fn spent(").expect("observe.rs has no fn spent")..];
    let body = &body[..body.find("\n}\n").expect("fn spent has no end")];
    let listed: BTreeSet<String> = between(body, "RelicModelExt::", ",")
        .into_iter()
        .map(|arm| {
            let (class, field) = arm
                .split_once("(d) => d.")
                .unwrap_or_else(|| panic!("an arm of fn spent that is no field read: {arm}"));
            assert!(is_name(class) && is_name(field), "an arm of fn spent that is no field read: {arm}");
            format!("{class}.{field}")
        })
        .collect();
    assert_eq!(
        listed, expected,
        "observe.rs's fn spent (left) and the once-a-combat flags of the schema's relics (right)"
    );
}

/// HeldCards.Get reads the first field of type CardModel of a power's `_internalData`. observe.rs lists them as
/// (data class, field) arms; the schema must give the same list for the classes nested in a power class.
#[test]
fn held_card_names_the_card_fields_of_the_schema() {
    let schema = schema();
    let mut expected = BTreeSet::new();
    for class in schema.member("classes").items() {
        let cs = class.member("cs").text();
        let Some((outer, _)) = cs.split_once('+') else { continue };
        if !outer.starts_with("MegaCrit.Sts2.Core.Models.Powers.") {
            continue;
        }
        let cards: Vec<&str> = fields(class)
            .filter(|(_, _, type_)| type_.member("kind").text() == "ref" && type_.member("class").text() == "CardModel")
            .map(|(field, _, _)| field)
            .collect();
        if cards.is_empty() {
            continue;
        }
        let name = class.member("name").text();
        assert_eq!(
            cards.len(),
            1,
            "{name} has several fields of type CardModel ({cards:?}): HeldCards takes the first reflection lists"
        );
        expected.insert(format!("{name}Data.{}", cards[0]));
    }

    let observe = source("observe.rs");
    let body = &observe[observe.find("fn held_card(").expect("observe.rs has no fn held_card")..];
    let body = &body[..body.find("\n}\n").expect("fn held_card has no end")];
    let listed: BTreeSet<String> = body
        .lines()
        .filter_map(|line| line.split_once("=> "))
        .map(|(_, arm)| arm.trim_end_matches(','))
        .filter(|arm| *arm != "Obj::NULL")
        .map(|arm| {
            let (class, field) = arm
                .split_once("::of(data, c).")
                .unwrap_or_else(|| panic!("an arm of fn held_card that is no field read: {arm}"));
            assert!(
                is_name(class) && is_name(field),
                "an arm of fn held_card that is no field read: {arm}"
            );
            format!("{class}.{field}")
        })
        .collect();
    assert_eq!(
        listed, expected,
        "observe.rs's fn held_card (left) and the card fields of the schema's power data (right)"
    );
}

/// A member with a dispatcher in gen/dispatch.rs is virtual and has an override: `g::<area>::<class>::<member>` is then
/// only the base class's implementation. The read side must ask the dispatcher, as the game's own callers do.
#[test]
fn virtual_members_are_called_through_their_dispatchers() {
    let dispatch = source("gen/dispatch.rs");
    let mut dispatched = BTreeSet::new();
    let mut module = "";
    for line in dispatch.lines() {
        if let Some(rest) = line.strip_prefix("pub mod ") {
            module = rest.trim_end_matches(" {");
        } else if let Some(rest) = line.strip_prefix("    pub fn ") {
            let member = rest.split(['(', '<']).next().unwrap_or("");
            dispatched.insert(format!("{module}::{member}"));
        }
    }
    assert!(dispatched.len() > 100, "gen/dispatch.rs was not read as dispatchers");

    let mut wrong = Vec::new();
    let mut calls = 0;
    for name in SOURCES {
        let text = source(name);
        for call in between(&text, "g::", "(") {
            let parts: Vec<&str> = call.split("::").collect();
            if parts.len() != 3 || !parts.iter().all(|part| is_name(part)) {
                continue;
            }
            calls += 1;
            if parts[0] != "dispatch" && dispatched.contains(&format!("{}::{}", parts[1], parts[2])) {
                wrong.push(format!(
                    "{name}: g::{call} is the base implementation; g::dispatch::{}::{} is the member",
                    parts[1], parts[2]
                ));
            }
        }
    }
    assert!(calls > 100, "the read side's calls into gen were not found");
    assert!(wrong.is_empty(), "{}", wrong.join("\n"));
}
