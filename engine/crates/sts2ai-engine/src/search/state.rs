//! A state of the search: a world where the player is asked (their turn, a card prompt, or the combat's end), with
//! what the search reads of every state it reaches, read once when the state is made (C#: EngineWorld with the
//! `sz_node` of its step and its observation, deserialized). The actions, and the record of enemy moves (C#:
//! MoveRecord).

use std::sync::Arc;

use crate::ffi::World as _;
use crate::protocol::{self, CombatObservation, EnemyTurn, EnemyTurns, Fnv, RunChange};
use crate::rt::Str;
use crate::world::Live;
use crate::{keys, legal, observe, prompt};

/// C#: CombatStatus.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub enum Status {
    InProgress = 0,
    Won = 1,
    Lost = 2,
}

/// C#: CombatAction. "play:CARD[:TARGET]", "potion:SLOT[:TARGET]", "end_turn", "pick:I,J" (the picks in order).
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub enum Action {
    Play { card: u32, target: Option<u32> },
    Potion { slot: i32, target: Option<u32> },
    EndTurn,
    Pick(Vec<i32>),
}

impl Action {
    /// C#: CombatAction.Pick, which keeps the picks in order: one answer has one form.
    pub fn pick(mut picks: Vec<i32>) -> Action {
        picks.sort_unstable();
        Action::Pick(picks)
    }

    pub fn is_end_turn(&self) -> bool {
        matches!(self, Action::EndTurn)
    }

    /// C#: CombatAction.ToString.
    pub fn text(&self) -> String {
        let target = |t: &Option<u32>| t.map(|t| format!(":{t}")).unwrap_or_default();
        match self {
            Action::Play { card, target: t } => format!("play:{card}{}", target(t)),
            Action::Potion { slot, target: t } => format!("potion:{slot}{}", target(t)),
            Action::EndTurn => "end_turn".to_string(),
            Action::Pick(picks) => {
                let picks: Vec<String> = picks.iter().map(|p| p.to_string()).collect();
                format!("pick:{}", picks.join(","))
            }
        }
    }

    /// C#: CombatAction.TryParse.
    pub fn parse(text: &str) -> Result<Action, String> {
        let bad = || format!("not a combat action: {text}");
        let parts: Vec<&str> = text.split(':').collect();
        let number = |p: &str| -> Result<u32, String> {
            if p.is_empty() || !p.bytes().all(|b| b.is_ascii_digit()) {
                return Err(bad());
            }
            p.parse::<u32>().map_err(|_| bad())
        };
        match parts.as_slice() {
            ["end_turn"] => Ok(Action::EndTurn),
            ["pick", answer] => {
                let mut picks = Vec::new();
                for p in answer.split(',').filter(|_| !answer.is_empty()) {
                    picks.push(i32::try_from(number(p)?).map_err(|_| bad())?);
                }
                Ok(Action::pick(picks))
            }
            [verb, first, rest @ ..] if rest.len() <= 1 => {
                let target = match rest {
                    [t] => Some(number(t)?),
                    _ => None,
                };
                match *verb {
                    "play" => Ok(Action::Play { card: number(first)?, target }),
                    "potion" => Ok(Action::Potion { slot: i32::try_from(number(first)?).map_err(|_| bad())?, target }),
                    _ => Err(bad()),
                }
            }
            _ => Err(bad()),
        }
    }

    fn of(play: &protocol::CombatAction) -> Action {
        match *play {
            protocol::CombatAction::Play { card, target } => Action::Play { card, target },
            protocol::CombatAction::Potion { slot, target } => Action::Potion { slot, target },
            protocol::CombatAction::EndTurn => Action::EndTurn,
        }
    }
}

/// A card prompt crossed on the node's clone, or the combat's end there (C#: PromptRoot's Place): a plan rooted at a
/// card prompt stands on the clone until the player can play again, and the engine's from there.
pub struct Crossed {
    /// The answers from the plan's prompt.
    pub path: Vec<Action>,
    pub status: Status,
    pub obs: Arc<CombatObservation>,
    pub run: RunChange,
    pub hidden_key: u64,
}

