//! What the brain is shown of a combat, and its JSON: `Sts2Ai.Protocol`'s records of a combat (CombatObservation.cs,
//! CardChoice.cs) with their members in the order they are declared there, written as `ProtocolJson` writes them
//! (System.Text.Json: snake_case names, a null member left out, the default escaping, a decimal with its scale).
//! The bytes are the contract: the lockstep check compares them with the real game's.

use crate::rt::{Dec, Str};

// ---------------------------------------------------------------------------------------------------------------------
// Where the bytes go
// ---------------------------------------------------------------------------------------------------------------------

/// Takes the bytes of a JSON text: a buffer, or a hash that never holds the text.
pub trait Sink {
    fn put(&mut self, bytes: &[u8]);
}

impl Sink for Vec<u8> {
    #[inline]
    fn put(&mut self, bytes: &[u8]) {
        // Most of a JSON text comes in pieces of a few bytes, which a call to copy them costs more than.
        if bytes.len() <= 16 {
            for &byte in bytes {
                self.push(byte);
            }
        } else {
            self.extend_from_slice(bytes);
        }
    }
}

/// C#: ProtocolJson.Hash. FNV-1a 64 over the bytes it is given.
pub struct Fnv(pub u64);

impl Fnv {
    pub const fn new() -> Fnv {
        Fnv(14695981039346656037)
    }
}

impl Default for Fnv {
    fn default() -> Fnv {
        Fnv::new()
    }
}

impl Sink for Fnv {
    #[inline]
    fn put(&mut self, bytes: &[u8]) {
        let mut hash = self.0;
        for &b in bytes {
            hash = (hash ^ b as u64).wrapping_mul(1099511628211);
        }
        self.0 = hash;
    }
}

/// The inside of a JSON string: what it is given is text, and goes on escaped. `ProtocolJson.Hash` of a C# string is the
/// hash of `"`, the text through this, `"`.
pub struct Escaped<'a, S: Sink>(pub &'a mut S);

impl<S: Sink> Sink for Escaped<'_, S> {
    fn put(&mut self, bytes: &[u8]) {
        escape(std::str::from_utf8(bytes).expect("text split inside a character"), self.0);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// System.Text.Json's forms
// ---------------------------------------------------------------------------------------------------------------------

/// Whether the default encoder writes the byte as it is: printable ASCII but for the characters HTML could misread.
const fn plain(byte: u8) -> bool {
    matches!(byte, b' '..=b'~') && !matches!(byte, b'"' | b'&' | b'\'' | b'+' | b'<' | b'>' | b'\\' | b'`')
}

/// A string's text as System.Text.Json's default encoder writes it between the quotes: `\b \t \n \f \r \\` short, every
/// other character that is not plain as `\uXXXX` per UTF-16 code unit, upper-case hex.
pub fn escape<S: Sink>(text: &str, out: &mut S) {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let bytes = text.as_bytes();
    let mut from = 0;
    while let Some(ahead) = bytes[from..].iter().position(|byte| !plain(*byte)) {
        let at = from + ahead;
        out.put(&bytes[from..at]);
        let ch = text[at..].chars().next().expect("a character where a byte is");
        from = at + ch.len_utf8();
        match ch {
            '\u{8}' => out.put(b"\\b"),
            '\t' => out.put(b"\\t"),
            '\n' => out.put(b"\\n"),
            '\u{c}' => out.put(b"\\f"),
            '\r' => out.put(b"\\r"),
            '\\' => out.put(b"\\\\"),
            _ => {
                let mut units = [0u16; 2];
                for unit in ch.encode_utf16(&mut units) {
                    let u = *unit as usize;
                    out.put(&[b'\\', b'u', HEX[u >> 12], HEX[(u >> 8) & 15], HEX[(u >> 4) & 15], HEX[u & 15]]);
                }
            }
        }
    }
    out.put(&bytes[from..]);
}

pub fn string<S: Sink>(text: &str, out: &mut S) {
    out.put(b"\"");
    escape(text, out);
    out.put(b"\"");
}

pub fn int<S: Sink>(n: i64, out: &mut S) {
    if n < 0 {
        out.put(b"-");
    }
    uint(n.unsigned_abs(), out);
}

pub fn uint<S: Sink>(n: u64, out: &mut S) {
    let mut digits = [0u8; 20];
    let mut at = digits.len();
    let mut left = n;
    loop {
        at -= 1;
        digits[at] = b'0' + (left % 10) as u8;
        left /= 10;
        if left == 0 {
            break;
        }
    }
    out.put(&digits[at..]);
}

/// A decimal as .NET prints it: every digit of its scale, and a zero with no sign.
pub fn decimal<S: Sink>(d: Dec, out: &mut S) {
    match u64::try_from(d.mantissa()) {
        Ok(whole) if d.scale() == 0 => {
            if d.is_negative() && whole != 0 {
                out.put(b"-");
            }
            uint(whole, out);
        }
        _ => out.put(d.to_string().as_bytes()),
    }
}

pub fn boolean<S: Sink>(b: bool, out: &mut S) {
    out.put(if b { b"true" } else { b"false" });
}

/// C#: JsonNamingPolicy.SnakeCaseLower.ConvertName, for a name of ASCII letters and digits (a member's, an enum
/// value's): a `_` before an upper-case letter that follows a lower-case letter or a digit, or that ends a run of
/// upper-case letters before a lower-case one.
pub fn snake(name: &str) -> String {
    let chars = name.as_bytes();
    let mut out = String::with_capacity(name.len() + 4);
    #[derive(PartialEq)]
    enum State {
        NotStarted,
        Upper,
        LowerOrDigit,
    }
    let mut state = State::NotStarted;
    for (i, &ch) in chars.iter().enumerate() {
        if ch.is_ascii_uppercase() {
            let next_is_lower = chars.get(i + 1).is_some_and(|n| n.is_ascii_lowercase());
            if state == State::LowerOrDigit || (state == State::Upper && next_is_lower) {
                out.push('_');
            }
            out.push(ch.to_ascii_lowercase() as char);
            state = State::Upper;
        } else if ch.is_ascii_lowercase() || ch.is_ascii_digit() {
            out.push(ch as char);
            state = State::LowerOrDigit;
        } else {
            panic!("snake case of {name:?}: only names of ASCII letters and digits are written");
        }
    }
    out
}

/// A JSON object being written: its members' commas.
struct Members<'a, S: Sink> {
    out: &'a mut S,
    any: bool,
}

