using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// The SL record: a checkpoint, and every step taken since that took effect, in the order it did, as the simulator's
/// trace names them ("surface action"). With the checkpoint the steps are the clone the brain plans and values on.
///
/// The checkpoint is one of the game's own saves, read back from the file the game wrote (the save is what a continued
/// run loads, so playing on and coming back meet the same checkpoint): on arrival at a map point, as the simulator takes
/// it; after a won fight and after an ancient, inside the room the game marked finished, which a load enters as finished;
/// and the save a continued run loads, whichever it was. The game saves nowhere else, so the record restarts at each of
/// these, from the game's own event (<see cref="Saved"/>). A fight inside an event is the exception, until a clone of
/// its save is checked: see <see cref="Enter"/>.
///
/// A step is taken where it takes effect, not where it is asked for: a card, a potion, a potion thrown away and the
/// turn's end once their action passed its own checks and acts, so an action the game cancels in its queue (at the
/// turn's end, at the combat's end, while a card is chosen) or skips (its card gone, its target dead) leaves no step;
/// every other decision where the game commits it, the entry points the simulator calls itself.
///
/// The player may do what the simulator never does, and the record follows: leaving a room is a step only while the
/// map is the decision (it is dropped when the player goes back into the room, which leaving does not change), a card
/// reward closed unanswered takes back the step that opened it, a potion thrown away in combat is a step of its own
/// ("combat discard_potion:S", which a clone replays but the brain is never offered), and taking back a turn's end is
/// no step (alone in the game, it cannot stop the turn's end). Anything else it cannot follow breaks the record until
/// the game's next save. Each step says whether the AI took it (<see cref="Expect"/>), so auto-play can stop when the
/// player acts (<see cref="PlayerActs"/>).
///
/// The record is kept in both environments, so switching to SL works in the room the player is in; only SL sends it.
/// Everything here runs on the game's main thread, and logs to <see cref="ModLog"/>.
/// </summary>
internal static class NodeRecord
{
    private static readonly AccessTools.FieldRef<VoteForMapCoordAction, MapVote?> Destination = AccessTools.FieldRefAccess<VoteForMapCoordAction, MapVote?>("_destination");
    private static readonly AccessTools.FieldRef<DiscardPotionGameAction, uint> DiscardedSlot = AccessTools.FieldRefAccess<DiscardPotionGameAction, uint>("_potionSlotIndex");

    private static readonly AccessTools.FieldRef<EventSynchronizer, List<Task>> EventOptionTasks = AccessTools.FieldRefAccess<EventSynchronizer, List<Task>>("_pendingOptionTasks");

    /// <param name="Ai">Whether the AI took it (<see cref="Expect"/>).</param>
    /// <param name="InSave">Taken while the game was writing a save, after the run it saves: it follows that save
    /// (<see cref="Saved"/>).</param>
    private sealed record Step(string Text, bool Ai, bool InSave);

    private static readonly List<Step> _steps = [];

    /// <summary>The game's tasks for the steps the player took, by the surface they were taken on.</summary>
    private static readonly List<(string Surface, Task Task)> _running = [];
    private static RunState? _run;

    /// <summary>The decision last on screen, which the player's next answer is read against.</summary>
    private static Asking? _screen;

    /// <summary>The room's decision last on screen (any but the map's): what leaving it, or going back to it, is about.</summary>
    private static Asking? _room;

    /// <summary>Where the step that left the room is, while the map is the decision and the player may still go back.</summary>
    private static int? _leave;

    /// <summary>An act's last rewards, left for good (<see cref="ActDone"/>): on screen until the act changes, it is no
    /// room to leave or come back to.</summary>
    private static RewardsSet? _actEnd;

    /// <summary>The action of the step the AI just asked the game for, which is the AI's when it takes effect.</summary>
    private static string? _expected;

    /// <summary>The checkpoint's number in the run, from 0 at the first the mod took in the run; -1 before.</summary>
    public static int Node { get; private set; } = -1;

    public static string? Checkpoint { get; private set; }

    /// <summary>The steps since the checkpoint, as the simulator's trace names them.</summary>
    public static IReadOnlyList<string> Steps => [.. _steps.Select(s => s.Text)];

