//! The protocol's records read from JSON, for the tests that write them back. A member no record has is an error, so
//! that a member added on the C# side cannot pass unseen.

use super::json::Json;
use sts2ai_engine::protocol::*;
use sts2ai_engine::rt::{Dec, Str};

type Res<T> = Result<T, String>;

/// The members of an object, each read once: `done` refuses what is left.
struct Members<'a> {
    what: &'static str,
    members: &'a [(String, Json)],
    read: Vec<bool>,
}

impl<'a> Members<'a> {
    fn of(value: &'a Json, what: &'static str) -> Res<Members<'a>> {
        match value {
            Json::Object(members) => Ok(Members {
                what,
                members,
                read: vec![false; members.len()],
            }),
            other => Err(format!("{what}: not an object: {}", other.brief())),
        }
    }

    fn get(&mut self, name: &str) -> Option<&'a Json> {
        let at = self.members.iter().position(|(key, _)| key == name)?;
        self.read[at] = true;
        Some(&self.members[at].1)
    }

    fn need(&mut self, name: &str) -> Res<&'a Json> {
        self.get(name).ok_or_else(|| format!("{}: no member {name}", self.what))
    }

    fn int(&mut self, name: &str) -> Res<i32> {
        int(self.need(name)?)
    }

    fn int_opt(&mut self, name: &str) -> Res<Option<i32>> {
        self.get(name).map(int).transpose()
    }

    fn uint_opt(&mut self, name: &str) -> Res<Option<u32>> {
        self.get(name)
            .map(|v| number(v)?.parse().map_err(|_| format!("not a u32: {}", v.brief())))
            .transpose()
    }

    fn bool(&mut self, name: &str) -> Res<bool> {
        boolean(self.need(name)?)
    }

    fn bool_opt(&mut self, name: &str) -> Res<Option<bool>> {
        self.get(name).map(boolean).transpose()
    }

    fn str(&mut self, name: &str) -> Res<Str> {
        string(self.need(name)?)
    }

    fn str_opt(&mut self, name: &str) -> Res<Option<Str>> {
        self.get(name).map(string).transpose()
    }

    fn dec(&mut self, name: &str) -> Res<Dec> {
        decimal(self.need(name)?)
    }

    fn dec_opt(&mut self, name: &str) -> Res<Option<Dec>> {
        self.get(name).map(decimal).transpose()
    }

    fn list<T>(&mut self, name: &str, read: impl Fn(&Json) -> Res<T>) -> Res<Vec<T>> {
        list(self.need(name)?, read)
    }

    fn list_opt<T>(&mut self, name: &str, read: impl Fn(&Json) -> Res<T>) -> Res<Option<Vec<T>>> {
        self.get(name).map(|v| list(v, read)).transpose()
    }

    fn done(self) -> Res<()> {
        match self.read.iter().position(|read| !read) {
            Some(at) => Err(format!("{}: a member no record has: {}", self.what, self.members[at].0)),
            None => Ok(()),
        }
    }
}

fn number(value: &Json) -> Res<&str> {
    match value {
        Json::Number(text) => Ok(text),
        other => Err(format!("not a number: {}", other.brief())),
    }
}

fn int(value: &Json) -> Res<i32> {
    number(value)?.parse().map_err(|_| format!("not an int: {}", value.brief()))
}

fn boolean(value: &Json) -> Res<bool> {
    match value {
        Json::Bool(b) => Ok(*b),
        other => Err(format!("not a bool: {}", other.brief())),
    }
}

fn string(value: &Json) -> Res<Str> {
    match value {
        Json::Text(text) => Ok(Str::from_string(text.clone())),
        other => Err(format!("not a string: {}", other.brief())),
    }
}

fn nullable_string(value: &Json) -> Res<Option<Str>> {
    match value {
        Json::Null => Ok(None),
        other => string(other).map(Some),
    }
}

fn decimal(value: &Json) -> Res<Dec> {
    let text = number(value)?;
    Dec::parse(text).ok_or_else(|| format!("not a decimal: {text}"))
}

fn list<T>(value: &Json, read: impl Fn(&Json) -> Res<T>) -> Res<Vec<T>> {
    match value {
        Json::Array(items) => items.iter().map(read).collect(),
        other => Err(format!("not an array: {}", other.brief())),
    }
}

pub fn observation(value: &Json) -> Res<CombatObservation> {
    let mut m = Members::of(value, "observation")?;
    let observation = CombatObservation {
        encounter: m.str_opt("encounter")?,
        room: m.str_opt("room")?,
        act: m.int("act")?,
        ascension: m.int("ascension")?,
        round: m.int("round")?,
        player: player(m.need("player")?)?,
        hand: m.list("hand", card)?,
        draw: m.list("draw", card)?,
        discard: m.list("discard", card)?,
        exhaust: m.list("exhaust", card)?,
        potions: m.list("potions", nullable_string)?,
        relics: m.list("relics", relic)?,
        allies: m.list("allies", creature)?,
        enemies: m.list("enemies", creature)?,
        prompt: m.get("prompt").map(choice).transpose()?,
    };
    m.done()?;
    Ok(observation)
}

pub fn run(value: &Json) -> Res<RunChange> {
    let mut m = Members::of(value, "run")?;
    let run = RunChange {
        hp: m.int("hp")?,
        max_hp: m.int("max_hp")?,
        gold: m.int("gold")?,
        deck: m.list("deck", card)?,
        relics: m.list("relics", relic)?,
        potions: m.list("potions", nullable_string)?,
    };
    m.done()?;
    Ok(run)
}