/// What stands after a path of answers from the plan's prompt (C#: PromptRoot.Crossing): asked of the C# side, from any
/// thread.
pub trait Crossing: Send + Sync {
    /// A prompt or the combat's end, crossed; or where the player can play again, the combat taken in by the engine.
    fn at(&self, path: &[Action]) -> Result<Place, String>;
}

/// Where a path of answers leads.
pub enum Place {
    Crossed(Arc<Crossed>),
    Engine(Box<Live>),
}

/// The world a state stands in.
#[derive(Clone)]
enum World {
    Engine(Box<Live>),
    Crossed(Arc<Crossed>, Arc<dyn Crossing>),
    #[cfg(test)]
    Toy(Box<dyn ToyWorld>),
}

/// A combat written by hand for the search's tests (C#: the tests' ICombatWorld toys, SlPlannerTests.Toy and
/// LoopSearchTests.Loop): what the search reads of a world, and its plays.
#[cfg(test)]
pub trait ToyWorld: Send + Sync {
    /// C#: ICombatWorld.Observe, with the prompt the combat waits on.
    fn observe(&self) -> CombatObservation;
    /// C#: ICombatWorld.Status.
    fn status(&self) -> Status;
    /// C#: ICombatWorld.HiddenKey.
    fn hidden_key(&self) -> u64;
    /// C#: ICombatWorld.Legal.
    fn legal(&self) -> Vec<Action>;
    /// C#: ICombatWorld.Execute: an error where the combat does not take the action.
    fn execute(&mut self, action: &Action) -> Result<(), String>;
    /// C#: ICombatWorld.Run.
    fn run(&self) -> RunChange;
    /// A copy that goes on apart from this one.
    fn boxed(&self) -> Box<dyn ToyWorld>;
    /// The ids of each enemy's powers that are debuffs, as `State::enemy_debuffs` reads them of the engine's world.
    fn debuffs(&self) -> Vec<Vec<Str>> {
        Vec::new()
    }
}

#[cfg(test)]
impl Clone for Box<dyn ToyWorld> {
    fn clone(&self) -> Self {
        self.boxed()
    }
}

/// A state the search reached, and what it reads of it.
pub struct State {
    world: World,
    /// Whether the world is a NoSL sample (`sz_world_import`'s `sample`): its hidden key is then `HiddenState.Key`.
    sample: bool,
    pub obs: Arc<CombatObservation>,
    pub status: Status,
    /// The observation's hash, the text C# writes of it (C#: ProtocolJson.Hash, `TurnEnd.Shown`).
    pub hash: u64,
    /// C#: PimcBrain.StateKey and ICombatWorld.HiddenKey; 0 once the combat is over. At a prompt a step came to, the
    /// hidden key holds where the step came from too (`step`).
    pub state_key: u64,
    pub hidden_key: u64,
}

fn hash_of(obs: &CombatObservation) -> u64 {
    let mut hash = Fnv::new();
    protocol::observation_json(obs, &mut hash);
    hash.0
}

impl State {
    /// The state a world stands at (C#: RunSession.Combat, then `sz_node`): the observation with the prompt it waits on,
    /// the status, the two keys and the observation's hash.
    pub fn new(mut world: Live, sample: bool) -> State {
        let asked = world.asked().cloned();
        let c = world.combat();
        let (st, player) = crate::ffi::live(c);
        let (mut obs, draw) = observe::seen(st, player, c);
        obs.prompt = asked.map(|asked| prompt::canonical(&asked, c).0);
        let status = match legal::status(player, c) as i32 {
            0 => Status::InProgress,
            1 => Status::Won,
            _ => Status::Lost,
        };
        let (state_key, hidden_key) = if status == Status::InProgress {
            let hidden = if sample { keys::key(st, player, &draw, c) } else { keys::full(st, player, &draw, c) };
            (protocol::state_key(&obs), hidden)
        } else {
            (0, 0)
        };
        let hash = hash_of(&obs);
        State {
            world: World::Engine(Box::new(world)),
            sample,
            obs: Arc::new(obs),
            status,
            hash,
            state_key,
            hidden_key,
        }
    }

