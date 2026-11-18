//! MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext: the choices of a hook the game runs with a
//! context of its own for each listener (`Hook.AfterDeath`, `AfterSideTurnEnd`, a turn start's setup, ...).
//!
//! In the game a hook that asks the player for the first time waits there: its context makes a hook action
//! (`GenericHookGameAction`), enqueues it, and lets the code that ran the hook go on (the next listeners, the rest of
//! the action or of the turn). Its awaits go on at once on the game's one thread, so what follows depends on the
//! action executor. Busy with an action, the executor runs the hook's action after it: the hook's rest, its prompt
//! with it, waits until then. Idle, the executor starts the action at once, and the hook goes on to its prompt (reading
//! the options then) before the code that ran it goes on; the player sees the prompt when that code has gone as far
//! as it goes, and the hook's rest runs after the answer. A turn start's setup does this by hand
//! (manual/combat_manager.rs, `start_turn`: the rest of the turn start runs where the setup pauses).
//!
//! Every other hook does it here. Busy: at its first choice the hook stops (`Stop::Paused`, which its context's wait
//! takes) and its action is enqueued. Idle: the hook goes on to its prompt, which is asked there (`ask`) and the hook
//! stops then. When the executor runs the action (an idle executor's, the next time it runs), the step is played again
//! from the combat it started from, with the same answers, up to the hook's choice or prompt, and goes on from there in
//! the combat as it stands now (`resume`): the hook's rest runs in its own frames, on the state the rest of the step
//! has made, as in the game. A prompt asked at once with no answer yet stops the step there and then.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use crate::r#gen as g;
use crate::r#gen::enums::{GameActionState, PlayerChoiceOptions};
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::rt::*;

/// The hooks that asked the player in the running step: part of the combat, so a replay of the step counts them the
/// same way.
#[derive(Clone, Debug, Default)]
pub struct Hooks {
    /// How many hooks have paused in this step so far: a paused hook's number, by which a replay finds it again.
    pub paused: u32,
    /// The contexts that have asked once: a hook that asks again only asks (C#: its game action is set).
    pub asked: Vec<HookPlayerChoiceContext>,
    /// The contexts of the hooks that paused in this step, with each one's number.
    pub waiting: Vec<(HookPlayerChoiceContext, u32)>,
    /// A hook action an idle executor started at once when it was enqueued: the hook's rest runs the next time the
    /// executor runs (manual/action_executor.rs), once the code that paused the hook has gone on.
    pub started: GameAction,
    /// The hook whose action was started that way, while it goes on to its prompt (`ask`), and its number.
    pub asking: HookPlayerChoiceContext,
    pub asking_number: u32,
    /// The hooks that asked that way and wait with their prompt already asked.
    pub asked_at_once: Vec<HookPlayerChoiceContext>,
    /// The contexts whose paused hook a replay runs to its end in the step's combat: that end ends the replay.
    pub resumed: Vec<HookPlayerChoiceContext>,
    /// Set when a hook paused in a step run without its start (`Combat.x.step`): the step is run again with it.
    pub restart: bool,
}

/// What a world's step started from (world.rs): the combat before it and the action, or a resume point where the
/// turn loop goes on (`action` none: the combat is the point's, which says how many answers were taken before it).
pub struct StepStart {
    pub before: Combat,
    pub action: Option<String>,
}

impl std::fmt::Debug for StepStart {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match &self.action {
            Some(action) => write!(f, "StepStart({action})"),
            None => write!(f, "StepStart(resume point)"),
        }
    }
}

/// A replay's target: the number of the paused hook whose rest it runs, the combat to go on in from that hook's
/// choice, and the flag set when it has gone over to it.
#[derive(Clone)]
pub struct Resume {
    pub target: u32,
    pub state: Combat,
    pub taken: Arc<AtomicBool>,
}

impl std::fmt::Debug for Resume {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Resume({})", self.target)
    }
}

/// What the running code keeps in the combat beside the game's state: it goes with the code, not with the combat it
/// runs in, when a replay goes over to another combat or hands it back (`resume`).
struct Running {
    entries: i32,
    entry: Option<super::card_select_cmd::Entry>,
    vakuu: i32,
    turn_start: Option<super::combat_manager::TurnStart>,
    only: Option<&'static Only>,
    points: bool,
    resume_point: Option<Arc<Combat>>,
}

impl Running {
    fn take(c: &mut Combat) -> Running {
        Running {
            entries: c.x.entries,
            entry: c.x.entry.take(),
            vakuu: c.x.vakuu,
            turn_start: c.x.turn_start.take(),
            only: c.only.take(),
            points: c.x.points,
            resume_point: c.x.resume_point.take(),
        }
    }

    fn put(self, c: &mut Combat) {
        c.x.entries = self.entries;
        c.x.entry = self.entry;
        c.x.vakuu = self.vakuu;
        c.x.turn_start = self.turn_start;
        c.only = self.only;
        c.x.points = self.points;
        c.x.resume_point = self.resume_point;
    }
}