fn player(value: &Json) -> Res<PlayerView> {
    let mut m = Members::of(value, "player")?;
    let player = PlayerView {
        uid: m.uint_opt("uid")?,
        character: m.str("character")?,
        hp: m.int("hp")?,
        max_hp: m.int("max_hp")?,
        block: m.int("block")?,
        energy: m.int("energy")?,
        max_energy: m.int("max_energy")?,
        stars: m.int("stars")?,
        gold: m.int("gold")?,
        powers: m.list("powers", power)?,
        orb_slots: m.int("orb_slots")?,
        orbs: m.list("orbs", orb)?,
        arrows: m.int_opt("arrows")?,
        arrow_slots: m.int_opt("arrow_slots")?,
        melodies: m.list_opt("melodies", string)?,
    };
    m.done()?;
    Ok(player)
}

fn card(value: &Json) -> Res<CardView> {
    let mut m = Members::of(value, "card")?;
    let card = CardView {
        id: m.str("id")?,
        uid: m.uint_opt("uid")?,
        up: m.int_opt("up")?,
        cost: m.int_opt("cost")?,
        x_cost: m.bool_opt("x_cost")?,
        stars: m.int_opt("stars")?,
        enchant: m.get("enchant").map(modifier).transpose()?,
        affliction: m.get("affliction").map(modifier).transpose()?,
        tinker: m.get("tinker").map(tinker).transpose()?,
        keywords: m.list_opt("keywords", string)?,
        this_turn: m.list_opt("this_turn", string)?,
        vars: m.get("vars").map(vars).transpose()?,
        upgraded: m.get("upgraded").map(card).transpose()?.map(Box::new),
        lasting: m.int_opt("lasting")?,
        stars_lasting: m.int_opt("stars_lasting")?,
        replay: m.int_opt("replay")?,
        spent: m.bool_opt("spent")?,
        calc: m.dec_opt("calc")?,
        grown: m.dec_opt("grown")?,
    };
    m.done()?;
    Ok(card)
}

fn vars(value: &Json) -> Res<Vec<(Str, Dec)>> {
    match value {
        Json::Object(members) => members
            .iter()
            .map(|(name, v)| Ok((Str::from_string(name.clone()), decimal(v)?)))
            .collect(),
        other => Err(format!("vars: not an object: {}", other.brief())),
    }
}

fn tinker(value: &Json) -> Res<TinkerView> {
    let mut m = Members::of(value, "tinker")?;
    let tinker = TinkerView {
        type_: m.str("type")?,
        rider: m.str("rider")?,
    };
    m.done()?;
    Ok(tinker)
}

fn modifier(value: &Json) -> Res<ModifierView> {
    let mut m = Members::of(value, "modifier")?;
    let modifier = ModifierView {
        id: m.str("id")?,
        amount: m.int("amount")?,
        extra: m.int_opt("extra")?,
    };
    m.done()?;
    Ok(modifier)
}

fn power(value: &Json) -> Res<PowerView> {
    let mut m = Members::of(value, "power")?;
    let power = PowerView {
        id: m.str("id")?,
        amount: m.int("amount")?,
        stacks: m.int_opt("stacks")?,
        card: m.get("card").map(card).transpose()?,
        detail: m.dec_opt("detail")?,
        stolen: m.get("stolen").map(card).transpose()?,
    };
    m.done()?;
    Ok(power)
}

fn orb(value: &Json) -> Res<OrbView> {
    let mut m = Members::of(value, "orb")?;
    let orb = OrbView {
        id: m.str("id")?,
        passive: m.dec("passive")?,
        evoke: m.dec("evoke")?,
    };
    m.done()?;
    Ok(orb)
}

fn relic(value: &Json) -> Res<RelicView> {
    let mut m = Members::of(value, "relic")?;
    let relic = RelicView {
        id: m.str("id")?,
        counter: m.int_opt("counter")?,
        status: m.str_opt("status")?,
        melted: m.bool_opt("melted")?,
        used_up: m.bool_opt("used_up")?,
        cards: m.list_opt("cards", string)?,
        spent: m.bool_opt("spent")?,
    };
    m.done()?;
    Ok(relic)
}

fn creature(value: &Json) -> Res<CreatureView> {
    let mut m = Members::of(value, "creature")?;
    let creature = CreatureView {
        uid: m.uint_opt("uid")?,
        id: m.str_opt("id")?,
        hp: m.int("hp")?,
        max_hp: m.int("max_hp")?,
        block: m.int("block")?,
        powers: m.list("powers", power)?,
        intents: m.list_opt("intents", intent)?,
        arrows: m.int_opt("arrows")?,
    };
    m.done()?;
    Ok(creature)
}

fn intent(value: &Json) -> Res<IntentView> {
    let mut m = Members::of(value, "intent")?;
    let intent = IntentView {
        type_: m.str("type")?,
        damage: m.int_opt("damage")?,
        hits: m.int_opt("hits")?,
    };
    m.done()?;
    Ok(intent)
}

fn choice(value: &Json) -> Res<CardChoice> {
    let mut m = Members::of(value, "prompt")?;
    let choice = CardChoice {
        options: m.list("options", card)?,
        min: m.int("min")?,
        max: m.int("max")?,
        context: context(m.need("context")?)?,
    };
    m.done()?;
    Ok(choice)
}

fn context(value: &Json) -> Res<ChoiceContext> {
    let mut m = Members::of(value, "context")?;
    let context = ChoiceContext {
        purpose: m.str("purpose")?,
        source: m.str_opt("source")?,
        cancelable: m.bool("cancelable")?,
        in_combat: m.bool("in_combat")?,
        prompt_key: m.str_opt("prompt_key")?,
        enchantment: m.get("enchantment").map(modifier).transpose()?,
    };
    m.done()?;
    Ok(context)
}