    /// The state where a path of answers leads (C#: PromptRoot, at its place), in the SL world.
    pub fn at(place: Place, crossing: &Arc<dyn Crossing>) -> State {
        let crossed = match place {
            Place::Engine(world) => return State::new(*world, false),
            Place::Crossed(crossed) => crossed,
        };
        let going = crossed.status == Status::InProgress;
        State {
            obs: crossed.obs.clone(),
            status: crossed.status,
            hash: hash_of(&crossed.obs),
            state_key: if going { protocol::state_key(&crossed.obs) } else { 0 },
            hidden_key: if going { crossed.hidden_key } else { 0 },
            sample: false,
            world: World::Crossed(crossed, crossing.clone()),
        }
    }

    /// The state a toy world stands at, read as `new` reads the engine's.
    #[cfg(test)]
    pub fn toy(world: Box<dyn ToyWorld>) -> State {
        let obs = world.observe();
        let status = world.status();
        let going = status == Status::InProgress;
        State {
            hash: hash_of(&obs),
            state_key: if going { protocol::state_key(&obs) } else { 0 },
            hidden_key: if going { world.hidden_key() } else { 0 },
            obs: Arc::new(obs),
            status,
            sample: false,
            world: World::Toy(world),
        }
    }

    /// The state after `action`, made from this one, which stays as it is (C#: ICombatWorld.Execute on a world whose
    /// snapshot stays). At a prompt crossed on the clone, the action is an answer to it.
    ///
    /// A state at a prompt stands inside an action, which goes on from where the action began, not from the combat as
    /// it stops there: what the action worked out before the prompt is in neither key of it (the turn's draw counts its
    /// cards before it draws, Hook.ModifyHandDraw, and the shuffle in the middle of the draw asks Stratagem's choice).
    /// Two lines whose combats stop there alike can go on apart: in sl-regent-g0's run 6706286 a turn's search took
    /// one's answer for the other's, and the solver played a pick the prompt did not have. So such a state's hidden key
    /// holds the state the step came from, exactly, and the action too.
    pub fn step(&mut self, action: &Action) -> Result<State, String> {
        use crate::protocol::Sink as _;
        let mut after = self.stepped(action)?;
        if after.status == Status::InProgress && after.obs.prompt.is_some() {
            let mut from = Fnv(super::turn::mix(super::turn::mix(self.state_key, self.hidden_key), self.exact()));
            from.put(action.text().as_bytes());
            after.hidden_key = super::turn::mix(after.hidden_key, from.0);
        }
        Ok(after)
    }

    fn stepped(&mut self, action: &Action) -> Result<State, String> {
        if super::ffi::stopped() {
            return Err(super::ffi::STOPPED.into());
        }
        match &mut self.world {
            World::Engine(world) => {
                let after = world.step(&action.text())?;
                Ok(State::new(after, self.sample))
            }
            World::Crossed(crossed, crossing) => {
                let accepted = match (&crossed.obs.prompt, action) {
                    (Some(prompt), Action::Pick(picks)) => crossed.status == Status::InProgress && prompt.accepts(picks),
                    _ => false,
                };
                if !accepted {
                    return Err(format!("{} is no answer to the card prompt the combat waits on", action.text()));
                }
                let mut path = crossed.path.clone();
                path.push(action.clone());
                let crossing = crossing.clone();
                Ok(State::at(crossing.at(&path)?, &crossing))
            }
            #[cfg(test)]
            World::Toy(world) => {
                let mut after = world.boxed();
                after.execute(action)?;
                Ok(State::toy(after))
            }
        }
    }