impl<'a, S: Sink> Members<'a, S> {
    fn begin(out: &'a mut S) -> Self {
        out.put(b"{");
        Members { out, any: false }
    }

    /// The member's name, which is written as it is: a snake_case name of this file.
    fn name(&mut self, name: &str) -> &mut S {
        if self.any {
            self.out.put(b",");
        }
        self.any = true;
        self.out.put(b"\"");
        self.out.put(name.as_bytes());
        self.out.put(b"\":");
        self.out
    }

    /// A member named by data (a dictionary's key): escaped as a string is.
    fn key(&mut self, key: &str) -> &mut S {
        if self.any {
            self.out.put(b",");
        }
        self.any = true;
        string(key, self.out);
        self.out.put(b":");
        self.out
    }

    fn int(&mut self, name: &str, n: i32) {
        int(n as i64, self.name(name));
    }

    fn int_opt(&mut self, name: &str, n: Option<i32>) {
        if let Some(n) = n {
            self.int(name, n);
        }
    }

    fn uint_opt(&mut self, name: &str, n: Option<u32>) {
        if let Some(n) = n {
            int(n as i64, self.name(name));
        }
    }

    fn bool(&mut self, name: &str, b: bool) {
        boolean(b, self.name(name));
    }

    fn bool_opt(&mut self, name: &str, b: Option<bool>) {
        if let Some(b) = b {
            self.bool(name, b);
        }
    }

    fn str(&mut self, name: &str, s: Str) {
        string(s.as_str(), self.name(name));
    }

    fn str_opt(&mut self, name: &str, s: Option<Str>) {
        if let Some(s) = s {
            self.str(name, s);
        }
    }

    fn dec(&mut self, name: &str, d: Dec) {
        decimal(d, self.name(name));
    }

    fn dec_opt(&mut self, name: &str, d: Option<Dec>) {
        if let Some(d) = d {
            self.dec(name, d);
        }
    }

    fn end(self) {
        self.out.put(b"}");
    }
}

/// A JSON array of `items`, each written by `write`.
fn array<S: Sink, T>(items: &[T], out: &mut S, mut write: impl FnMut(&T, &mut S)) {
    out.put(b"[");
    for (i, item) in items.iter().enumerate() {
        if i > 0 {
            out.put(b",");
        }
        write(item, out);
    }
    out.put(b"]");
}

