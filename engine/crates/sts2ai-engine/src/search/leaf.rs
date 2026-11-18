//! What a search leaf is worth (C#: ICombatLeaf, LeafState, LeafOutcome) and the combat net that says so (C#:
//! CombatNet, where its reasons are): the graph read by ONNX Runtime, its endings priced by V, which the C# side gives
//! (`Price`), every belt worth the best of it with any of its potions thrown away (C#: FreeDisposal).

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock, RwLock};

use super::state::{History, Status};
use super::tokens::{self, Data, Index};
use crate::protocol::{self, CombatObservation, Fnv, RunChange};
use crate::rt::Str;

/// C#: CombatNet.Batch. How many states the net is asked about in one evaluation.
pub const BATCH: usize = 16;

/// C#: LeafState. A state a leaf is asked about: its status, its observation, what the combat has changed of the run
/// there, and the record of enemy moves up to it.
pub struct LeafState {
    pub status: Status,
    pub obs: Arc<CombatObservation>,
    pub run: RunChange,
    pub history: Arc<History>,
}

/// C#: LeafOutcome. The value of a state, the chance to win the combat from it, and the HP held at the end of a win.
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct LeafOutcome {
    pub value: f64,
    pub win: f64,
    pub hp: f64,
}

/// C#: ICombatLeaf.
pub trait Leaf {
    /// The outcome of one state, from one evaluation.
    fn outcome(&self, state: &LeafState) -> Result<LeafOutcome, String>;

    /// The outcomes of several states, in their order: the net is asked about `BATCH` of them at a time.
    fn outcomes(&self, states: &[&LeafState]) -> Result<Vec<LeafOutcome>, String>;
}

/// C#: Math.Max of doubles (.NET Core 3.0 and after): NaN wins, and +0 over -0.
pub fn max(a: f64, b: f64) -> f64 {
    if a != b {
        if !a.is_nan() {
            return if b < a { a } else { b };
        }
        return a;
    }
    if b.is_sign_negative() { a } else { b }
}

/// C#: FreeDisposal.Kept. Each belt `potions` can be left as, by the mask of the potions held that it keeps: bit i keeps
/// the i-th potion held, in slot order. The last keeps them all.
pub fn kept(potions: &[Option<Str>]) -> Vec<Vec<Option<Str>>> {
    let held: Vec<usize> = (0..potions.len()).filter(|&s| potions[s].is_some()).collect();
    (0..1usize << held.len())
        .map(|mask| {
            let mut belt = potions.to_vec();
            for (i, &slot) in held.iter().enumerate() {
                if (mask >> i) & 1 == 0 {
                    belt[slot] = None;
                }
            }
            belt
        })
        .collect()
}

/// C#: FreeDisposal.Spread. Values by the mask of potions kept, each made the best of its own and its subsets'.
pub fn spread(by_mask: &mut [f64]) {
    let mut bit = 1;
    while bit < by_mask.len() {
        for mask in 0..by_mask.len() {
            if mask & bit != 0 {
                by_mask[mask] = max(by_mask[mask], by_mask[mask ^ bit]);
            }
        }
        bit <<= 1;
    }
}

/// C#: FreeDisposal.Best.
fn best(potions: &[Option<Str>], mut value: impl FnMut(&[Option<Str>]) -> Result<f64, String>) -> Result<f64, String> {
    let belts = kept(potions);
    let mut values = Vec::with_capacity(belts.len());
    for belt in &belts {
        values.push(value(belt)?);
    }
    spread(&mut values);
    Ok(*values.last().expect("a belt at least"))
}

// ---------------------------------------------------------------------------------------------------------------------
// The net
// ---------------------------------------------------------------------------------------------------------------------

/// C#: CombatNetFile, but for its graph and its tests: what the C# side gives of it.
#[derive(serde::Deserialize)]
pub struct NetFile {
    pub name: String,
    pub buckets: i32,
    pub loss_min: f64,
    pub lags: i32,
    pub vocab: HashMap<String, Vec<String>>,
    pub potion_groups: i32,
}

/// C#: CombatNet's Kept. How many of V's values the net keeps; past it they are dropped and valued again as needed.
const KEPT: usize = 200_000;