    /// The state as it stands, apart from this one: the world is plain data, a copy shares its objects until either
    /// writes (C#: a snapshot taken there).
    pub fn copy(&self) -> State {
        State {
            world: self.world.clone(),
            sample: self.sample,
            obs: self.obs.clone(),
            status: self.status,
            hash: self.hash,
            state_key: self.state_key,
            hidden_key: self.hidden_key,
        }
    }

    /// The plays the game allows where the player can play, in the game's order; none at a prompt or once the combat
    /// is over (C#: ICombatWorld.Legal).
    pub fn legal(&mut self) -> Vec<Action> {
        #[cfg(test)]
        if let World::Toy(world) = &self.world {
            return world.legal();
        }
        let World::Engine(world) = &mut self.world else {
            return Vec::new();
        };
        let at_prompt = world.asked().is_some();
        let c = world.combat();
        let (st, player) = crate::ffi::live(c);
        legal::plays(st, player, at_prompt, c).iter().map(Action::of).collect()
    }

    /// What the combat has changed of the run so far (C#: ICombatWorld.Run, the run given at the import over it).
    pub fn run(&mut self) -> RunChange {
        match &mut self.world {
            World::Engine(world) => {
                let c = world.combat();
                let (_, player) = crate::ffi::live(c);
                observe::run(player, c)
            }
            World::Crossed(crossed, _) => crossed.run.clone(),
            #[cfg(test)]
            World::Toy(world) => world.run(),
        }
    }

    /// The ids of each enemy's visible powers the game declares debuffs, enemy by enemy as the observation lists them
    /// (`observe::enemy_debuffs`); none where the search keeps no world (a prompt crossed on the clone).
    pub fn enemy_debuffs(&mut self) -> Vec<Vec<Str>> {
        match &mut self.world {
            World::Engine(world) => {
                let c = world.combat();
                let (st, _) = crate::ffi::live(c);
                observe::enemy_debuffs(st, c)
            }
            World::Crossed(..) => Vec::new(),
            #[cfg(test)]
            World::Toy(world) => world.debuffs(),
        }
    }

    /// The round of the observation.
    pub fn round(&self) -> i32 {
        self.obs.round
    }

    /// The world exactly, as far as a line played on from here depends on it: the observation's hash; every card of the
    /// player's piles (hand, draw, discard, exhaust, play) by its id and its own cost (`own_cost`), in each pile's order;
    /// and the counters that name what comes later: the card prompts asked so far (a prompt's number orders its options
    /// that look the same, so the same `pick` takes another copy), the next card's id and the next creature's. The two
    /// keys tell copies of a card apart by nothing and hold none of the counters, and the observation shows the uid of a
    /// card in hand only, so two lines can reach one key and one observation with the copies laid out otherwise in the
    /// draw pile, or after another number of prompts; a line played on from one then plays a copy the other does not
    /// hold. Where the search keeps no world (a prompt crossed on the clone, a toy) or the combat is over, the
    /// observation's hash.
    pub fn exact(&mut self) -> u64 {
        use crate::r#gen as g;
        use crate::protocol::Sink as _;
        let World::Engine(world) = &mut self.world else {
            return self.hash;
        };
        if self.status != Status::InProgress {
            return self.hash;
        }
        let c = world.combat();
        let (st, player) = crate::ffi::live(c);
        let mut hash = Fnv(self.hash);
        let pcs = g::entities_players::player::player_combat_state(player, c);
        let db = g::game_actions_multiplayer::net_combat_card_db::instance(c);
        hash.put(&c.x.prompts.to_le_bytes());
        hash.put(&g::state::NetCombatCardDbData::of(db, c)._next_id.to_le_bytes());
        hash.put(&g::state::CombatStateData::of(st, c)._next_creature_id.to_le_bytes());
        let piles = [
            g::entities_players::player_combat_state::hand(pcs, c),
            g::entities_players::player_combat_state::draw_pile(pcs, c),
            g::entities_players::player_combat_state::discard_pile(pcs, c),
            g::entities_players::player_combat_state::exhaust_pile(pcs, c),
            g::entities_players::player_combat_state::play_pile(pcs, c),
        ];
        for pile in piles {
            for card in g::entities_cards::card_pile::cards(pile, c) {
                let (known, id) = g::game_actions_multiplayer::net_combat_card_db::try_get_card_id(db, card, c);
                hash.put(&(if known { id } else { u32::MAX }).to_le_bytes());
                own_cost(card, c, &mut hash);
            }
            hash.put(b"|");
        }
        hash.0
    }
}

