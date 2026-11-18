//! What the C# side writes for a search, read (ProtocolJson's form): a record of enemy moves, and a turn's search
//! asked for. And the turn's search found, written back in the form C# reads a `TurnSearch` in.

use serde::Deserialize;

use super::beam::{Budget, Found};
use super::bound::Bounds;
use super::state::{Action, History, Status};
use super::turn::{TurnEnd, TurnSearch};
use crate::protocol::{self, EnemyTurn, EnemyTurns, IntentView, Sink};
use crate::rt::Str;

#[derive(Deserialize)]
struct IntentJson {
    #[serde(rename = "type")]
    type_: String,
    damage: Option<i32>,
    hits: Option<i32>,
}

#[derive(Deserialize)]
struct TurnJson {
    round: i32,
    intents: Vec<IntentJson>,
}

#[derive(Deserialize)]
struct EnemyJson {
    uid: u32,
    id: Option<String>,
    turns: Vec<TurnJson>,
}

fn str(s: String) -> Str {
    Str::from_string(s)
}

/// C#: ProtocolJson.Deserialize of a combat's record of enemy moves (`IReadOnlyList<EnemyTurns>`).
pub fn history(json: &[u8]) -> Result<History, String> {
    let enemies: Vec<EnemyJson> = serde_json::from_slice(json).map_err(|e| format!("a record of enemy moves: {e}"))?;
    Ok(record(enemies))
}

fn record(enemies: Vec<EnemyJson>) -> History {
    enemies
        .into_iter()
        .map(|e| EnemyTurns {
            uid: e.uid,
            id: e.id.map(str),
            turns: e
                .turns
                .into_iter()
                .map(|t| EnemyTurn {
                    round: t.round,
                    intents: t
                        .intents
                        .into_iter()
                        .map(|i| IntentView {
                            type_: str(i.type_),
                            damage: i.damage,
                            hits: i.hits,
                        })
                        .collect(),
                })
                .collect(),
        })
        .collect()
}

/// A turn's search asked for (C#: PlanJob, with the nodes and the states kept).
pub struct TurnAsked {
    pub path: Vec<Action>,
    pub history: History,
    pub nodes: i32,
    pub keep: usize,
    pub bounds: Bounds,
}

#[derive(Deserialize)]
struct AskedJson {
    path: Vec<String>,
    history: Vec<EnemyJson>,
    nodes: i32,
    keep: i32,
    #[serde(default)]
    life_bounds: Option<std::collections::HashMap<String, i32>>,
}

/// `{"path":[ACTION…],"history":[…],"nodes":N,"keep":K,"life_bounds":{POTIONS:BOUND…}}`.
pub fn asked(json: &[u8]) -> Result<TurnAsked, String> {
    let asked: AskedJson = serde_json::from_slice(json).map_err(|e| format!("a turn's search asked for: {e}"))?;
    Ok(TurnAsked {
        path: asked.path.iter().map(|a| Action::parse(a)).collect::<Result<_, _>>()?,
        history: record(asked.history),
        nodes: asked.nodes,
        keep: asked.keep.max(0) as usize,
        bounds: asked.life_bounds.unwrap_or_default(),
    })
}

#[derive(Deserialize)]
struct EndJson {
    actions: Vec<String>,
    hashes: Vec<u64>,
    status: i32,
    value: f64,
    key: u64,
    history: Vec<EnemyJson>,
    #[serde(default)]
    win: f64,
    #[serde(default)]
    hp: f64,
    #[serde(default)]
    life: i32,
    #[serde(default)]
    reach: i32,
    #[serde(default)]
    potions: String,
    #[serde(default)]
    shown: u64,
}

/// C#: ProtocolJson.Deserialize of a `TurnEnd`.
pub fn end(json: &[u8]) -> Result<TurnEnd, String> {
    let end: EndJson = serde_json::from_slice(json).map_err(|e| format!("a line: {e}"))?;
    Ok(TurnEnd {
        actions: end.actions.iter().map(|a| Action::parse(a)).collect::<Result<_, _>>()?,
        hashes: end.hashes,
        status: match end.status {
            0 => Status::InProgress,
            1 => Status::Won,
            2 => Status::Lost,
            other => return Err(format!("a line: no status {other}")),
        },
        value: end.value,
        key: end.key,
        history: std::sync::Arc::new(record(end.history)),
        win: end.win,
        hp: end.hp,
        life: end.life,
        reach: end.reach,
        potions: end.potions,
        shown: end.shown,
    })
}