/// The price table's parts: one lock each, so threads pricing apart seldom wait on each other.
const SHARDS: usize = 64;

/// V's value of a run at an HP with a belt (C#: CombatNet's `_prices`), by the run's key (`Net::key`), the HP and the
/// belt.
type PriceKey = (u64, i32, Vec<Option<Str>>);

/// The combat net loaded: its file, its token tables, its graph's sessions, and V's values it priced.
pub struct Net {
    pub file: NetFile,
    index: HashMap<String, HashMap<String, i64>>,
    graph: Vec<u8>,
    /// Sessions of the graph, each run by one thread at a time; one more is made when every one is in use.
    sessions: Mutex<Vec<ort::session::Session>>,
    prices: Vec<RwLock<HashMap<PriceKey, f64>>>,
}

/// An error of ONNX Runtime's, on the combat net's graph.
fn failed(e: impl std::fmt::Display) -> String {
    format!("the combat net's graph: {e}")
}

/// ONNX Runtime, loaded from the library the C# side runs too.
static ORT: OnceLock<Result<ort::environment::Environment, String>> = OnceLock::new();

/// ONNX Runtime from `library`, once for the process: the first library given is the one.
fn runtime(library: &str) -> Result<&'static ort::environment::Environment, String> {
    ORT.get_or_init(|| {
        let builder = ort::init_from(library).map_err(|e| format!("ONNX Runtime at {library}: {e}"))?;
        builder.with_name("sts2ai-search").build().map_err(|e| format!("ONNX Runtime's environment: {e}"))
    })
    .as_ref()
    .map_err(|e| e.clone())
}

/// What the C# side prices a run by: V of the run given at the import with `change` over it. The C# side answers
/// with an error where V fails.
pub type Price<'a> = dyn Fn(&RunChange) -> Result<f64, String> + Sync + 'a;

impl Net {
    /// The net of a file and its graph (ONNX), read by the ONNX Runtime library at `library`.
    pub fn load(file: NetFile, graph: Vec<u8>, library: &str) -> Result<Net, String> {
        runtime(library)?;
        let index = file
            .vocab
            .iter()
            .map(|(table, ids)| (table.clone(), ids.iter().enumerate().map(|(i, id)| (id.clone(), i as i64)).collect()))
            .collect();
        let net = Net {
            file,
            index,
            graph,
            sessions: Mutex::new(Vec::new()),
            prices: (0..SHARDS).map(|_| RwLock::new(HashMap::new())).collect(),
        };
        let first = net.session()?;
        net.sessions.lock().expect("sessions").push(first);
        Ok(net)
    }

    /// A session of the graph, as C# makes its one: one thread for a call, the default optimizations.
    fn session(&self) -> Result<ort::session::Session, String> {
        let env = ORT.get().and_then(|r| r.as_ref().ok()).ok_or("ONNX Runtime is not loaded")?;
        ort::session::Session::builder(env)
            .map_err(failed)?
            .with_intra_threads(1)
            .map_err(failed)?
            .with_inter_threads(1)
            .map_err(failed)?
            .commit_from_memory(&self.graph)
            .map_err(failed)
    }