/// A card's own cost, which the observation shows only with what other models change of it: its energy cost's base and
/// each change made to the card itself, and its star cost's. With Pounce's free skill, a Defend made free for the combat
/// (Touch of Insanity) beside a Survivor free by the power shows as the Survivor made free beside a Defend free by it;
/// once a skill is played the power is gone and only the card made free stays so (sl-silent-g0, seed FER38JLYFJKL).
fn own_cost(card: crate::r#gen::kinds::CardModel, c: &mut crate::rt::Combat, hash: &mut Fnv) {
    use crate::r#gen as g;
    use crate::protocol::Sink as _;
    let energy = g::models::card_model::energy_cost(card, c);
    let base_stars = g::models::card_model::base_star_cost(card, c);
    let cost = g::state::CardEnergyCostData::of(energy, c);
    hash.put(&cost._base.to_le_bytes());
    for &modifier in &cost._local_modifiers {
        let modifier = g::state::LocalCostModifierData::of(modifier, c);
        hash.put(&modifier.amount.to_le_bytes());
        hash.put(&modifier.type_.0.to_le_bytes());
        hash.put(&modifier.expiration.0.to_le_bytes());
        hash.put(&[modifier.is_reduce_only as u8]);
    }
    hash.put(b";");
    hash.put(&base_stars.to_le_bytes());
    for &temporary in &g::state::CardModelData::of(card, c)._temporary_star_costs {
        let temporary = g::state::TemporaryCardCostData::of(temporary, c);
        hash.put(&temporary.cost.to_le_bytes());
        hash.put(&[temporary.clears_when_turn_ends as u8, temporary.clears_when_card_is_played as u8]);
    }
    hash.put(b";");
}

/// A combat's public record of enemy moves (C#: MoveRecord's History): what each living enemy showed at the first
/// decision of each round, by uid, in the order the enemies first showed up.
pub type History = Vec<EnemyTurns>;

/// C#: `new MoveRecord(history)` then `Witness(obs)`: the record that goes on from `history` with the observation at
/// a decision. On the round's first, each living enemy's intents join it; a round that goes back starts it over.
pub fn witness(history: &History, obs: &CombatObservation) -> History {
    let round = history.iter().flat_map(|e| e.turns.iter().map(|t| t.round)).max().unwrap_or(-1);
    if obs.round == round {
        return history.clone();
    }
    let mut record = if obs.round < round { Vec::new() } else { history.clone() };
    for enemy in &obs.enemies {
        let (Some(uid), Some(intents)) = (enemy.uid, &enemy.intents) else {
            continue;
        };
        if enemy.hp <= 0 {
            continue;
        }
        let turn = EnemyTurn { round: obs.round, intents: intents.clone() };
        match record.iter_mut().find(|e| e.uid == uid) {
            Some(known) => known.turns.push(turn),
            None => record.push(EnemyTurns { uid, id: enemy.id, turns: vec![turn] }),
        }
    }
    record
}

/// The hash of a record of enemy moves, by its JSON as C# writes it (C#: ProtocolJson.Hash of the list).
pub fn history_hash(history: &History) -> u64 {
    let mut hash = Fnv::new();
    protocol::history_json(history, &mut hash);
    hash.0
}
