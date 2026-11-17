//! A combat of the real game, exported by the C# side, read into the engine (DESIGN.md, "The export").

use crate::r#gen::import::{GAME, SCHEMA_HASH, read_arenas, read_roots};
use crate::rt::Combat;
use crate::rt::import::{Res, field, members, object};
use serde_json::Value;

/// The format of an export this engine reads. 2: `sim` carries the draw pile's record (`draw_log`).
pub const FORMAT: u32 = 2;

/// The combat of an export. An export of another game version or made along another schema is refused, and so is one
/// that reached an object the engine has no class for: there is nothing the engine could do with it but guess.
pub fn import(json: &str) -> Res<Combat> {
    let doc: Value = serde_json::from_str(json).map_err(|e| format!("not JSON: {e}"))?;
    let o = object(&doc)?;
    members(o, 7)?;
    let format: u32 = field(o, "format")?;
    if format != FORMAT {
        return Err(format!("export format {format}, not {FORMAT}"));
    }
    let game: String = field(o, "game")?;
    if game != GAME {
        return Err(format!(
            "an export of the game {game}, the engine is {GAME}"
        ));
    }
    let schema: String = field(o, "schema")?;
    if schema != SCHEMA_HASH {
        return Err(format!(
            "an export made along the schema {schema}, the engine has {SCHEMA_HASH}"
        ));
    }
    let outside = object(o.get("outside").ok_or("no member outside")?)?;
    if !outside.is_empty() {
        let names: Vec<&str> = outside.keys().map(String::as_str).collect();
        return Err(format!(
            "the combat reaches classes the engine does not hold: {}",
            names.join(", ")
        ));
    }
    let mut c = crate::manual::canon::combat();
    read_arenas(
        object(o.get("arenas").ok_or("no member arenas")?)?,
        &mut c.a,
    )?;
    c.s = read_roots(object(o.get("roots").ok_or("no member roots")?)?)?;
    // What the program around the game holds beside it: how many card prompts its selector was asked so far, and
    // the draw pile's public record since its last shuffle (drawlog.rs).
    let sim = object(o.get("sim").ok_or("no member sim")?)?;
    members(sim, 2)?;
    c.x.prompts = field(sim, "prompts")?;
    // Read here, refused only where a NoSL world is drawn from it: an SL world never reads it.
    let log = crate::drawlog::DrawLog::parse(sim.get("draw_log").ok_or("no member sim.draw_log")?);
    c.x.draw_log = Some(std::sync::Arc::new(log));
    Ok(c)
}