fn strings<S: Sink>(items: &[Str], out: &mut S) {
    array(items, out, |s, out| string(s.as_str(), out));
}

/// A list of ids with nulls among them (the potion slots): a null item is written, unlike a null member.
fn nullable_strings<S: Sink>(items: &[Option<Str>], out: &mut S) {
    array(items, out, |s, out| match s {
        Some(s) => string(s.as_str(), out),
        None => out.put(b"null"),
    });
}

// ---------------------------------------------------------------------------------------------------------------------
// CombatObservation.cs
// ---------------------------------------------------------------------------------------------------------------------

/// C#: CombatObservation.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CombatObservation {
    pub encounter: Option<Str>,
    pub room: Option<Str>,
    pub act: i32,
    pub ascension: i32,
    pub round: i32,
    pub player: PlayerView,
    pub hand: Vec<CardView>,
    pub draw: Vec<CardView>,
    pub discard: Vec<CardView>,
    pub exhaust: Vec<CardView>,
    pub potions: Vec<Option<Str>>,
    pub relics: Vec<RelicView>,
    pub allies: Vec<CreatureView>,
    pub enemies: Vec<CreatureView>,
    pub prompt: Option<CardChoice>,
}

/// C#: PlayerView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct PlayerView {
    pub uid: Option<u32>,
    pub character: Str,
    pub hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub energy: i32,
    pub max_energy: i32,
    pub stars: i32,
    pub gold: i32,
    pub powers: Vec<PowerView>,
    pub orb_slots: i32,
    pub orbs: Vec<OrbView>,
    pub arrows: Option<i32>,
    pub arrow_slots: Option<i32>,
    pub melodies: Option<Vec<Str>>,
}

/// C#: CardView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CardView {
    pub id: Str,
    pub uid: Option<u32>,
    pub up: Option<i32>,
    pub cost: Option<i32>,
    pub x_cost: Option<bool>,
    pub stars: Option<i32>,
    pub enchant: Option<ModifierView>,
    pub affliction: Option<ModifierView>,
    pub tinker: Option<TinkerView>,
    pub keywords: Option<Vec<Str>>,
    pub this_turn: Option<Vec<Str>>,
    /// In the order they are written: Observe fills the dictionary sorted by name.
    pub vars: Option<Vec<(Str, Dec)>>,
    pub upgraded: Option<Box<CardView>>,
    pub lasting: Option<i32>,
    pub stars_lasting: Option<i32>,
    pub replay: Option<i32>,
    pub spent: Option<bool>,
    pub calc: Option<Dec>,
    pub grown: Option<Dec>,
}

/// C#: TinkerView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct TinkerView {
    pub type_: Str,
    pub rider: Str,
}

/// C#: ModifierView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ModifierView {
    pub id: Str,
    pub amount: i32,
    pub extra: Option<i32>,
}

/// C#: PowerView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct PowerView {
    pub id: Str,
    pub amount: i32,
    pub stacks: Option<i32>,
    pub card: Option<CardView>,
    pub detail: Option<Dec>,
    pub stolen: Option<CardView>,
}

/// C#: OrbView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct OrbView {
    pub id: Str,
    pub passive: Dec,
    pub evoke: Dec,
}

/// C#: RelicView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct RelicView {
    pub id: Str,
    pub counter: Option<i32>,
    pub status: Option<Str>,
    pub melted: Option<bool>,
    pub used_up: Option<bool>,
    pub cards: Option<Vec<Str>>,
    pub spent: Option<bool>,
}

/// C#: CreatureView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CreatureView {
    pub uid: Option<u32>,
    pub id: Option<Str>,
    pub hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub powers: Vec<PowerView>,
    pub intents: Option<Vec<IntentView>>,
    pub arrows: Option<i32>,
}

/// C#: IntentView.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct IntentView {
    pub type_: Str,
    pub damage: Option<i32>,
    pub hits: Option<i32>,
}

// ---------------------------------------------------------------------------------------------------------------------
// CardChoice.cs
// ---------------------------------------------------------------------------------------------------------------------

/// C#: CardChoice.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CardChoice {
    pub options: Vec<CardView>,
    pub min: i32,
    pub max: i32,
    pub context: ChoiceContext,
}

