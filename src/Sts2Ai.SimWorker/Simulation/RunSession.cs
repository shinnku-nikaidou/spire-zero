using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>How a run starts: a new singleplayer run of <see cref="Character"/>; a loadout replaces the starting one.</summary>
/// <param name="AutoWin">The run's rule (<see cref="RunView.AutoWin"/>): the acts, indexes from 0, whose plain monster
/// fights, an event's included, are won at their start as the game's <c>win</c> console command wins them, their rewards
/// following as after any win; elites and bosses are fought. Null in the normal game.</param>
internal sealed record RunSetup(string Character, string Seed, int Ascension = 0, Loadout? Loadout = null, IReadOnlyList<int>? AutoWin = null);

/// <summary>
/// One whole run in the real engine, started the way the game starts a new singleplayer run.
/// <see cref="Step"/> pumps the game until it waits for the player, then makes one decision: combat through the
/// combat policy, a card prompt the game waits on (<see cref="Prompt"/>) through the choice policy, every other screen
/// through the macro policy. Each decision can be observed (<see cref="Observation"/>): policies build it on request,
/// and <c>observed</c> sees all of them.
///
/// The game runs each room by itself; this class plays the part of the UI: which screen is showing and what its
/// buttons do. What a screen would remember (a rewards screen still open, the player having left the room) is kept
/// per room. Same setup and same policy answers, same run. Like <see cref="CombatSession"/>, one live at a time.
///
/// On arrival at each map point the game saves; that save is the node's checkpoint, and with the decisions since it
/// makes the node's SL clone (<see cref="Clone"/>, <see cref="Load"/>).
/// </summary>
internal sealed class RunSession : IDisposable
{
    /// <summary>Things the driver does without a decision (offer rewards, open a chest) between two decisions.</summary>
    private const int MaxAutoSteps = 20;

    private readonly Pump _pump;
    private readonly IChoicePolicy _choices;
    private readonly ICombatPolicy _combat;
    private readonly IMacroPolicy _macro;
    private readonly Action<Observation>? _observed;
    private readonly PromptSelector _selector;
    private IDisposable? _selectorScope;
    private readonly List<string> _trace = [];
    private readonly List<ulong> _mapHashes = [];
    private readonly List<CombatEntry> _combats = [];

    /// <summary>Rewards screens open, newest last. The game hands each set over in <see cref="RewardsSet.testSelector"/>.</summary>
    private readonly List<RewardsSet> _shown = [];

    /// <summary>Game tasks the driver started (a purchase, a rest option, opening a chest), and what to do once each ends.</summary>
    private readonly List<(Task Task, Action? Done, string What)> _running = [];

    /// <summary>The crystal sphere screen, open over its event until the last divination.</summary>
    private CrystalSphereMinigame? _minigame;

    /// <summary>The state of the last combat entered, kept for its enemies' HP once it is over.</summary>
    private CombatState? _combatState;

    private AbstractRoom? _room;
    private RoomProgress _progress = new();
    private int? _endedTurn;
    private IReadOnlyList<CardRewardAlternative>? _alternatives;
    private readonly HashSet<CardRewardAlternative> _spentAlternatives = [];

    /// <summary>The current node's checkpoint: the game's save on arrival, as JSON.</summary>
    private string? _checkpoint;

    /// <summary>A loaded clone stops at its node's exit.</summary>
    private bool _sealed;

    /// <summary>A loaded clone's recorded decisions, which come before the policies' own.</summary>
    private Replay? _replay;

    public RunSetup Setup { get; }
    public Player Player { get; }
    public RunState Run { get; }

    /// <summary>Every decision so far, as "surface action", card prompts among them.</summary>
    public IReadOnlyList<string> Trace => _trace;

    /// <summary>A hash of each act's map, taken when the act is entered; the same in every process if generation is.</summary>
    public IReadOnlyList<ulong> MapHashes => _mapHashes;

    /// <summary>Every combat entered so far, with the HP at its first decision and, once it is over, its end.</summary>
    public IReadOnlyList<CombatEntry> Combats => _combats;