    /// <summary>The prompts asked since the checkpoint (card prompts, card rewards, bundles), as the simulator's selector
    /// counts them from a clone's load: the next prompt is this number, which seeds its ties' order.</summary>
    public static int Prompts => _steps.Count(s => s.Text.StartsWith(Surfaces.CardChoice + " ", StringComparison.Ordinal)
                                                  || s.Text.StartsWith(Surfaces.CardReward + " ", StringComparison.Ordinal)
                                                  || s.Text.StartsWith(Surfaces.Bundle + " ", StringComparison.Ordinal));

    /// <summary>Why the record cannot be cloned, or null.</summary>
    public static string? Broken { get; private set; }

    /// <summary>The steps of the fight being played since it began (<see cref="FightBegins"/>), as <see cref="Steps"/>
    /// names them, whatever checkpoints came since: what the brain's engine takes to stand at a card prompt; null when
    /// the record missed one of them, being off or broken.</summary>
    public static IReadOnlyList<string>? Fight => _fight;

    private static List<string>? _fight;

    /// <summary>A fight begins: its steps are recorded from here, if the record is on and whole. A run of more than one
    /// player has no record, and its fight's steps start at the first decision (<see cref="Rearm"/>): the engine stands
    /// a prompt in a turn start's setup (the fight's first turn's too) with one player starting the turn only.</summary>
    public static void FightBegins() => _fight = Checkpoint != null && Broken == null ? [] : null;

    /// <summary>In a run of more than one player, the combat was written out again at a decision of the local player's:
    /// its steps are recorded from here, until a teammate acts or the turn ends (the next turn's start is several
    /// players').</summary>
    public static void Rearm()
    {
        if (Coop && CombatManager.Instance.IsInProgress) _fight = [];
    }

    private static bool Coop => RunManager.Instance.DebugOnlyGetState() is { Players.Count: > 1 };

    /// <summary>The run the steps are taken in: the record's, or in a run of more than one player (which has none) the
    /// game's.</summary>
    private static RunState? Run => Coop ? RunManager.Instance.DebugOnlyGetState() : _run;

    /// <summary>A teammate acted in the fight: the fight's steps since the combat was last written out are not all in the
    /// record, so no card prompt stands on them.</summary>
    private static void Teammate()
    {
        if (Coop) _fight = null;
    }

    /// <summary>Each step as it is taken, the record on or off: its surface, its action and whether the AI took it (the
    /// console's review reads the player's answers off it).</summary>
    public static event Action<string, string, bool>? Taken;

    /// <summary>How many times the player acted: took a step the AI did not, or went back. Auto-play stops on a change.</summary>
    public static int PlayerActs { get; private set; }