    /// The leaf of a search with this net, its endings priced by `price`; `base` tells apart the runs the C# side
    /// prices over (the run at the import), for the prices the net keeps across searches.
    pub fn leaf<'a>(&'a self, base: u64, price: &'a Price<'a>) -> NetLeaf<'a> {
        NetLeaf {
            net: self,
            base,
            price,
            keyed: std::cell::RefCell::new(None),
        }
    }

    /// The graph's outputs for each of the states, asked as one batch (C#: CombatNet.Graphs): per state, the endings'
    /// chances and the potions' chances of being gone.
    pub fn graphs(&self, states: &[(&CombatObservation, &History)]) -> Result<Vec<Vec<Vec<f32>>>, String> {
        let lags = self.file.lags as usize;
        let fed: Vec<Vec<tokens::Input>> = states.iter().map(|(obs, history)| tokens::feed(&tokens::of(obs, history, lags), lags, self)).collect();
        let batch = tokens::stack(fed);
        let mut inputs: Vec<(std::borrow::Cow<'_, str>, ort::session::SessionInputValue<'_>)> = Vec::with_capacity(batch.len());
        for input in batch {
            let value: ort::value::DynValue = match input.data {
                Data::F32(data) => ort::value::Tensor::from_array((input.dims, data)).map_err(failed)?.into_dyn(),
                Data::I64(data) => ort::value::Tensor::from_array((input.dims, data)).map_err(failed)?.into_dyn(),
            };
            inputs.push((input.name.into(), value.into()));
        }
        let taken = self.sessions.lock().expect("sessions").pop();
        let mut session = match taken {
            Some(session) => session,
            None => self.session()?,
        };
        let outputs = (|| -> Result<Vec<Vec<f32>>, String> {
            let results = session.run(inputs).map_err(failed)?;
            let mut outputs = Vec::with_capacity(results.len());
            for (_, value) in results.iter() {
                let (_, data) = value.try_extract_tensor::<f32>().map_err(failed)?;
                outputs.push(data.to_vec());
            }
            Ok(outputs)
        })();
        self.sessions.lock().expect("sessions").push(session);
        let outputs = outputs?;
        let count = states.len();
        Ok((0..count)
            .map(|b| {
                outputs
                    .iter()
                    .map(|all| {
                        let width = all.len() / count;
                        all[b * width..(b + 1) * width].to_vec()
                    })
                    .collect()
            })
            .collect())
    }

    fn shard(&self, at: &PriceKey) -> &RwLock<HashMap<PriceKey, f64>> {
        let mut hash = Fnv::new();
        protocol::Sink::put(&mut hash, &at.0.to_le_bytes());
        protocol::Sink::put(&mut hash, &at.1.to_le_bytes());
        &self.prices[(hash.0 % SHARDS as u64) as usize]
    }
}

impl Index for Net {
    fn index(&self, table: &str, id: &str) -> i64 {
        self.index.get(table).and_then(|t| t.get(id)).copied().unwrap_or(0)
    }
}

/// The net as one search's leaf: its endings priced by that search's run (C#: CombatNet as an ICombatLeaf). One
/// thread's.
pub struct NetLeaf<'a> {
    net: &'a Net,
    base: u64,
    price: &'a Price<'a>,
    /// The run this leaf last made a key of, without its HP and potions, and the key (C#: CombatNet's `_keyed`).
    keyed: std::cell::RefCell<Option<(RunChange, u64)>>,
}