    /// <summary>Card rewards of the rewards screens closed so far, and how many of them were left untaken: skipped on
    /// their screen, or never opened.</summary>
    public int CardRewards { get; private set; }

    public int CardRewardsSkipped { get; private set; }

    /// <summary>Where the current node's decisions begin in <see cref="Trace"/>.</summary>
    public int NodeStart { get; private set; }

    /// <summary>A sealed clone has left its node: it travelled. It cannot step any more.</summary>
    public bool AtHorizon { get; private set; }

    public bool IsOver => Run.IsGameOver;

    /// <summary>Winning ends in the victory room, where the game kills the players once the run is recorded.</summary>
    public bool Won => IsOver && Run.CurrentRoom?.IsVictoryRoom == true;

    private RunSession(Pump pump, RunSetup setup, Player player, RunState run, IChoicePolicy choices, ICombatPolicy combat, IMacroPolicy macro,
        Action<Observation>? observed)
    {
        _pump = pump;
        Setup = setup;
        Player = player;
        Run = run;
        _choices = choices;
        _combat = combat;
        _macro = macro;
        _observed = observed;
        _selector = new PromptSelector(pump, (choice, picks) => Record(Surfaces.CardChoice, string.Join(",", picks), () => PromptScreen(choice)),
            CardReward, Bundle);
        RewardsSet.testSelector = set =>
        {
            set.ThrowInTestIfRewardsNotTaken = false;
            _shown.Add(set);
            return Task.CompletedTask;
        };
        HeadlessPatches.CrystalSphereShown = minigame => _minigame = minigame;
        // A clone's prompts are numbered from its node's entry, so the node's own are too.
        HeadlessPatches.NodeEntered = () =>
        {
            (_checkpoint, NodeStart) = (Checkpoint.Take(player), _trace.Count);
            _selector.RestartPrompts();
        };
    }