    public static void Patch(Harmony harmony)
    {
        SaveManager.Instance.Saved += Saved;
        Prefix(harmony, typeof(RunManager), nameof(RunManager.LoadIntoLatestMapCoord), nameof(Continuing));
        Prefix(harmony, typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue), nameof(Requested));
        Prefix(harmony, typeof(GameAction), nameof(GameAction.Execute), nameof(Executing));
        Prefix(harmony, typeof(GameAction), nameof(GameAction.Cancel), nameof(Cancelled));
        CombatManager.Instance.History.Changed += Played;
        Prefix(harmony, typeof(PotionModel), nameof(PotionModel.OnUseWrapper), nameof(Used));
        Prefix(harmony, typeof(PotionCmd), nameof(PotionCmd.Discard), nameof(Discarded));
        Prefix(harmony, typeof(PlayerCmd), nameof(PlayerCmd.EndTurn), nameof(TurnEnded));
        Prefix(harmony, typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice), nameof(Chosen));
        Prefix(harmony, typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption), nameof(EventOption));
        Prefix(harmony, typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption), nameof(RestOption));
        Prefix(harmony, typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward), nameof(RewardTaken));
        Prefix(harmony, typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SkipLocalRewardsSet), nameof(RewardsDone));
        Prefix(harmony, typeof(RunManager), nameof(RunManager.ProceedFromTerminalRewardsScreen), nameof(RewardsDone));
        Prefix(harmony, typeof(ActChangeSynchronizer), nameof(ActChangeSynchronizer.SetLocalPlayerReady), nameof(ActDone));
        Prefix(harmony, typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.PickRelicLocally), nameof(RelicPicked));
        Prefix(harmony, typeof(MerchantSynchronizer), nameof(MerchantSynchronizer.PurchaseLocalEntry), nameof(Bought));
        Prefix(harmony, typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked), nameof(Divined));
        Postfix(harmony, typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption), nameof(EventRunning));
        Postfix(harmony, typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption), nameof(RestRunning));
        Postfix(harmony, typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward), nameof(RewardRunning));
        Postfix(harmony, typeof(MerchantSynchronizer), nameof(MerchantSynchronizer.PurchaseLocalEntry), nameof(PurchaseRunning));
        Postfix(harmony, typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked), nameof(DivinationRunning));
    }

    private static void Prefix(Harmony harmony, Type type, string method, string prefix) =>
        harmony.Patch(AccessTools.DeclaredMethod(type, method), prefix: new HarmonyMethod(typeof(NodeRecord), prefix));

    private static void Postfix(Harmony harmony, Type type, string method, string postfix) =>
        harmony.Patch(AccessTools.DeclaredMethod(type, method), postfix: new HarmonyMethod(typeof(NodeRecord), postfix));

    /// <summary>
    /// Whether a step the player took on <paramref name="surface"/> still plays out (an event option, a rest option, a
    /// reward, a purchase, a divination): until it ends, the screen still shows the decision it answered. The simulator
    /// waits for the same tasks before its next decision.
    /// </summary>
    public static bool Running(string surface)
    {
        _running.RemoveAll(r => r.Task.IsCompleted);
        return _running.Any(r => r.Surface == surface);
    }

    private static void EventRunning(EventSynchronizer __instance)
    {
        foreach (var task in EventOptionTasks(__instance)) _running.Add((Surfaces.Event, task));
    }

    private static void RestRunning(Task __result) => _running.Add((Surfaces.Rest, __result));

    private static void RewardRunning(Task __result) => _running.Add((Surfaces.Rewards, __result));

    private static void PurchaseRunning(Task __result) => _running.Add((Surfaces.Shop, __result));

    private static void DivinationRunning(Task __result) => _running.Add((Surfaces.CrystalSphere, __result));

    /// <summary>
    /// The AI is asking the game for <paramref name="action"/> (a decision's action, as the record names it), or asked for
    /// nothing after all (null): when a step with that action takes effect, it is the AI's. Every other step is the player's.
    /// </summary>
    public static void Expect(string? action) => _expected = action;

    /// <summary>
    /// The decision on screen, each frame one is read. Leaving an event, a shop, a rest site or a fight's rewards is a
    /// decision of its own in the simulator, where the game only opens the map: it is a step once the map is the
    /// decision, and taken back when the room's decision comes back.
    /// </summary>
    public static void Seen(Asking asking)
    {
        if (_actEnd != null && ReferenceEquals(asking.Source, _actEnd)) return;
        if (asking.Surface == Surfaces.Map)
        {
            if (_screen?.Surface != Surfaces.Map) Leave();
        }
        else
        {
            if (_leave is int at) Back(at);
            _room = asking;
        }
        _screen = asking;
    }

    /// <summary>The step that leaves the room on screen for the map, if the simulator has one; while the map is the
    /// decision, it is the last step, and belt potions taken on the map go before it.</summary>
    private static void Leave()
    {
        if (_leave != null || _room is not { } room) return;
        MacroAction? leave = room.Surface switch
        {
            Surfaces.Event => MacroAction.Proceed,
            Surfaces.Shop or Surfaces.Rest => MacroAction.Leave,
            Surfaces.Rewards when room.Source is RewardsSet set && RewardActions.IsTerminal(set) => MacroAction.RewardsDone,
            _ => null,
        };
        if (leave is not { } action || !room.Legal.Contains(action.ToString())) return;
        if (Add(room.Surface, action.ToString())) _leave = _steps.Count - 1;
    }

    /// <summary>The player closed the map and is back in the room: leaving it changed nothing, so its step goes.</summary>
    private static void Back(int at)
    {
        _leave = null;
        if (Checkpoint == null || Broken != null) return;
        var left = _steps[at];
        _steps.RemoveAt(at);
        PlayerActs++;
        ModLog.Write("record", $"back in the room from the map: step {at} \"{left.Text}\" taken back");
    }

    // ---- checkpoints ----

    /// <summary>
    /// The game saved the run, on arrival at a map point (no finished room), after a won fight or an ancient (that
    /// room): the save it wrote, read back as a continued run reads it, is the checkpoint. Read off the game's own event,
    /// with no patch to lose: a Harmony prefix on <c>SaveManager.SaveRun</c> stopped being called after its 30th call in
    /// two real games (0.3.11, 0.3.13), with nothing in any log, and every room after it had no clone. The event comes
    /// once the save is written, and the game does not wait for an ancient's: a step taken in the meantime follows the
    /// save, and stays.
    /// </summary>
    private static void Saved() => Guard(() =>
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { Players.Count: 1 } run || LocalContext.GetMe(run) is null) return;
        var read = SaveManager.Instance.LoadRunSave();
        if (!read.Success || read.SaveData is not { } save) throw new InvalidDataException($"the game's save does not read back: {read.Status}");
        Enter(save.PreFinishedRoom != null ? run.CurrentRoom : null, continued: false, save);
    });

    /// <summary>A continued run enters the point its save was made at, without saving: the save it loaded is the checkpoint.</summary>
    private static void Continuing(AbstractRoom? preFinishedRoom) => Guard(() => Enter(preFinishedRoom, continued: true));

    /// <param name="save">The save the game wrote, read back; null for the run as it is now (a continued run's).</param>
    private static void Enter(AbstractRoom? room, bool continued, SerializableRun? save = null)
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { Players.Count: 1 } run || LocalContext.GetMe(run) is not { } me) return;
        // A fight inside an event: no clone of its save has been checked yet (run --check met none), so playing on keeps
        // the arrival's record, and a run continued from it has none.
        bool eventFight = room is CombatRoom { ParentEventId: not null };
        if (eventFight && !continued)
        {
            // The arrival's record goes on: the steps taken while this save was written are its own.
            for (int i = 0; i < _steps.Count; i++) _steps[i] = _steps[i] with { InSave = false };
            return;
        }
        bool sameRun = ReferenceEquals(run, _run);
        if (!sameRun) (_run, Node) = (run, -1);
        Node++;
        var following = sameRun && !continued ? _steps.Where(s => s.InSave).Select(s => s with { InSave = false }).ToList() : [];
        _steps.Clear();
        _steps.AddRange(following);
        (_screen, _room, _leave, _actEnd) = (null, null, null, null);
        // A step the AI asked for may still be on its way.
        if (!sameRun || continued) _expected = null;
        (Checkpoint, Broken) = eventFight
            ? ((string?)null, "the run was continued from a save after an event's fight, which no clone has been checked on")
            : (save != null ? GameBridge.Checkpoint.Of(save, me) : GameBridge.Checkpoint.Take(me, room), (string?)null);
        string kind = room switch
        {
            null => "arrival",
            CombatRoom { ParentEventId: not null } => "after an event's fight",
            CombatRoom => "after a won fight",
            _ => "after an ancient",
        };
        ModLog.Write("record", $"checkpoint {Node}: {kind}{(continued ? ", the save a continued run loaded" : "")}, floor {run.TotalFloor}, " +
                               $"{run.CurrentRoom?.GetType().Name ?? "no room yet"}, " + (Checkpoint != null ? $"{Checkpoint.Length} chars" : $"none: {Broken}") +
                               (following.Count > 0 ? $"; {following.Count} steps taken while it was written follow it" : ""));
    }

    // ---- actions through the queue: logged when asked for, taken when they act ----

    private static void Requested(GameAction action) => Guard(() =>
    {
        if (_run != null && ActionQueueSet.IsGameActionPlayerDriven(action)) ModLog.Write("action", $"asked for: {action}");
    });

    private static void Executing(GameAction __instance) => Guard(() =>
    {
        if (_run == null || __instance.State != GameActionState.WaitingForExecution || !ActionQueueSet.IsGameActionPlayerDriven(__instance)) return;
        ModLog.Write("action", $"runs: {__instance}");
        if (__instance is VoteForMapCoordAction vote) Travelled(vote);
    });

    private static void Cancelled(GameAction __instance) => Guard(() =>
    {
        if (_run != null && ActionQueueSet.IsGameActionPlayerDriven(__instance)) ModLog.Write("action", $"cancelled ({__instance.State}): {__instance}");
    });

    private static GameAction? RunningAction => RunManager.Instance.ActionExecutor.CurrentlyRunningAction;

    /// <summary>How many of the combat history's entries <see cref="Played"/> has read.</summary>
    private static int _historyRead;

    /// <summary>
    /// A card played from the hand: its action passed its checks and spent its cost, and the game wrote the play's start
    /// into the combat's history. Auto-played cards, and a card's further plays in its series, are no decision. Read off
    /// the history, an event of the game's: a Harmony prefix on <c>CardModel.OnPlayWrapper</c> stopped firing in the middle
    /// of a real game (0.3.11, 2026-11-15, after 60 plays) with nothing in any log, and every later play was missing.
    /// </summary>
    private static void Played() => Guard(() =>
    {
        // The history only grows during a combat (a list behind its enumerable), and a new combat clears it.
        var entries = (IReadOnlyList<CombatHistoryEntry>)CombatManager.Instance.History.Entries;
        if (entries.Count < _historyRead) _historyRead = 0;
        for (int i = _historyRead; i < entries.Count; i++)
            if (CombatActions.PlayStarted(entries[i], RunningAction) is { } play)
            {
                if (Mine(RunningAction!)) Add(Surfaces.Combat, play.ToString());
                else Teammate();
            }
        _historyRead = entries.Count;
    });

    /// <summary>A potion drunk: its action found it and its target.</summary>
    private static void Used() => Guard(() =>
    {
        if (RunningAction is UsePotionAction { WasEnqueuedInCombat: true } other && !Mine(other)) Teammate();
        if (RunningAction is not UsePotionAction use || !Mine(use) || Run is not { } run || LocalContext.GetMe(run) is not { } me) return;
        if (use.WasEnqueuedInCombat) Add(Surfaces.Combat, CombatActions.PotionOf(me, (int)use.PotionIndex, use.TargetId).ToString());
        else Belt(MacroAction.Of(MacroAction.Potion, (int)use.PotionIndex));
    });

    /// <summary>A potion thrown away by the player (events throw some away too, outside any action).</summary>
    private static void Discarded() => Guard(() =>
    {
        if (RunningAction is DiscardPotionGameAction { WasEnqueuedInCombat: true } other && !Mine(other)) Teammate();
        if (RunningAction is not DiscardPotionGameAction discard || !Mine(discard)) return;
        var action = MacroAction.Of(MacroAction.DiscardPotion, (int)DiscardedSlot(discard));
        // In combat the simulator never decides one; the clone replays it all the same (RunSession).
        if (discard.WasEnqueuedInCombat) Add(Surfaces.Combat, action.ToString());
        else Belt(action);
    });

    /// <summary>The player's turn end, once: cards and effects that end the turn are no decision.</summary>
    private static void TurnEnded(Player player) => Guard(() =>
    {
        if (RunningAction is EndPlayerTurnAction && !Mine(player.NetId)) Teammate();
        if (RunningAction is EndPlayerTurnAction && Mine(player.NetId) && !CombatManager.Instance.IsPlayerReadyToEndTurn(player)) Add(Surfaces.Combat, CombatAction.EndTurn.ToString());
    });

    /// <summary>The travel, the node's last step: leaving the room is final now.</summary>
    private static void Travelled(VoteForMapCoordAction vote)
    {
        if (Destination(vote) is not { } to || !Mine(vote) || _run is not { } run) return;
        if (_screen?.Surface != Surfaces.Map) Leave();
        _leave = null;
        var travel = MapActions.Of(to.coord);
        if (MapActions.Legal(run).Contains(travel)) Add(Surfaces.Map, travel.ToString());
        else Break($"a travel to {travel}, which the map does not offer");
    }

    /// <summary>The potion belt outside combat, offered on the screen it is used on. While the map is the decision and the
    /// room may still be gone back to, a belt step goes before the leaving step, on the room's surface: the belt is the
    /// same on every screen.</summary>
    private static void Belt(MacroAction action)
    {
        string text = action.ToString();
        if (_leave is int at && _room is { } room && room.Legal.Contains(text))
        {
            if (Insert(at, room.Surface, text)) _leave = at + 1;
        }
        else if (_screen is { } screen && screen.Surface != Surfaces.Combat && screen.Legal.Contains(text)) Add(screen.Surface, text);
        else Break($"{action} on a screen that does not offer it");
    }

    // ---- decisions the game commits at once ----

    /// <summary>Card prompts, card rewards and bundles answer through the choice synchronizer.</summary>
    private static void Chosen(PlayerChoiceResult result) => Guard(() =>
    {
        // A run of more than one player has no record, but its fight's steps (Fight) take the card prompts' answers.
        if ((Checkpoint == null || Broken != null) && !(Coop && _fight != null)) return;
        switch (_screen)
        {
            case { Surface: Surfaces.CardChoice, Source: IReadOnlyList<CardModel> options }:
            {
                // The game's own indexes into the prompt's options; a skipped single pick is -1.
                var picks = result.ChoiceType == PlayerChoiceType.Index
                    ? result.AsIndexes().Where(i => i >= 0).ToList()
                    : [.. result.AsCards(result.ChoiceType).Select(c => options.ToList().IndexOf(c))];
                if (picks.Any(i => i < 0 || i >= options.Count)) Break("a card picked outside the prompt's options");
                else Add(Surfaces.CardChoice, string.Join(",", picks));
                break;
            }
            case { Surface: Surfaces.CardReward } screen:
                // The screen's cards, then its alternatives (skip, reroll), as both the game and the legal actions list them.
                if (result.AsIndexOrNull() is int pick && pick >= 0 && pick < screen.Legal.Count) Add(Surfaces.CardReward, screen.Legal[pick]);
                else Unopened();
                break;
            case { Surface: Surfaces.Bundle }:
                Add(Surfaces.Bundle, MacroAction.Of(MacroAction.Bundle, result.AsIndex()).ToString());
                break;
            default:
                Break($"a {result.ChoiceType} choice on {_screen?.Surface ?? "no screen"}");
                break;
        }
    });

    /// <summary>
    /// A card reward closed unanswered: the reward stays on the rewards screen as it was (its cards were made with it),
    /// so the step that opened it is taken back.
    /// </summary>
    private static void Unopened()
    {
        if (_steps.Count > 0 && _steps[^1].Text.StartsWith($"{Surfaces.Rewards} {MacroAction.Reward}:", StringComparison.Ordinal))
        {
            var opened = _steps[^1];
            _steps.RemoveAt(_steps.Count - 1);
            PlayerActs++;
            ModLog.Write("record", $"a card reward closed unanswered: step {_steps.Count} \"{opened.Text}\" taken back");
        }
        else Break("a card reward closed unanswered, not opened by the last step");
    }

    private static void EventOption(int index) => Add(Surfaces.Event, MacroAction.Of(MacroAction.Event, index).ToString());

    private static void RestOption(RestSiteSynchronizer __instance, int index) =>
        Guard(() => Add(Surfaces.Rest, RestActions.Of(__instance.GetLocalOptions()[index]).ToString()));

    /// <summary>A potion reward clicked with a full belt is refused and stays on the screen: a reward not offered changes
    /// nothing.</summary>
    private static void RewardTaken(Reward reward) => Guard(() =>
    {
        if (_screen is not { Surface: Surfaces.Rewards, Source: RewardsSet set } || set.Rewards.IndexOf(reward) is not (var i and >= 0))
        {
            Break("a reward taken from no rewards screen");
            return;
        }
        var action = MacroAction.Of(MacroAction.Reward, i);
        if (RewardActions.Legal(set).Contains(action)) Add(Surfaces.Rewards, action.ToString());
    });

    /// <summary>The rewards screen's proceed button; the same calls leave a chest or a fight without rewards, which are no
    /// decisions. A fight's rewards end its room: the map opens, and the player may still go back.</summary>
    private static void RewardsDone()
    {
        if (_screen is not { Surface: Surfaces.Rewards } screen) return;
        // Some screens call two of these.
        _screen = null;
        if (screen.Source is RewardsSet set && RewardActions.IsTerminal(set)) Leave();
        else Add(Surfaces.Rewards, MacroAction.RewardsDone.ToString());
    }

    /// <summary>
    /// The proceed button of an act's last rewards: the game goes on to the next act, or to the Architect after the last
    /// one, and disables the button for good (<c>NRewardsScreen</c>). There is no going back, so the step is final at
    /// once, and the room is over: the rewards screen stays up until the act changes, and neither seeing it again nor
    /// the next act's map, which opens without a travel, leaves it again.
    /// </summary>
    private static void ActDone() => Guard(() =>
    {
        if (_screen is not { Surface: Surfaces.Rewards, Source: RewardsSet set }) return;
        (_screen, _room, _actEnd) = (null, null, set);
        Add(Surfaces.Rewards, MacroAction.RewardsDone.ToString());
    });

    private static void RelicPicked(int? index) =>
        Add(Surfaces.Treasure, (index is int i ? MacroAction.Of(MacroAction.Relic, i) : MacroAction.RelicSkip).ToString());

    /// <summary>A purchase the shop cannot make (too little gold, sold out) changes nothing. Only the player's own purchase
    /// comes this way: a relic buying for free goes past it.</summary>
    private static void Bought(MerchantEntry entry) => Guard(() =>
    {
        if (_run?.CurrentRoom is not MerchantRoom) return;
        var inventory = RunManager.Instance.MerchantSynchronizer.GetLocalInventory();
        var action = ShopActions.Of(inventory, entry);
        if (ShopActions.Legal(inventory).Contains(action)) Add(Surfaces.Shop, action.ToString());
    });

    /// <summary>A click that uncovers nothing changes nothing.</summary>
    private static void Divined(CrystalSphereMinigame __instance, CrystalSphereCell clickedCell) => Guard(() =>
    {
        var action = CrystalSphereActions.Of(__instance, clickedCell);
        if (CrystalSphereActions.Legal(__instance).Contains(action)) Add(Surfaces.CrystalSphere, action.ToString());
    });

    // ---- the steps ----

    private static bool Add(string surface, string action) => Insert(_steps.Count, surface, action);

    /// <summary>A step at <paramref name="at"/>, the AI's if it is the one the AI asked for; false when the record is off
    /// (the player's acts count all the same).</summary>
    private static bool Insert(int at, string surface, string action)
    {
        bool ai = action == _expected;
        Taken?.Invoke(surface, action, ai);
        if (ai) _expected = null;
        else PlayerActs++;
        if (Checkpoint == null || Broken != null)
        {
            if (Coop && _fight != null && surface is Surfaces.Combat or Surfaces.CardChoice && action != CombatAction.EndTurn.ToString())
                _fight.Add($"{surface} {action}");
            else _fight = null;
            return false;
        }
        _steps.Insert(at, new($"{surface} {action}", ai, SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false }));
        if (_fight != null && surface is Surfaces.Combat or Surfaces.CardChoice)
        {
            if (at == _steps.Count - 1) _fight.Add($"{surface} {action}");
            else _fight = null;
        }
        ModLog.Write("record", $"step {at} of checkpoint {Node}: {surface} {action} ({(ai ? "AI" : "player")})");
        return true;
    }

    /// <summary>Whether a player's action is the local player's: in a run of several players the others' steps are no
    /// answer of this player's.</summary>
    private static bool Mine(GameAction action) => Mine(action.OwnerId);

    private static bool Mine(ulong owner) => LocalContext.NetId is not { } me || owner == me;

    /// <summary>A hook runs inside the game's own call: whatever goes wrong in it must not reach the game.</summary>
    private static void Guard(Action hook)
    {
        try
        {
            hook();
        }
        catch (Exception e)
        {
            ModLog.Error("record", $"SL record: {e}");
            Break($"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The node's clone failed in the brain: the record is off until the next checkpoint, with the reason.</summary>
    public static void CloneFailed(string reason) => Break(reason);

    private static void Break(string reason)
    {
        if (Coop) _fight = null;
        if (Checkpoint == null || Broken != null) return;
        Broken = reason;
        _fight = null;
        ModLog.Warn("record", $"SL record of checkpoint {Node} broken after {_steps.Count} steps: {reason}");
    }
}
