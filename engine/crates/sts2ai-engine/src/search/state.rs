//! A state of the search: a world where the player is asked (their turn, a card prompt, or the combat's end), with
//! what the search reads of every state it reaches, read once when the state is made (C#: EngineWorld with the
//! `sz_node` of its step and its observation, deserialized). The actions, and the record of enemy moves (C#:
//! MoveRecord).

use std::sync::Arc;

use crate::ffi::World as _;
use crate::protocol::{self, CombatObservation, EnemyTurn, EnemyTurns, Fnv, RunChange};
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
    /// C#: PimcBrain.StateKey and ICombatWorld.HiddenKey; 0 once the combat is over.
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

    /// The state after `action`, made from this one, which stays as it is (C#: ICombatWorld.Execute on a world whose
    /// snapshot stays). At a prompt crossed on the clone, the action is an answer to it.
    pub fn step(&mut self, action: &Action) -> Result<State, String> {
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
        }
    }

    /// The round of the observation.
    pub fn round(&self) -> i32 {
        self.obs.round
    }
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