    /// <summary>
    /// Starts the run and pumps it to the first decision (normally Neow). <paramref name="observed"/>, if given, gets
    /// every decision's observation, taken before its action is applied.
    /// </summary>
    public static RunSession Start(Pump pump, RunSetup setup, IChoicePolicy choices, ICombatPolicy combat, IMacroPolicy macro,
        Action<Observation>? observed = null)
    {
        var character = ModelDb.AllCharacters.First(c => c.Id.Entry.Equals(setup.Character, StringComparison.OrdinalIgnoreCase));
        var player = Player.CreateForNewRun(character, UnlockState.all, LocalContext.NetId!.Value);
        setup.Loadout?.ApplyTo(player, setup.Ascension);

        // StartRunLobby.BeginRunLocally: the acts are rolled from the seed.
        var actRng = new Rng(StringHelper.GetDeterministicHashCode(setup.Seed), "act_selection");
        var acts = ActModel.GetRandomList(actRng, UnlockState.all, isMultiplayer: false).Select(a => a.ToMutable()).ToList();
        var run = RunState.CreateForNewRun([player], acts, [], GameMode.Standard, isCustom: false, setup.Ascension, setup.Seed);
        RunManager.Instance.SetUpNewSingleplayer(run, shouldSave: false);

        var session = new RunSession(pump, setup, player, run, choices, combat, macro, observed);
        try
        {
            session.UseSelector();
            // NGame.StartRun without the nodes. A loadout's relics are its final state, pickups done (their potion
            // slots included), so their AfterObtained does not run again.
            if (setup.Loadout?.Relics == null) session.Await(RunManager.Instance.FinalizeStartingRelics());
            RunManager.Instance.Launch();
            session.Await(RunManager.Instance.EnterAct(0, doTransition: false));
            session.Settle();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads <paramref name="clone"/>: its save as the game's Continue loads one, with the relic fields
    /// the save drops put back (<see cref="Checkpoint"/>), then its decisions replayed, before the policies answer. The
    /// clone is sealed: once it travels it is at its horizon, and stepping further throws. A clone whose last decision
    /// raised a card prompt stands at that prompt, and so does one whose room asks one as it is entered.
    /// </summary>
    /// <param name="opened">The clone's checkpoint already read (<see cref="Checkpoint.Open"/>), for a node loaded many times.</param>
    public static RunSession Load(Pump pump, NodeClone clone, IChoicePolicy choices, ICombatPolicy combat, IMacroPolicy macro,
        Action<Observation>? observed = null, Checkpoint.Opened? opened = null)
    {
        opened ??= Checkpoint.Open(clone.Save);
        var read = SaveManager.FromJson<SerializableRun>(opened.Save);
        var save = read.SaveData ?? throw new InvalidDataException($"node checkpoint does not load: {read.Status} {read.ErrorMessage}");
        var run = RunState.FromSerializable(save);
        var player = run.Players[0];
        var replay = new Replay(clone.Trace, combat, macro);
        var setup = new RunSetup(player.Character.Id.Entry, run.Rng.StringSeed, run.AscensionLevel, AutoWin: clone.AutoWin);
        var session = new RunSession(pump, setup, player, run, choices, replay, replay, observed) { _sealed = true, _checkpoint = clone.Save, _replay = replay };
        try
        {
            // NMainMenu's Continue and NGame.LoadRun without the nodes.
            session.Await(RunManager.Instance.SetUpSavedSingleplayer(run, save));
            session.UseSelector();
            Checkpoint.RestoreRelics(opened, player);
            // Continue always saves; a clone saves nothing, as a run this simulator starts.
            AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave)).Invoke(RunManager.Instance, [false]);
            RunManager.Instance.Launch();
            session.Await(RunManager.Instance.GenerateMap());
            session.Enter(RunManager.Instance.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(save.PreFinishedRoom, run)));
            while (replay.Pending > 0 && !session.IsOver) session.Step();
            if (!session.AtHorizon) session.Settle();
            if (replay.Pending > 0) throw new InvalidOperationException($"replay diverged: {replay.Pending} decisions never asked for");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>The run's card prompts go to this session: the game keeps its selectors with the run it has set up.</summary>
    private void UseSelector() => _selectorScope = RunManager.Instance.CardSelectorStack.PushSelector(Player, _selector);

    /// <summary>The current node's SL clone: its checkpoint and the decisions since, with the run's rule.</summary>
    public NodeClone Clone() => new(_checkpoint ?? throw new InvalidOperationException("no node entered yet"), [.. _trace.Skip(NodeStart)], Setup.AutoWin);

    /// <summary>The run as a decision shows it, with the run's rule (<see cref="RunView.AutoWin"/>).</summary>
    /// <param name="previews">As <see cref="Observe.Run"/>'s.</param>
    public RunView View(bool previews = true)
    {
        var view = Observe.Run(Run, Player, previews);
        return Setup.AutoWin == null ? view : view with { AutoWin = Setup.AutoWin };
    }

    private Observation Of(ScreenView screen) => new() { Run = View(), Screen = screen };

    /// <summary>The card prompt the game waits on, if any: the next decision, the choice policy's.</summary>
    public CardChoice? Prompt => _selector.Waiting;

    /// <summary>The combat as a decision shows it, with the prompt it waits on.</summary>
    public CombatObservation Combat()
    {
        var obs = Observe.Combat(CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat state"), Player);
        return Prompt is { } prompt ? obs with { Prompt = prompt } : obs;
    }

    private CardChoiceScreen PromptScreen(CardChoice choice) => new()
    {
        Choice = choice,
        Combat = CombatManager.Instance.IsInProgress ? Observe.Combat(CombatManager.Instance.DebugOnlyGetState()!, Player) : null,
    };

    /// <summary>Answers the waiting prompt: a loaded clone's recorded answer first, then the choice policy's.</summary>
    private void AnswerPrompt()
    {
        var prompt = Prompt ?? throw new InvalidOperationException("no card prompt waits for an answer");
        if (_replay?.Picks() is { } picks) _selector.Replay(picks);
        else _selector.Answer(_choices.Choose(new ChoiceDecision(prompt, () => Of(PromptScreen(prompt)))).Picks);
    }

    /// <summary>Makes one decision, after anything the game or the driver does on its own. Does nothing once the run is over.</summary>
    public void Step()
    {
        if (AtHorizon) throw new InvalidOperationException("sealed: the clone has left its node");
        for (int auto = 0; ; auto++)
        {
            Settle();
            if (IsOver) return;
            if (auto > MaxAutoSteps) throw new InvalidOperationException($"no decision after {MaxAutoSteps} automatic steps: {Describe()}");
            bool prompted = Prompt != null;
            if (prompted) AnswerPrompt();
            if (prompted || Decide())
            {
                // A death inside the action itself (a card in hand at the turn's end, a play) ends the run before any next Settle.
                if (IsOver) NoteCombatEnd();
                return;
            }
        }
    }

    /// <summary>Pumps until the player can act: a card prompt waits (<see cref="Prompt"/>); or in combat, their turn (a
    /// later one after ending a turn); or elsewhere, the game is idle. A fight past the round cap is lost there
    /// (<see cref="FightCap"/>).</summary>
    public void Settle()
    {
        void Pump()
        {
            _pump.RunUntil(() =>
            {
                if (Prompt != null) return true;
                if (CombatManager.Instance.IsInProgress)
                    return CombatActions.WaitingForInput(Player) && (_endedTurn == null || Player.PlayerCombatState!.TurnNumber > _endedTurn);
                return _pump.Pending == 0 && !RunManager.Instance.ActionExecutor.IsRunning;
            }, Describe);
            _pump.Drain();
        }
        Pump();
        if (Prompt == null && FightCap.Enforce(_pump, Player, Describe)) Pump();
        // A prompt on the way to the next turn does not end the wait for it.
        if (Prompt == null) _endedTurn = null;
        NoteCombatEnd();
        if (_mapHashes.Count == Run.CurrentActIndex && Run.Map is not NullActMap) _mapHashes.Add(MapHash(Run.Map));
        Collect();
    }

    /// <summary>Writes the end of the run's last combat, once it has one. A combat the run died in before its first decision
    /// (a relic's damage as the first turn starts) gets its entry here, at no HP.</summary>
    private void NoteCombatEnd()
    {
        if (IsOver && !Won && Run.CurrentRoom is CombatRoom room && room != _room) EnterCombat(room);
        if (_combats.Count == 0 || _combats[^1].End != null || (CombatManager.Instance.IsInProgress && !IsOver)) return;
        _combats[^1] = _combats[^1] with { End = new(IsOver && !Won, Player.Creature.CurrentHp, Player.Creature.MaxHp, Potions()) };
    }

    private void EnterCombat(CombatRoom room)
    {
        _combatState = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat state");
        _combats.Add(new(Run.CurrentActIndex, Run.TotalFloor, room.Encounter.Id.Entry, room.Encounter.RoomType.ToString(),
            Player.Creature.CurrentHp, Player.Creature.MaxHp, Potions()));
    }

    private int Potions() => Player.PotionSlots.Count(p => p != null);

    /// <summary>Ends the driver's tasks that are done, failing on any that faulted.</summary>
    private void Collect()
    {
        for (int i = 0; i < _running.Count;)
        {
            var (task, done, what) = _running[i];
            if (!task.IsCompleted) { i++; continue; }
            _running.RemoveAt(i);
            if (!task.IsCompletedSuccessfully)
                throw new InvalidOperationException($"{what} failed: {task.Exception?.GetBaseException().Message ?? "cancelled"}", task.Exception?.GetBaseException());
            done?.Invoke();
        }
    }

    /// <summary>Makes a decision (true), or does what the UI would do without one (false). Throws when no screen fits.</summary>
    private bool Decide()
    {
        var room = Run.CurrentRoom ?? throw new InvalidOperationException("no current room");
        if (room != _room)
        {
            _room = room;
            _progress = new RoomProgress();
        }

        // A non-terminal rewards screen closes once everything on it is taken.
        foreach (var completed in _shown.Where(s => !RewardActions.IsTerminal(s) && RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(s)).ToList())
            Close(completed);
        if (_shown.Count > 0) return RewardsDecision(_shown[^1]);
        // Rewards are the only screen a started task can wait on.
        if (_running.Count > 0)
            throw new InvalidOperationException($"waiting for {string.Join(", ", _running.Select(r => r.What))} with no screen to answer: {Describe()}");

        if (room is MapRoom || _progress.Left) return MapDecision();
        return room switch
        {
            CombatRoom combat => CombatStep(combat),
            EventRoom => EventDecision(),
            MerchantRoom merchant => ShopDecision(merchant),
            RestSiteRoom => RestDecision(),
            TreasureRoom treasure => TreasureStep(treasure),
            _ => throw new InvalidOperationException($"no screen for {room.GetType().Name}: {Describe()}"),
        };
    }

    private bool RewardsDecision(RewardsSet set)
    {
        if (Ask(Surfaces.Rewards, RewardActions.Legal(set), () => RewardActions.Screen(set)) is not { } action) return true;
        if (action == MacroAction.RewardsDone)
        {
            Close(set);
            Track(RewardActions.Proceed(Run, set), "rewards proceed");
            if (RewardActions.IsTerminal(set)) _progress.Left = true;
        }
        else Track(RewardActions.Start(set, action), action.ToString());
        return true;
    }

    private void Close(RewardsSet set)
    {
        _shown.Remove(set);
        foreach (var reward in set.Rewards.Where(r => r is MegaCrit.Sts2.Core.Rewards.CardReward))
        {
            CardRewards++;
            if (!reward.SuccessfullySelected) CardRewardsSkipped++;
        }
    }

    private bool MapDecision()
    {
        if (Ask(Surfaces.Map, MapActions.Legal(Run), () => MapActions.Screen(Run)) is not { } action) return true;
        // The travel is the node's last action: a sealed clone records it and stops there, without travelling.
        if (_sealed) AtHorizon = true;
        else MapActions.Start(Run, Player, action);
        return true;
    }

    /// <summary>A won combat shows its rewards (<c>NCombatUi.OnCombatWon</c>), or proceeds if the encounter gives none. A
    /// plain monster fight in an act of the run's rule (<see cref="RunSetup.AutoWin"/>) is won at its start.</summary>
    private bool CombatStep(CombatRoom room)
    {
        if (CombatManager.Instance.IsInProgress)
        {
            if (!_progress.CombatEntered)
            {
                _progress.CombatEntered = true;
                EnterCombat(room);
            }
            if (!RunView.WinsAtStart(Setup.AutoWin, Run.CurrentActIndex, room.Encounter.RoomType.ToString())) return CombatDecision();
            // An enemy that comes back or calls others when killed keeps the combat going: kill again, a few times.
            if (++_progress.AutoWins > 5) throw new InvalidOperationException($"auto-win: the combat goes on after 5 wins: {Describe()}");
            Await(Win());
            return false;
        }
        if (_progress.RewardsOffered) throw new InvalidOperationException($"combat over and no screen: {Describe()}");
        _progress.RewardsOffered = true;
        if (room.Encounter.ShouldGiveRewards) Track(room.OfferRoomEndRewards(), "offer combat rewards");
        else
        {
            Track(RunManager.Instance.ProceedFromTerminalRewardsScreenAsync(), "proceed without rewards");
            _progress.Left = true;
        }
        return false;
    }

    /// <summary>The game's <c>WinConsoleCmd</c>: each enemy loses its powers and is killed, then the game checks for the win.</summary>
    private static async Task Win()
    {
        foreach (var enemy in CombatManager.Instance.DebugOnlyGetState()!.Enemies.ToList())
        {
            enemy.RemoveAllPowersInternalExcept();
            await CreatureCmd.Kill(enemy);
        }
        await CombatManager.Instance.CheckWinCondition();
    }

    private bool CombatDecision()
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat state");
        var legal = CombatActions.Legal(state, Player);
        var decision = new CombatDecision(legal, () => Observe.Combat(state, Player), combat => Of(new CombatScreen { Combat = combat }));
        var action = _combat.Choose(decision);
        if (!legal.Contains(action)) throw new InvalidOperationException($"combat policy chose {action}, which is not legal");
        Record(Surfaces.Combat, action.ToString(), () => new CombatScreen { Combat = decision.Observation });
        if (action.Kind == CombatActionKind.EndTurn) _endedTurn = Player.PlayerCombatState!.TurnNumber;
        CombatActions.Start(state, Player, action);
        return true;
    }

    private bool EventDecision()
    {
        if (_minigame is { IsFinished: false } minigame) return CrystalSphereDecision(minigame);
        _minigame = null;
        var ev = RunManager.Instance.EventSynchronizer.GetLocalEvent();
        if (Ask(Surfaces.Event, EventActions.Legal(ev), () => EventActions.Screen(ev, Player)) is not { } action) return true;
        EventActions.Start(ev, action);
        if (action == MacroAction.Proceed) _progress.Left = true;
        return true;
    }

    private bool CrystalSphereDecision(CrystalSphereMinigame minigame)
    {
        if (Ask(Surfaces.CrystalSphere, CrystalSphereActions.Legal(minigame), () => CrystalSphereActions.Screen(minigame)) is { } action) Track(CrystalSphereActions.Start(minigame, action), action.ToString());
        return true;
    }

    private bool ShopDecision(MerchantRoom room)
    {
        var inventory = RunManager.Instance.MerchantSynchronizer.GetLocalInventory();
        if (Ask(Surfaces.Shop, ShopActions.Legal(inventory), () => ShopActions.Screen(inventory)) is not { } action) return true;
        Track(ShopActions.Start(inventory, action), action.ToString());
        if (action == MacroAction.Leave) _progress.Left = true;
        return true;
    }

    private bool RestDecision()
    {
        var options = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions();
        var progress = _progress;
        if (Ask(Surfaces.Rest, RestActions.Legal(options, progress.RestSucceeded), () => RestActions.Screen(options, Player)) is not { } action) return true;
        if (action == MacroAction.Leave)
        {
            progress.Left = true;
            return true;
        }
        var chosen = RestActions.Start(options, progress.RestSucceeded, action);
        Track(chosen, action.ToString(), () => progress.RestSucceeded |= chosen.Result);
        return true;
    }

    /// <summary>
    /// The chest opens by itself (opening it is not a choice), then the relic is picked or skipped, then the room is
    /// left as the proceed button does (<c>NTreasureRoom</c>).
    /// </summary>
    private bool TreasureStep(TreasureRoom room)
    {
        if (!_progress.ChestOpened)
        {
            _progress.ChestOpened = true;
            Track(OpenChest(room), "open chest");
            return false;
        }
        var relics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
        if (relics is { Count: > 0 } && !_progress.RelicPicked)
        {
            if (Ask(Surfaces.Treasure, TreasureActions.Legal(relics), () => TreasureActions.Screen(relics)) is not { } action) return true;
            TreasureActions.Start(relics, action);
            _progress.RelicPicked = true;
            return true;
        }
        Track(RunManager.Instance.ProceedFromTerminalRewardsScreenAsync(), "leave treasure");
        _progress.Left = true;
        return false;

        static async Task OpenChest(TreasureRoom room)
        {
            await room.DoNormalRewards();
            await room.DoExtraRewardsIfNeeded();
        }
    }

    /// <summary>
    /// Asks the macro policy. The potion belt is usable on every screen, so its actions are offered everywhere and
    /// handled here: then the result is null, and the screen asks again once the potion is done.
    /// </summary>
    private MacroAction? Ask(string surface, List<MacroAction> legal, Func<ScreenView> screen)
    {
        legal.AddRange(PotionActions.Legal(Player));
        var action = Choose(surface, legal, screen);
        if (!PotionActions.Handles(action)) return action;
        PotionActions.Start(Player, action);
        return null;
    }

    private MacroAction Choose(string surface, List<MacroAction> legal, Func<ScreenView> screen)
    {
        var decision = new MacroDecision(surface, legal, () => Of(screen()));
        var action = _macro.Choose(decision).Action;
        if (!legal.Contains(action)) throw new InvalidOperationException($"macro policy chose {action}, not legal on {surface}: {string.Join(" ", legal)}");
        _trace.Add($"{surface} {action}");
        _observed?.Invoke(decision.Observation);
        return action;
    }

    /// <summary>Adds a decision to the trace, and shows its observation to <see cref="_observed"/>.</summary>
    private void Record(string surface, string action, Func<ScreenView> screen)
    {
        _trace.Add($"{surface} {action}");
        _observed?.Invoke(Of(screen()));
    }

    /// <summary>
    /// The card reward screen, asked again after each pick while the screen stays open. A reroll leaves its button
    /// listed but used, so it is offered once per screen (the game keeps the same alternatives list for the screen).
    /// </summary>
    private CardRewardSelection CardReward(IReadOnlyList<CardCreationResult> cards, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        if (!ReferenceEquals(alternatives, _alternatives))
        {
            _alternatives = alternatives;
            _spentAlternatives.Clear();
        }
        var action = Choose(Surfaces.CardReward, RewardActions.CardRewardLegal(cards, alternatives, _spentAlternatives),
            () => RewardActions.CardRewardScreen(cards, alternatives));
        var answer = RewardActions.CardRewardAnswer(cards, alternatives, action);
        if (answer.alternative is { } used && RewardActions.KeepsScreenOpen(used)) _spentAlternatives.Add(used);
        return answer;
    }

    private int Bundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles) =>
        BundleActions.Index(bundles.Count, Choose(Surfaces.Bundle, BundleActions.Legal(bundles.Count), () => BundleActions.Screen(bundles)));