impl CardChoice {
    /// C#: CardChoice.Accepts.
    pub fn accepts(&self, picks: &[i32]) -> bool {
        let count = picks.len() as i32;
        count >= self.min
            && count <= self.max
            && picks.iter().all(|&i| i >= 0 && (i as usize) < self.options.len())
            && picks.iter().enumerate().all(|(at, i)| !picks[..at].contains(i))
    }
}

/// C#: ChoiceContext.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ChoiceContext {
    pub purpose: Str,
    pub source: Option<Str>,
    pub cancelable: bool,
    pub in_combat: bool,
    pub prompt_key: Option<Str>,
    pub enchantment: Option<ModifierView>,
}

// ---------------------------------------------------------------------------------------------------------------------
// The run, as far as a combat changes it
// ---------------------------------------------------------------------------------------------------------------------

/// C#: EngineWorld.RunChange. The members of RunView a combat can change, under RunView's names.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct RunChange {
    pub hp: i32,
    pub max_hp: i32,
    pub gold: i32,
    pub deck: Vec<CardView>,
    pub relics: Vec<RelicView>,
    pub potions: Vec<Option<Str>>,
}

// ---------------------------------------------------------------------------------------------------------------------
// ProtocolJson.cs
// ---------------------------------------------------------------------------------------------------------------------

/// How the cards of an observation are written.
#[derive(Copy, Clone, PartialEq)]
enum Form {
    /// C#: ProtocolJson.Serialize.
    Shown,
    /// C#: PimcBrain.StateKey's observation: the hand, the discard pile and the exhaust pile each with no uids and
    /// sorted by card key, the potions sorted with the empty slots last, a prompt's options with no uids.
    StateKey,
}

/// C#: ProtocolJson.Serialize of a CombatObservation.
pub fn observation_json<S: Sink>(obs: &CombatObservation, out: &mut S) {
    observation(obs, Form::Shown, out);
}

/// C#: PimcBrain.StateKey. The hash of the observation as search tells states apart; the text it is the hash of is
/// never held, only each card's key while its pile is sorted.
pub fn state_key(obs: &CombatObservation) -> u64 {
    let mut hash = Fnv::new();
    observation(obs, Form::StateKey, &mut hash);
    hash.0
}

/// C#: ProtocolJson.CardKey. The card's identity as the player sees it: its JSON without the uid.
pub fn card_key(card: &CardView) -> Vec<u8> {
    let mut key = Vec::with_capacity(96);
    card_json(card, false, &mut key);
    key
}

/// The cards in the ordinal order of their keys, cards with one key in the order they came (C#: OrderBy(CardKey,
/// StringComparer.Ordinal), a stable sort). A key is ASCII, so its bytes order as its UTF-16 does.
pub fn sorted_keys(cards: &[CardView]) -> Vec<Vec<u8>> {
    let mut keys: Vec<Vec<u8>> = cards.iter().map(card_key).collect();
    keys.sort();
    keys
}

/// The cards sorted by card key (see `sorted_keys`), for the piles an observation lists as multisets.
pub fn sort_by_key(cards: &mut Vec<CardView>) {
    let mut keyed: Vec<(Vec<u8>, CardView)> = std::mem::take(cards).into_iter().map(|c| (card_key(&c), c)).collect();
    keyed.sort_by(|a, b| a.0.cmp(&b.0));
    cards.extend(keyed.into_iter().map(|(_, c)| c));
}

/// C#: ProtocolJson.Serialize of what `sz_world_run` gives (EngineWorld.RunChange).
pub fn run_json<S: Sink>(run: &RunChange, out: &mut S) {
    let mut m = Members::begin(out);
    m.int("hp", run.hp);
    m.int("max_hp", run.max_hp);
    m.int("gold", run.gold);
    array(&run.deck, m.name("deck"), |c, out| card_json(c, true, out));
    array(&run.relics, m.name("relics"), relic_json);
    nullable_strings(&run.potions, m.name("potions"));
    m.end();
}