/// C#: HookPlayerChoiceContext.AssignTaskAndWaitForPauseOrCompletion: true when the task ran to its end, false when
/// its hook paused for the player. In a replay, the end of the hook it resumes ends the replay.
pub fn assign_task_and_wait_for_pause_or_completion(
    me: HookPlayerChoiceContext,
    task: R<()>,
    c: &mut Combat,
) -> R<bool> {
    match task {
        Err(Stop::Paused(context)) if context == me => Ok(false),
        Err(stop) => Err(stop),
        Ok(()) => {
            if c.x.hooks.resumed.last() == Some(&me) {
                c.x.hooks.resumed.pop();
                return Err(Stop::Resumed(me));
            }
            // A hook whose action the executor started at once and that ran to its end without asking: the action
            // ends with it, before the code that ran the hook goes on.
            let action = HookPlayerChoiceContextData::of(me, c)._game_action;
            if !action.is_null() && c.x.hooks.started == action {
                c.x.hooks.asking = Obj::NULL;
                let executor = RunManagerData::of(c.s.run_manager__instance, c).action_executor;
                super::action_executor::end_started(executor, action, c)?;
            }
            Ok(true)
        }
    }
}

/// C#: HookPlayerChoiceContext.WaitForPauseOrCompletionWithoutAssigningTask: a turn start's setup, which pauses by
/// hand (manual/combat_manager.rs).
pub fn wait_for_pause_or_completion_without_assigning_task(
    _me: HookPlayerChoiceContext,
    task: R<()>,
    _c: &mut Combat,
) -> R<bool> {
    task?;
    Ok(true)
}

/// C#: HookPlayerChoiceContext.WaitForCompletion: a hook that waits for the player has not completed; `Task.WhenAll`
/// runs its action (rt::when_all, `wait`).
pub fn wait_for_completion(me: HookPlayerChoiceContext, c: &mut Combat) -> R<()> {
    let action = HookPlayerChoiceContextData::of(me, c)._game_action;
    if !action.is_null() && GameActionData::of(action, c).state != GameActionState::Finished {
        return Err(Stop::Pending(me));
    }
    Ok(())
}

/// `Task.WhenAll` over hooks that wait for the player: the game's executor runs their actions while the code that
/// awaits them waits, and it goes on once they are done.
pub fn wait(contexts: &[HookPlayerChoiceContext], c: &mut Combat) -> R<()> {
    let executor = RunManagerData::of(c.s.run_manager__instance, c).action_executor;
    super::action_executor::execute_actions(executor, c)?;
    for &context in contexts {
        let action = HookPlayerChoiceContextData::of(context, c)._game_action;
        if GameActionData::of(action, c).state != GameActionState::Finished {
            panic!("Task.WhenAll awaits a hook whose action the executor has not run: {action:?} (not ported)");
        }
    }
    Ok(())
}

/// C#: HookPlayerChoiceContext.SignalPlayerChoiceBegun. The first choice of a turn start's setup pauses it: the rest
/// of the turn start runs here, before the choice is asked. The first choice of any other hook pauses it: its action
/// is enqueued and the hook stops (`Stop::Paused`), unless this is the replay that resumes it, which goes over to the
/// step's combat here and asks. A hook that has asked before only asks.
pub fn signal_player_choice_begun(
    me: HookPlayerChoiceContext,
    chooser: Player,
    _options: PlayerChoiceOptions,
    c: &mut Combat,
) -> R<()> {
    if c.x.hooks.asked.contains(&me) {
        return Ok(());
    }
    c.x.hooks.asked.push(me);
    if crate::manual::combat_manager::turn_start_pauses(me, c) {
        crate::manual::combat_manager::turn_start_rest(c)?;
        return Ok(());
    }
    // A world's step keeps what it started from only once a hook has paused in it (world.rs).
    if c.x.step.is_none() {
        c.x.hooks.restart = true;
        return Err(Stop::Restart);
    }
    let number = c.x.hooks.paused;
    c.x.hooks.paused += 1;
    // C#: ActionQueueSynchronizer.GenerateHookAction, GenericHookGameAction.SetChoiceContext, RequestEnqueueHookAction
    // (one player: EnqueueHookAction, EnqueueOrHoldUntilCombatEnd).
    let synchronizer = RunManagerData::of(c.s.run_manager__instance, c).action_queue_synchronizer;
    let id = ActionQueueSynchronizerData::of(synchronizer, c)._next_hook_id;
    ActionQueueSynchronizerData::of_mut(synchronizer, c)._next_hook_id = id.wrapping_add(1);
    let owner = g::entities_players::player::net_id(chooser, c);
    let kind = HookPlayerChoiceContextData::of(me, c)._game_action_type;
    let action = g::game_actions::generic_hook_game_action::new(id, owner, kind, c);
    g::game_actions::generic_hook_game_action::set_choice_context(action, me, c);
    HookPlayerChoiceContextData::of_mut(me, c)._game_action = action;
    c.x.hooks.waiting.push((me, number));
    g::game_actions_multiplayer::action_queue_synchronizer::enqueue_or_hold_until_combat_end(synchronizer, action, c);
    let executor = RunManagerData::of(c.s.run_manager__instance, c).action_executor;
    if super::action_executor::start_at_once(executor, action, c) {
        // The executor was idle and started the action at once: the game's hook goes on here to its prompt, which it
        // asks before the code that ran the hook goes on (`ask`), or to its end.
        c.x.hooks.asking = me;
        c.x.hooks.asking_number = number;
        return Ok(());
    }
    if c.x.resume.as_ref().is_some_and(|r| r.target == number) {
        take_over(me, c);
        return Ok(());
    }
    Err(Stop::Paused(me))
}