impl NetLeaf<'_> {
    /// C#: CombatNet.Key. The run without its HP and potions, hashed with the run at the import: what the leaf's prices
    /// of one state share.
    fn key(&self, run: &RunChange) -> u64 {
        let bare = |run: &RunChange| RunChange {
            hp: 0,
            potions: Vec::new(),
            max_hp: run.max_hp,
            gold: run.gold,
            deck: run.deck.clone(),
            relics: run.relics.clone(),
        };
        let mut keyed = self.keyed.borrow_mut();
        if let Some((last, key)) = keyed.as_ref()
            && last.max_hp == run.max_hp
            && last.gold == run.gold
            && last.deck == run.deck
            && last.relics == run.relics
        {
            return *key;
        }
        let without = bare(run);
        let mut hash = Fnv(self.base);
        protocol::run_json(&without, &mut hash);
        let key = hash.0;
        *keyed = Some((without, key));
        key
    }

    /// C#: CombatNet.Price. V of `run` (whose key is `key`) at `hp` with `potions`.
    fn price(&self, run: &RunChange, key: u64, hp: i32, potions: &[Option<Str>]) -> Result<f64, String> {
        let at: PriceKey = (key, hp, potions.to_vec());
        let shard = self.net.shard(&at);
        if let Some(known) = shard.read().expect("prices").get(&at) {
            return Ok(*known);
        }
        let priced = RunChange {
            hp,
            potions: potions.to_vec(),
            ..run.clone()
        };
        let value = (self.price)(&priced)?;
        let mut table = shard.write().expect("prices");
        if table.len() >= KEPT / SHARDS {
            table.clear();
        }
        table.insert(at, value);
        Ok(value)
    }

    /// C#: CombatNet.Ended. A finished combat: nothing for a death, V of its run for a win; none while it goes on.
    fn ended(&self, status: Status, run: &RunChange) -> Result<Option<LeafOutcome>, String> {
        Ok(match status {
            Status::Lost => Some(LeafOutcome { value: 0.0, win: 0.0, hp: 0.0 }),
            Status::Won => {
                let key = self.key(run);
                let value = best(&run.potions, |potions| self.price(run, key, run.hp, potions))?;
                Some(LeafOutcome {
                    value,
                    win: 1.0,
                    hp: run.hp as f64,
                })
            }
            Status::InProgress => None,
        })
    }

    /// C#: CombatNet.Priced. The value, the chance not to die, and the HP held at a win, from the graph's outputs for
    /// the state.
    fn priced(&self, run: &RunChange, outputs: &[Vec<f32>]) -> Result<LeafOutcome, String> {
        let file = &self.net.file;
        let key = self.key(run);
        let (outcome, potion_used) = (&outputs[0], &outputs[1]);
        let held: Vec<usize> = (0..run.potions.len()).filter(|&s| run.potions[s].is_some()).collect();
        let table = file.vocab.get("potions").map(|t| t.len()).unwrap_or(0);
        let per_group = (file.buckets / file.potion_groups) as usize;
        let kept_by: Vec<Vec<f64>> = (0..file.potion_groups as usize)
            .map(|g| {
                held.iter()
                    .map(|&s| {
                        let id = run.potions[s].expect("held").as_str();
                        1.0 - potion_used[g * table + self.net.index("potions", id) as usize] as f64
                    })
                    .collect()
            })
            .collect();
        let max_hp = run.max_hp.max(1);
        let share = run.hp as f64 / max_hp as f64;
        let width = (1.0 - file.loss_min) / file.buckets as f64;
        let mut value = 0.0;
        let mut left = 0.0;
        let belts = kept(&run.potions);
        for k in 1..outcome.len() {
            if outcome[k] == 0.0 {
                continue;
            }
            let after = share - (file.loss_min + (k as f64 - 0.5) * width);
            let hp = ((after * max_hp as f64).round_ties_even() as i32).clamp(1, max_hp);
            let mut prices = Vec::with_capacity(belts.len());
            for belt in &belts {
                prices.push(self.price(run, key, hp, belt)?);
            }
            spread(&mut prices);
            for (mask, price) in prices.iter().enumerate() {
                let mut chance = outcome[k] as f64;
                for (i, kept_here) in kept_by[(k - 1) / per_group].iter().enumerate() {
                    chance *= if (mask >> i) & 1 == 1 { *kept_here } else { 1.0 - kept_here };
                }
                value += chance * price;
            }
            left += outcome[k] as f64 * after;
        }
        // C#: `1 - outcome[0]`, in floats.
        let win = (1.0f32 - outcome[0]) as f64;
        Ok(LeafOutcome {
            value,
            win,
            hp: if win > 0.0 { tokens::clamp(left / win, 0.0, 1.0) * max_hp as f64 } else { 0.0 },
        })
    }
}

impl Leaf for NetLeaf<'_> {
    fn outcome(&self, state: &LeafState) -> Result<LeafOutcome, String> {
        if let Some(ended) = self.ended(state.status, &state.run)? {
            return Ok(ended);
        }
        let outputs = self.net.graphs(&[(&state.obs, &state.history)])?;
        self.priced(&state.run, &outputs[0])
    }

    fn outcomes(&self, states: &[&LeafState]) -> Result<Vec<LeafOutcome>, String> {
        let mut outcomes: Vec<Option<LeafOutcome>> = Vec::with_capacity(states.len());
        let mut going = Vec::new();
        for (i, state) in states.iter().enumerate() {
            let ended = self.ended(state.status, &state.run)?;
            if ended.is_none() {
                going.push(i);
            }
            outcomes.push(ended);
        }
        for asked in going.chunks(BATCH) {
            let batch: Vec<(&CombatObservation, &History)> = asked.iter().map(|&i| (&*states[i].obs, &*states[i].history)).collect();
            let outputs = self.net.graphs(&batch)?;
            for (k, &i) in asked.iter().enumerate() {
                outcomes[i] = Some(self.priced(&states[i].run, &outputs[k])?);
            }
        }
        Ok(outcomes.into_iter().map(|o| o.expect("every state valued")).collect())
    }
}