/// Where a solver's root moved: the actions played from it, and the record of enemy moves there.
pub struct Advanced {
    pub played: Vec<Action>,
    pub moves: History,
}

#[derive(Deserialize)]
struct AdvancedJson {
    played: Vec<String>,
    moves: Vec<EnemyJson>,
}

/// `{"played":[ACTION…],"moves":[…]}`.
pub fn advanced(json: &[u8]) -> Result<Advanced, String> {
    let asked: AdvancedJson = serde_json::from_slice(json).map_err(|e| format!("a solver's move: {e}"))?;
    Ok(Advanced {
        played: asked.played.iter().map(|a| Action::parse(a)).collect::<Result<_, _>>()?,
        moves: record(asked.moves),
    })
}

/// A plan asked for (C#: SlPlanner.Search's budget and the record of enemy moves at the decision).
pub struct PlanAsked {
    pub history: History,
    pub budget: Budget,
}

#[derive(Deserialize)]
struct PlanJson {
    history: Vec<EnemyJson>,
    nodes_per_world: i32,
    beam: i32,
    easy_beam: i32,
    turn_decay: f64,
}

/// `{"history":[…],"nodes_per_world":N,"beam":B,"easy_beam":E,"turn_decay":D}`.
pub fn plan_asked(json: &[u8]) -> Result<PlanAsked, String> {
    let asked: PlanJson = serde_json::from_slice(json).map_err(|e| format!("a plan asked for: {e}"))?;
    Ok(PlanAsked {
        history: record(asked.history),
        budget: Budget {
            nodes_per_world: asked.nodes_per_world,
            beam: asked.beam,
            easy_beam: asked.easy_beam,
            turn_decay: asked.turn_decay,
        },
    })
}

/// What the beam found: `{"pick":END,"from":F,"by_first":[END…],"proved":P,"nodes":N,"turns":T}`.
pub fn write_found(found: &Found, out: &mut Vec<u8>) {
    out.put(b"{\"pick\":");
    end_json(&found.pick, out);
    out.put(b",\"from\":");
    double(found.from, out);
    out.put(b",\"by_first\":[");
    for (i, end) in found.by_first.iter().enumerate() {
        if i > 0 {
            out.put(b",");
        }
        end_json(end, out);
    }
    out.put(format!("],\"proved\":{},\"nodes\":{},\"turns\":{}}}", found.proved, found.nodes, found.turns).as_bytes());
}

/// A JSON number of a double as System.Text.Json reads it back exactly: the shortest text that is the double.
fn double(x: f64, out: &mut Vec<u8>) {
    if !x.is_finite() {
        panic!("a value that is not a number: {x}");
    }
    out.extend_from_slice(serde_json::to_string(&x).expect("a finite double").as_bytes());
}

fn string(s: &str, out: &mut Vec<u8>) {
    out.extend_from_slice(serde_json::to_string(s).expect("a string").as_bytes());
}

/// The turn's search as ProtocolJson reads a `TurnSearch`.
pub fn write(found: &TurnSearch, out: &mut Vec<u8>) {
    out.put(b"{\"ends\":[");
    for (i, end) in found.ends.iter().enumerate() {
        if i > 0 {
            out.put(b",");
        }
        end_json(end, out);
    }
    out.put(format!("],\"nodes\":{},\"cut\":{},\"pruned\":{}}}", found.nodes, found.cut, found.pruned).as_bytes());
}

fn end_json(end: &TurnEnd, out: &mut Vec<u8>) {
    out.put(b"{\"actions\":[");
    for (i, action) in end.actions.iter().enumerate() {
        if i > 0 {
            out.put(b",");
        }
        string(&action.text(), out);
    }
    out.put(b"],\"hashes\":[");
    for (i, hash) in end.hashes.iter().enumerate() {
        if i > 0 {
            out.put(b",");
        }
        out.put(hash.to_string().as_bytes());
    }
    out.put(format!("],\"status\":{},\"value\":", end.status as i32).as_bytes());
    double(end.value, out);
    out.put(format!(",\"key\":{},\"history\":", end.key).as_bytes());
    protocol::history_json(&end.history, out);
    out.put(b",\"win\":");
    double(end.win, out);
    out.put(b",\"hp\":");
    double(end.hp, out);
    out.put(format!(",\"life\":{},\"reach\":{},\"potions\":", end.life, end.reach).as_bytes());
    string(&end.potions, out);
    out.put(format!(",\"shown\":{}}}", end.shown).as_bytes());
}