/// The replay that resumes the paused hook `me` goes over to the step's combat here, where the hook goes on.
fn take_over(me: HookPlayerChoiceContext, c: &mut Combat) {
    let resume = c.x.resume.take().expect("a replay with nothing to take over");
    let running = Running::take(c);
    *c = resume.state;
    running.put(c);
    resume.taken.store(true, Ordering::Relaxed);
    c.x.hooks.resumed.push(me);
}

/// The prompt of a hook whose action the executor started at once (`signal_player_choice_begun`): the game asks it now,
/// from what the combat holds now, and the code that ran the hook goes on while the prompt waits; the player sees it
/// when the executor would go on (the hook's rest, `resume`). The answer is taken there, in a replay that takes the
/// step's combat over here.
pub fn ask(me: HookPlayerChoiceContext, options: Vec<CardModel>, min_select: i32, max_select: i32, c: &mut Combat) -> R<Vec<CardModel>> {
    c.x.hooks.asking = Obj::NULL;
    if c.x.resume.as_ref().is_some_and(|r| r.target == c.x.hooks.asking_number) {
        take_over(me, c);
        let picks = c.choices.take().expect("a hook resumed at its prompt without its answer");
        return Ok(picks.iter().map(|&i| options[i as usize]).collect());
    }
    let number = c.x.prompts;
    c.x.prompts += 1;
    if c.choices.next == c.choices.answers.len() {
        c.x.asked = Some(super::i_card_selector::asked(options, min_select, max_select, number, c));
    }
    c.x.hooks.asked_at_once.push(me);
    Err(Stop::Paused(me))
}

/// C#: HookPlayerChoiceContext.SignalPlayerChoiceEnded.
pub fn signal_player_choice_ended(_me: HookPlayerChoiceContext, _c: &mut Combat) -> R<()> {
    Ok(())
}

/// C#: GenericHookGameAction.ExecuteAction: the rest of the hook that paused for this action. The step is played
/// again from where it started, with the answers given so far, up to the hook's choice; there the replay goes over to
/// the combat as it stands now and runs the hook to its end (or to a prompt with no answer yet), and that combat comes
/// back here.
pub fn resume(me: HookPlayerChoiceContext, c: &mut Combat) -> R<()> {
    let target = c
        .x
        .hooks
        .waiting
        .iter()
        .find(|w| w.0 == me)
        .unwrap_or_else(|| panic!("the action of a hook that did not pause in this step: {me:?}"))
        .1;
    // A hook that asked at once, with no answer given yet: the step stops at its prompt, asked then (`ask`).
    if c.x.hooks.asked_at_once.contains(&me) && c.choices.next == c.choices.answers.len() {
        return Err(Stop::Choice);
    }
    let step = c.x.step.clone().expect("a hook paused outside a world's step");
    let taken = Arc::new(AtomicBool::new(false));
    let mut replay = step.before.clone();
    // From the step's action, with no answer taken and no hook asked yet; from a resume point, as the point stood.
    replay.choices = Choices {
        answers: c.choices.answers.clone(),
        next: if step.action.is_some() { 0 } else { step.before.choices.next },
        stopped: false,
    };
    if step.action.is_some() {
        replay.x.hooks = Hooks::default();
    }
    // A replay takes no resume point: the step it replays keeps its own.
    replay.x.points = false;
    replay.x.resume_point = None;
    replay.x.step = Some(step.clone());
    replay.x.resume = Some(Box::new(Resume {
        target,
        state: c.clone(),
        taken: taken.clone(),
    }));
    let ran = crate::world::replay(&step, &mut replay);
    if !taken.load(Ordering::Relaxed) {
        panic!("a replay of {step:?} ended ({ran:?}) before the hook it resumes asked");
    }
    let running = Running::take(c);
    *c = replay;
    running.put(c);
    match ran {
        Err(Stop::Resumed(context)) if context == me => Ok(()),
        Ok(()) => panic!("a replay of {step:?} went on past the end of the hook it resumes"),
        Err(stop) => Err(stop),
    }
}