fn observation<S: Sink>(obs: &CombatObservation, form: Form, out: &mut S) {
    let mut m = Members::begin(out);
    m.str_opt("encounter", obs.encounter);
    m.str_opt("room", obs.room);
    m.int("act", obs.act);
    m.int("ascension", obs.ascension);
    m.int("round", obs.round);
    player_json(&obs.player, m.name("player"));
    pile(&obs.hand, form, m.name("hand"));
    array(&obs.draw, m.name("draw"), |c, out| card_json(c, true, out));
    pile(&obs.discard, form, m.name("discard"));
    pile(&obs.exhaust, form, m.name("exhaust"));
    match form {
        Form::Shown => nullable_strings(&obs.potions, m.name("potions")),
        Form::StateKey => {
            // C#: OrderBy(p => p == null).ThenBy(p => p, StringComparer.Ordinal)
            let mut potions = obs.potions.clone();
            potions.sort_by(|a, b| match (a, b) {
                (Some(a), Some(b)) => a.compare(*b).cmp(&0),
                (a, b) => a.is_none().cmp(&b.is_none()),
            });
            nullable_strings(&potions, m.name("potions"));
        }
    }
    array(&obs.relics, m.name("relics"), relic_json);
    array(&obs.allies, m.name("allies"), creature_json);
    array(&obs.enemies, m.name("enemies"), creature_json);
    if let Some(prompt) = &obs.prompt {
        choice_json(prompt, form == Form::Shown, m.name("prompt"));
    }
    m.end();
}

/// A pile whose order the player sees: as it is, or for the state key as the multiset of its cards' keys.
fn pile<S: Sink>(cards: &[CardView], form: Form, out: &mut S) {
    match form {
        Form::Shown => array(cards, out, |c, out| card_json(c, true, out)),
        Form::StateKey => array(&sorted_keys(cards), out, |key, out| out.put(key)),
    }
}

fn player_json<S: Sink>(p: &PlayerView, out: &mut S) {
    let mut m = Members::begin(out);
    m.uint_opt("uid", p.uid);
    m.str("character", p.character);
    m.int("hp", p.hp);
    m.int("max_hp", p.max_hp);
    m.int("block", p.block);
    m.int("energy", p.energy);
    m.int("max_energy", p.max_energy);
    m.int("stars", p.stars);
    m.int("gold", p.gold);
    array(&p.powers, m.name("powers"), power_json);
    m.int("orb_slots", p.orb_slots);
    array(&p.orbs, m.name("orbs"), orb_json);
    m.int_opt("arrows", p.arrows);
    m.int_opt("arrow_slots", p.arrow_slots);
    if let Some(melodies) = &p.melodies {
        strings(melodies, m.name("melodies"));
    }
    m.end();
}

/// A card; `uid` false leaves its uid out, which makes the text its key.
fn card_json<S: Sink>(card: &CardView, uid: bool, out: &mut S) {
    let mut m = Members::begin(out);
    m.str("id", card.id);
    if uid {
        m.uint_opt("uid", card.uid);
    }
    m.int_opt("up", card.up);
    m.int_opt("cost", card.cost);
    m.bool_opt("x_cost", card.x_cost);
    m.int_opt("stars", card.stars);
    if let Some(enchant) = &card.enchant {
        modifier_json(enchant, m.name("enchant"));
    }
    if let Some(affliction) = &card.affliction {
        modifier_json(affliction, m.name("affliction"));
    }
    if let Some(tinker) = &card.tinker {
        let mut t = Members::begin(m.name("tinker"));
        t.str("type", tinker.type_);
        t.str("rider", tinker.rider);
        t.end();
    }
    if let Some(keywords) = &card.keywords {
        strings(keywords, m.name("keywords"));
    }
    if let Some(this_turn) = &card.this_turn {
        strings(this_turn, m.name("this_turn"));
    }
    if let Some(vars) = &card.vars {
        let mut v = Members::begin(m.name("vars"));
        for (name, value) in vars {
            decimal(*value, v.key(name.as_str()));
        }
        v.end();
    }
    if let Some(upgraded) = &card.upgraded {
        card_json(upgraded, true, m.name("upgraded"));
    }
    m.int_opt("lasting", card.lasting);
    m.int_opt("stars_lasting", card.stars_lasting);
    m.int_opt("replay", card.replay);
    m.bool_opt("spent", card.spent);
    m.dec_opt("calc", card.calc);
    m.dec_opt("grown", card.grown);
    m.end();
}

fn modifier_json<S: Sink>(modifier: &ModifierView, out: &mut S) {
    let mut m = Members::begin(out);
    m.str("id", modifier.id);
    m.int("amount", modifier.amount);
    m.int_opt("extra", modifier.extra);
    m.end();
}

