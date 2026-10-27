using System.Text;
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
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>How a run starts: a new singleplayer run of <see cref="Character"/>; a loadout replaces the starting one.</summary>
internal sealed record RunSetup(string Character, string Seed, int Ascension = 0, Loadout? Loadout = null);

/// <summary>
/// One whole run in the real engine, started the way the game starts a new singleplayer run (simulator.md §5.1).
/// <see cref="Step"/> pumps the game until it waits for the player, then makes one decision: combat through the
/// combat policy, card prompts through the card selector, every other screen through the macro policy. Each decision
/// can be observed (<see cref="Observation"/>): policies build it on request, and <c>observed</c> sees all of them.
///
/// The game runs each room by itself; this class plays the part of the UI: which screen is showing and what its
/// buttons do. What a screen would remember (a rewards screen still open, the player having left the room) is kept
/// per room. Same setup and same policy answers, same run. Like <see cref="CombatSession"/>, one live at a time.
/// </summary>
internal sealed class RunSession : IDisposable
{
    /// <summary>Things the driver does without a decision (offer rewards, open a chest) between two decisions.</summary>
    private const int MaxAutoSteps = 20;

    private readonly Pump _pump;
    private readonly ICombatPolicy _combat;
    private readonly IMacroPolicy _macro;
    private readonly Action<Observation>? _observed;
    private readonly IDisposable _selectorScope;
    private readonly List<string> _trace = [];
    private readonly List<ulong> _mapHashes = [];

    /// <summary>Rewards screens open, newest last. The game hands each set over in <see cref="RewardsSet.testSelector"/>.</summary>
    private readonly List<RewardsSet> _shown = [];

    /// <summary>Game tasks the driver started (a purchase, a rest option, opening a chest), and what to do once each ends.</summary>
    private readonly List<(Task Task, Action? Done, string What)> _running = [];

    /// <summary>The crystal sphere screen, open over its event until the last divination.</summary>
    private CrystalSphereMinigame? _minigame;

    private AbstractRoom? _room;
    private RoomProgress _progress = new();
    private int? _endedTurn;
    private IReadOnlyList<CardRewardAlternative>? _alternatives;
    private readonly HashSet<CardRewardAlternative> _spentAlternatives = [];

    public RunSetup Setup { get; }
    public Player Player { get; }
    public RunState Run { get; }

    /// <summary>Every decision so far, as "surface action", including card prompts answered inside another action.</summary>
    public IReadOnlyList<string> Trace => _trace;

    /// <summary>A hash of each act's map, taken when the act is entered; the same in every process if generation is.</summary>
    public IReadOnlyList<ulong> MapHashes => _mapHashes;

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
        _combat = combat;
        _macro = macro;
        _observed = observed;
        var selector = new PolicySelector(choices, (choice, picks) => Record(Surfaces.CardChoice, string.Join(",", picks), () => new CardChoiceScreen
        {
            Choice = choice,
            Combat = CombatManager.Instance.IsInProgress ? Observe.Combat(CombatManager.Instance.DebugOnlyGetState()!, Player) : null,
        }), CardReward, Bundle);
        _selectorScope = CardSelectCmd.PushSelector(selector);
        RewardsSet.testSelector = set =>
        {
            set.ThrowInTestIfRewardsNotTaken = false;
            _shown.Add(set);
            return Task.CompletedTask;
        };
        HeadlessPatches.CrystalSphereShown = minigame => _minigame = minigame;
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
        setup.Loadout?.ApplyTo(player);

        // StartRunLobby.BeginRunLocally: the acts are rolled from the seed.
        var actRng = new Rng(StringHelper.GetDeterministicHashCode(setup.Seed), "act_selection");
        var acts = ActModel.GetRandomList(actRng, UnlockState.all, isMultiplayer: false).Select(a => a.ToMutable()).ToList();
        var run = RunState.CreateForNewRun([player], acts, [], GameMode.Standard, setup.Ascension, setup.Seed);
        RunManager.Instance.SetUpNewSingleplayer(run, shouldSave: false);

        var session = new RunSession(pump, setup, player, run, choices, combat, macro, observed);
        try
        {
            // NGame.StartRun without the nodes.
            session.Await(RunManager.Instance.FinalizeStartingRelics());
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

    /// <summary>Makes one decision, after anything the game or the driver does on its own. Does nothing once the run is over.</summary>
    public void Step()
    {
        for (int auto = 0; ; auto++)
        {
            Settle();
            if (IsOver) return;
            if (auto > MaxAutoSteps) throw new InvalidOperationException($"no decision after {MaxAutoSteps} automatic steps: {Describe()}");
            if (Decide()) return;
        }
    }

    /// <summary>Pumps until the player can act: in combat, their turn (a later one after ending a turn); elsewhere, the game idle.</summary>
    private void Settle()
    {
        _pump.RunUntil(() =>
        {
            if (CombatManager.Instance.IsInProgress)
                return CombatActions.WaitingForInput(Player) && (_endedTurn == null || Player.PlayerCombatState!.TurnNumber > _endedTurn);
            return _pump.Pending == 0 && !RunManager.Instance.ActionExecutor.IsRunning;
        }, Describe);
        _pump.Drain();
        _endedTurn = null;
        if (_mapHashes.Count == Run.CurrentActIndex && Run.Map is not NullActMap) _mapHashes.Add(MapHash(Run.Map));
        Collect();
    }

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
        _shown.RemoveAll(s => !RewardActions.IsTerminal(s) && RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(s));
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
        if (action == RewardActions.Done)
        {
            _shown.Remove(set);
            Track(RewardActions.Proceed(Run, set), "rewards proceed");
            if (RewardActions.IsTerminal(set)) _progress.Left = true;
        }
        else Track(RewardActions.Start(set, action), action);
        return true;
    }