    private void Track(Task task, string what, Action? done = null) => _running.Add((task, done, what));

    /// <summary>
    /// Pumps a loaded clone's entry into its node's room to its end, or to a card prompt its trace has no answer for: a
    /// combat's first turn starts inside the entry and may ask one (a relic's, at the start of each combat). The clone
    /// then stands at that prompt, as the run did, and the entry goes on once it is answered.
    /// </summary>
    private void Enter(Task entry)
    {
        while (true)
        {
            _pump.RunUntil(() => Prompt != null || entry.IsCompleted, Describe);
            if (Prompt == null || _replay!.Pending == 0) break;
            AnswerPrompt();
        }
        Track(entry, "enter the node's room");
        Collect();
    }

    /// <summary>Pumps a task the driver must finish before going on (run setup, the auto-win), with the card prompts it
    /// raises answered as the run's decisions.</summary>
    private void Await(Task task)
    {
        while (true)
        {
            _pump.RunUntil(() => Prompt != null || task.IsCompleted, Describe);
            if (Prompt == null) break;
            AnswerPrompt();
        }
        if (!task.IsCompletedSuccessfully) throw task.Exception?.GetBaseException() ?? new OperationCanceledException();
    }

    /// <summary>The map's points (row, col, type) and edges in a fixed order, hashed.</summary>
    private static ulong MapHash(ActMap map)
    {
        var text = new StringBuilder();
        var points = map.GetAllMapPoints().Append(map.StartingMapPoint).Append(map.BossMapPoint);
        if (map.SecondBossMapPoint is { } second) points = points.Append(second);
        foreach (var p in points.Distinct().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col))
        {
            text.Append($"{p.coord.col},{p.coord.row}:{p.PointType}>");
            foreach (var c in p.Children.OrderBy(c => c.coord.row).ThenBy(c => c.coord.col)) text.Append($"{c.coord.col},{c.coord.row};");
            text.Append('|');
        }
        return CombatSession.StableHash(text.ToString());
    }

    /// <summary>The player's run state, for comparing two runs.</summary>
    public string Summary()
    {
        var deck = string.Join(",", Player.Deck.Cards.Select(c => c.Id.Entry + (c.CurrentUpgradeLevel > 0 ? "+" : "")));
        var relics = string.Join(",", Player.Relics.Select(r => r.Id.Entry));
        var potions = string.Join(",", Player.PotionSlots.Select(p => p?.Id.Entry ?? "-"));
        return $"act {Run.CurrentActIndex} floor {Run.TotalFloor} room {Run.CurrentRoom?.ModelId?.Entry ?? Run.CurrentRoom?.GetType().Name} " +
               $"hp {Player.Creature.CurrentHp}/{Player.Creature.MaxHp} gold {Player.Gold} " +
               $"deck [{deck}] relics [{relics}] potions [{potions}]";
    }

    public string Describe()
    {
        var executor = RunManager.Instance.ActionExecutor;
        var pcs = Player.PlayerCombatState;
        return $"room={Run.CurrentRoom?.GetType().Name}({Run.CurrentRoom?.ModelId?.Entry}) rooms={Run.CurrentRoomCount} act={Run.CurrentActIndex} " +
               $"floor={Run.TotalFloor} left={_progress.Left} shown={_shown.Count} running=[{string.Join(", ", _running.Select(r => r.What))}] " +
               $"combat={CombatManager.Instance.IsInProgress} phase={pcs?.Phase} turn={pcs?.TurnNumber} " +
               $"executorRunning={executor.IsRunning} paused={executor.IsPaused} action={executor.CurrentlyRunningAction} pending={_pump.Pending}";
    }

    public void Dispose()
    {
        RewardsSet.testSelector = null;
        HeadlessPatches.CrystalSphereShown = null;
        HeadlessPatches.NodeEntered = null;
        _selectorScope?.Dispose();
        // Nothing runs while a prompt waits: the game stands where it asked until its run is cleaned up.
        _pump.Drain();
        RunManager.Instance.CleanUp(graceful: true);
        _selector.Drop();
        _pump.Drain();
        LocalContext.NetId = 1UL;
    }

    /// <summary>
    /// Answers decisions from a recorded trace, in order, then passes combat and macro decisions on to the policies. A
    /// card prompt's recorded answer is the game's own indexes (<see cref="Picks"/>).
    /// </summary>
    private sealed class Replay(IEnumerable<string> trace, ICombatPolicy combat, IMacroPolicy macro) : ICombatPolicy, IMacroPolicy
    {
        private readonly Queue<string> _steps = new(trace);

        public int Pending => _steps.Count;

        public CombatAction Choose(CombatDecision decision) => _steps.Count > 0 ? CombatAction.Parse(Next(Surfaces.Combat)) : combat.Choose(decision);

        public MacroAnswer Choose(MacroDecision decision) => _steps.Count > 0 ? new(MacroAction.Parse(Next(decision.Surface))) : macro.Choose(decision);

        /// <summary>The recorded answer to the card prompt that waits; null once the trace is played.</summary>
        public int[]? Picks() => _steps.Count == 0 ? null
            : [.. Next(Surfaces.CardChoice).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];

        private string Next(string surface)
        {
            var step = _steps.Dequeue();
            int space = step.IndexOf(' ');
            if (step[..space] != surface) throw new InvalidOperationException($"replay diverged: recorded \"{step}\", asked on {surface}");
            return step[(space + 1)..];
        }
    }

    /// <summary>What the room's screen remembers. A new room starts fresh; a room resumed after an event combat too.</summary>
    private sealed class RoomProgress
    {
        /// <summary>Proceeded: the map is open.</summary>
        public bool Left;
        public bool CombatEntered;
        public int AutoWins;
        public bool RewardsOffered;
        public bool RestSucceeded;
        public bool ChestOpened;
        public bool RelicPicked;
    }
}