fn power_json<S: Sink>(power: &PowerView, out: &mut S) {
    let mut m = Members::begin(out);
    m.str("id", power.id);
    m.int("amount", power.amount);
    m.int_opt("stacks", power.stacks);
    if let Some(card) = &power.card {
        card_json(card, true, m.name("card"));
    }
    m.dec_opt("detail", power.detail);
    if let Some(stolen) = &power.stolen {
        card_json(stolen, true, m.name("stolen"));
    }
    m.end();
}

fn orb_json<S: Sink>(orb: &OrbView, out: &mut S) {
    let mut m = Members::begin(out);
    m.str("id", orb.id);
    m.dec("passive", orb.passive);
    m.dec("evoke", orb.evoke);
    m.end();
}

fn relic_json<S: Sink>(relic: &RelicView, out: &mut S) {
    let mut m = Members::begin(out);
    m.str("id", relic.id);
    m.int_opt("counter", relic.counter);
    m.str_opt("status", relic.status);
    m.bool_opt("melted", relic.melted);
    m.bool_opt("used_up", relic.used_up);
    if let Some(cards) = &relic.cards {
        strings(cards, m.name("cards"));
    }
    m.bool_opt("spent", relic.spent);
    m.end();
}

fn creature_json<S: Sink>(creature: &CreatureView, out: &mut S) {
    let mut m = Members::begin(out);
    m.uint_opt("uid", creature.uid);
    m.str_opt("id", creature.id);
    m.int("hp", creature.hp);
    m.int("max_hp", creature.max_hp);
    m.int("block", creature.block);
    array(&creature.powers, m.name("powers"), power_json);
    if let Some(intents) = &creature.intents {
        array(intents, m.name("intents"), intent_json);
    }
    m.int_opt("arrows", creature.arrows);
    m.end();
}

fn intent_json<S: Sink>(intent: &IntentView, out: &mut S) {
    let mut m = Members::begin(out);
    m.str("type", intent.type_);
    m.int_opt("damage", intent.damage);
    m.int_opt("hits", intent.hits);
    m.end();
}

fn choice_json<S: Sink>(choice: &CardChoice, uids: bool, out: &mut S) {
    let mut m = Members::begin(out);
    array(&choice.options, m.name("options"), |c, out| card_json(c, uids, out));
    m.int("min", choice.min);
    m.int("max", choice.max);
    let context = &choice.context;
    let mut x = Members::begin(m.name("context"));
    x.str("purpose", context.purpose);
    x.str_opt("source", context.source);
    x.bool("cancelable", context.cancelable);
    x.bool("in_combat", context.in_combat);
    x.str_opt("prompt_key", context.prompt_key);
    if let Some(enchantment) = &context.enchantment {
        modifier_json(enchantment, x.name("enchantment"));
    }
    x.end();
    m.end();
}

// ---------------------------------------------------------------------------------------------------------------------
// CombatAction.cs
// ---------------------------------------------------------------------------------------------------------------------

/// C#: CombatAction, as far as the engine writes one: a play of the plays the game lists.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum CombatAction {
    /// "play:CARD[:TARGET]"
    Play { card: u32, target: Option<u32> },
    /// "potion:SLOT[:TARGET]"
    Potion { slot: i32, target: Option<u32> },
    /// "end_turn"
    EndTurn,
}

impl CombatAction {
    /// C#: CombatAction.ToString.
    pub fn write<S: Sink>(&self, out: &mut S) {
        let target = |target: &Option<u32>, out: &mut S| {
            if let Some(t) = target {
                out.put(b":");
                int(*t as i64, out);
            }
        };
        match self {
            CombatAction::Play { card, target: t } => {
                out.put(b"play:");
                int(*card as i64, out);
                target(t, out);
            }
            CombatAction::Potion { slot, target: t } => {
                out.put(b"potion:");
                int(*slot as i64, out);
                target(t, out);
            }
            CombatAction::EndTurn => out.put(b"end_turn"),
        }
    }
}

/// The plays as `sz_world_legal` gives them: each one's text and a line feed.
pub fn legal_text(plays: &[CombatAction]) -> Vec<u8> {
    let mut out = Vec::with_capacity(plays.len() * 12);
    for play in plays {
        play.write(&mut out);
        out.put(b"\n");
    }
    out
}