    private bool MapDecision()
    {
        if (Ask(Surfaces.Map, MapActions.Legal(Run), () => MapActions.Screen(Run)) is { } action) MapActions.Start(Run, Player, action);
        return true;
    }

    /// <summary>A won combat shows its rewards (<c>NCombatUi.OnCombatWon</c>), or proceeds if the encounter gives none.</summary>
    private bool CombatStep(CombatRoom room)
    {
        if (CombatManager.Instance.IsInProgress) return CombatDecision();
        if (_progress.RewardsOffered) throw new InvalidOperationException($"combat over and no screen: {Describe()}");
        _progress.RewardsOffered = true;
        if (room.Encounter.ShouldGiveRewards) Track(room.OfferRoomEndRewards(), "offer combat rewards");
        else
        {
            Track(RunManager.Instance.ProceedFromTerminalRewardsScreen(), "proceed without rewards");
            _progress.Left = true;
        }
        return false;
    }

    private bool CombatDecision()
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat state");
        var legal = CombatActions.Legal(state, Player);
        var decision = new CombatDecision(legal, () => Observe.Combat(state, Player));
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
        if (action == EventActions.Proceed) _progress.Left = true;
        return true;
    }

    private bool CrystalSphereDecision(CrystalSphereMinigame minigame)
    {
        if (Ask(Surfaces.CrystalSphere, CrystalSphereActions.Legal(minigame), () => CrystalSphereActions.Screen(minigame)) is { } action) Track(CrystalSphereActions.Start(minigame, action), action);
        return true;
    }

    private bool ShopDecision(MerchantRoom room)
    {
        var inventory = room.GetLocalInventory();
        if (Ask(Surfaces.Shop, ShopActions.Legal(inventory), () => ShopActions.Screen(inventory)) is not { } action) return true;
        Track(ShopActions.Start(inventory, action), action);
        if (action == ShopActions.Leave) _progress.Left = true;
        return true;
    }

    private bool RestDecision()
    {
        var options = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions();
        var progress = _progress;
        if (Ask(Surfaces.Rest, RestActions.Legal(options, progress.RestSucceeded), () => RestActions.Screen(options)) is not { } action) return true;
        if (action == RestActions.Leave)
        {
            progress.Left = true;
            return true;
        }
        var chosen = RestActions.Start(options, progress.RestSucceeded, action);
        Track(chosen, action, () => progress.RestSucceeded |= chosen.Result);
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
        Track(RunManager.Instance.ProceedFromTerminalRewardsScreen(), "leave treasure");
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
    private string? Ask(string surface, List<string> legal, Func<ScreenView> screen)
    {
        legal.AddRange(PotionActions.Legal(Player));
        var action = Choose(surface, legal, screen);
        if (!PotionActions.Handles(action)) return action;
        PotionActions.Start(Player, action);
        return null;
    }

    private string Choose(string surface, List<string> legal, Func<ScreenView> screen)
    {
        var decision = new MacroDecision(surface, legal, () => Observe.Of(Run, Player, screen()));
        var action = _macro.Choose(decision);
        if (!legal.Contains(action)) throw new InvalidOperationException($"macro policy chose {action}, not legal on {surface}: {string.Join(" ", legal)}");
        _trace.Add($"{surface} {action}");
        _observed?.Invoke(decision.Observation);
        return action;
    }

    /// <summary>Adds a decision to the trace, and shows its observation to <see cref="_observed"/>.</summary>
    private void Record(string surface, string action, Func<ScreenView> screen)
    {
        _trace.Add($"{surface} {action}");
        _observed?.Invoke(Observe.Of(Run, Player, screen()));
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

    /// <summary>Pumps a task the driver must finish before going on (run setup).</summary>
    private void Await(Task task)
    {
        _pump.RunUntil(() => task.IsCompleted, Describe);
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
        _selectorScope.Dispose();
        _pump.Drain();
        RunManager.Instance.CleanUp(graceful: true);
        _pump.Drain();
        LocalContext.NetId = 1UL;
    }

    /// <summary>What the room's screen remembers. A new room starts fresh; a room resumed after an event combat too.</summary>
    private sealed class RoomProgress
    {
        /// <summary>Proceeded: the map is open.</summary>
        public bool Left;
        public bool RewardsOffered;
        public bool RestSucceeded;
        public bool ChestOpened;
        public bool RelicPicked;
    }
}
