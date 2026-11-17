using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

internal enum CombatOutcome { InProgress, Win, Loss, Truncated }

/// <summary>
/// How a combat starts: a fresh run of <see cref="Character"/> with the loadout, entering the encounter. With
/// <see cref="From"/>, the combat is then made to show that observation (see <see cref="CombatBuilder"/>), and the
/// seed also picks what the observation hides; <see cref="History"/>, the enemies' intents in the rounds so far (the
/// record the player saw), gives each monster the moves it made before.
/// </summary>
internal sealed record CombatSetup(string Character, EncounterModel Encounter, int ActIndex, string Seed, int Ascension = 0, Loadout? Loadout = null,
    CombatObservation? From = null, IReadOnlyList<EnemyTurns>? History = null);

internal enum TraceKind { Action, Choice, Determinize }

/// <summary>One step of a combat's history. A setup plus its trace is enough to rebuild the combat by replay.</summary>
internal readonly record struct TraceEvent(TraceKind Kind, CombatAction Action = default, int[]? Picks = null, ulong Seed = 0)
{
    public override string ToString() => Kind switch
    {
        TraceKind.Action => Action.ToString(),
        TraceKind.Choice => $"choose:{string.Join(",", Picks!)}",
        _ => $"determinize:{Seed}",
    };

    public static TraceEvent Parse(string text)
    {
        if (text.StartsWith("choose:", StringComparison.Ordinal))
            return new(TraceKind.Choice, Picks: text.Length == 7 ? [] : [.. text[7..].Split(',').Select(int.Parse)]);
        if (text.StartsWith("determinize:", StringComparison.Ordinal)) return new(TraceKind.Determinize, Seed: ulong.Parse(text[12..]));
        return new(TraceKind.Action, CombatAction.Parse(text));
    }
}

/// <summary>A point in a combat, kept as the recipe to rebuild it: see <see cref="CombatSession.Restore"/>.</summary>
internal sealed record CombatSnapshot(CombatSetup Setup, IReadOnlyList<TraceEvent> Trace);

/// <summary>
/// One combat in the real engine: a fresh run with the setup's loadout (by default the character's starting deck
/// and relics), placed directly in the encounter's room. The driver lists legal actions, executes one, and pumps
/// the game until it is waiting for player input again (or the combat is over).
///
/// A card prompt an action raises is a state of the combat: the game waits on it
/// (<see cref="Prompt"/>), no play is legal, and the next action is its answer (<see cref="CombatAction.Pick"/>). Only
/// the prompts of the combat's start are answered here, by the policy the session is given: a combat built from an
/// observation starts before the state it is built to show, and those are no decisions of that state's.
///
/// The game keeps the run and combat in process-wide singletons, so only one session can be live at a time.
/// Branching is done by replay: <see cref="Snapshot"/> records the setup and every action and choice so far,
/// and <see cref="Restore"/> rebuilds that exact state. Same setup and trace, same state: the game draws all its
/// randomness from the run's seeded streams.
/// </summary>
internal sealed class CombatSession : IDisposable
{
    private const int MaxActions = 1000;

    private readonly Pump _pump;
    private readonly List<TraceEvent> _trace;
    private readonly PromptSelector _selector;
    private readonly IDisposable _selectorScope;
    private readonly IChoicePolicy _prompts;
    /// <summary>The turn the player ended, until the next one starts: a prompt on the way does not end the wait.</summary>
    private int? _endedTurn;

    public CombatSetup Setup { get; }
    public Player Player { get; }
    public RunState Run { get; }
    public int ActionsExecuted { get; private set; }
    public IReadOnlyList<TraceEvent> Trace => _trace;
    /// <summary>For a combat built from an observation: what it failed to reproduce, as JSON paths. Empty is exact; a
    /// restored combat is not checked again, and shows none.</summary>
    public IReadOnlyList<string> BuildDifferences { get; private set; } = [];

    private CombatSession(Pump pump, CombatSetup setup, Player player, RunState run, List<TraceEvent> trace, PromptSelector selector, IChoicePolicy prompts)
    {
        _pump = pump;
        Setup = setup;
        Player = player;
        Run = run;
        _trace = trace;
        _selector = selector;
        _prompts = prompts;
        _selectorScope = PromptSelector.Install(player, selector);
    }

    /// <param name="prompts">Answers the card prompts of the combat's start, and those of a combat played by <see cref="Step"/>.</param>
    public static CombatSession Start(Pump pump, CombatSetup setup, IChoicePolicy prompts) => Start(pump, setup, prompts, [], check: true);

    /// <summary>Rebuilds the combat at <paramref name="snapshot"/>: its start, then its actions and answers in order.</summary>
    public static CombatSession Restore(Pump pump, CombatSnapshot snapshot, IChoicePolicy prompts)
    {
        // The answers given while the combat started come first in the trace; the start asks for them again.
        int started = snapshot.Trace.TakeWhile(e => e.Kind == TraceKind.Choice).Count();
        // A built combat was checked when its setup was first started (Start); a restore does not check it again.
        var session = Start(pump, snapshot.Setup, prompts, [.. snapshot.Trace.Take(started).Select(e => e.Picks!)], check: false);
        try
        {
            foreach (var e in snapshot.Trace.Skip(started))
            {
                if (e.Kind == TraceKind.Action) session.Execute(e.Action);
                else if (e.Kind == TraceKind.Choice) session.Replay(e.Picks!);
                else session.Determinize(e.Seed);
            }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public CombatSnapshot Snapshot() => new(Setup, [.. _trace]);

    private static CombatSession Start(Pump pump, CombatSetup setup, IChoicePolicy prompts, IReadOnlyList<int[]> replayPicks, bool check)
    {
        var character = ModelDb.AllCharacters.First(c => c.Id.Entry.Equals(setup.Character, StringComparison.OrdinalIgnoreCase));
        var player = Player.CreateForNewRun(character, UnlockState.all, LocalContext.NetId!.Value);
        setup.Loadout?.ApplyTo(player, setup.Ascension);

        var acts = ActModel.GetDefaultList().ToList();
        var encounterAct = ModelDb.ActsByIndex[setup.ActIndex].FirstOrDefault(a => a.AllEncounters.Contains(setup.Encounter));
        if (encounterAct != null) acts[setup.ActIndex] = encounterAct;

        var run = RunState.CreateForTest([player], acts, null, GameMode.Standard, isCustomGame: false, setup.Ascension, setup.Seed);
        run.CurrentActIndex = setup.ActIndex;
        run.Map = new MockSinglePointActMap();
        try
        {
            RunManager.Instance.SetUpTest(run, new NetSingleplayerGameService(), disableCombatStateSync: true, shouldSave: false);
        }
        catch
        {
            // The game holds the run from the first line of its setup: one that fails there must not stay, or no
            // combat of the process starts after it.
            RunManager.Instance.CleanUp(graceful: true);
            pump.Drain();
            LocalContext.NetId = 1UL;
            throw;
        }

        var trace = new List<TraceEvent>();
        var selector = new PromptSelector(pump, (_, picks) => trace.Add(new(TraceKind.Choice, Picks: picks)));
        var session = new CombatSession(pump, setup, player, run, trace, selector, prompts);
        try
        {
            // The start's prompts get the recorded answers first, then the policy's.
            var replay = new Queue<int[]>(replayPicks);
            bool Answered()
            {
                if (selector.Waiting is not { } prompt) return false;
                if (replay.TryDequeue(out var picks)) selector.Replay(picks);
                else selector.Answer(prompts.Choose(new ChoiceDecision(prompt)).Picks);
                return true;
            }
            var enter = RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: setup.Encounter.ToMutable(), showTransition: false);
            do pump.RunUntil(() => selector.Waiting != null || enter.IsCompleted, session.Describe);
            while (Answered());
            if (enter.IsFaulted) throw enter.Exception!.GetBaseException();
            do session.Settle();
            while (Answered());
            if (replay.Count > 0) throw new InvalidOperationException($"replay diverged: {replay.Count} answers recorded at the combat's start were never asked for");
            // A start-of-combat effect can kill a player built at low HP (Royal Poison's 4 damage at 3 HP), and a dead
            // player's relics, powers and cards no longer act. Such a combat starts again at full HP; the build then
            // sets the observed HP as always. A relic that keeps a flag by the HP at the start (Red Skull) can then
            // be off, which only this rare start risks.
            if (setup.From != null && player.Creature.IsDead && setup.Loadout is { Hp: { } hp, MaxHp: { } maxHp } && hp < maxHp)
            {
                session.Dispose();
                return Start(pump, setup with { Loadout = setup.Loadout with { Hp = maxHp } }, prompts, replayPicks, check);
            }
            if (setup.From is { } obs)
            {
                CombatBuilder.Apply(State, player, obs, pump, setup.History);
                session.Resample(new Rng(StableHash(setup.Seed)));
                if (check) session.BuildDifferences = CombatBuilder.Differences(obs, session.Observation());
            }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public static CombatState State => CombatManager.Instance.DebugOnlyGetState()
        ?? throw new InvalidOperationException("no combat state");

    private PlayerCombatState Pcs => Player.PlayerCombatState
        ?? throw new InvalidOperationException("player has no combat state");

    public static bool IsOver => !CombatManager.Instance.IsInProgress;

    public CombatOutcome Outcome
    {
        get
        {
            if (!IsOver) return CombatOutcome.InProgress;
            if (Player.Creature.IsDead) return CombatOutcome.Loss;
            return State.Enemies.Any(e => e.IsAlive) ? CombatOutcome.Truncated : CombatOutcome.Win;
        }
    }

    public bool WaitingForInput => CombatActions.WaitingForInput(Player);

    /// <summary>The card prompt the combat waits on, if any: its answer is the next action.</summary>
    public CardChoice? Prompt => _selector.Waiting;

    /// <summary>The plays the game allows: none while a prompt waits.</summary>
    public List<CombatAction> LegalActions() => Prompt != null ? [] : CombatActions.Legal(State, Player);

    public CombatObservation Observation()
    {
        var obs = Observe.Combat(State, Player);
        return Prompt is { } prompt ? obs with { Prompt = prompt } : obs;
    }

    /// <summary>Whether the combat is over or at the action cap.</summary>
    public bool IsCapped => IsOver || ActionsExecuted >= MaxActions;

    public CombatDecision Decision() => new(LegalActions(), Observation);

    /// <summary>Plays until the combat ends or hits the action cap. Calls <paramref name="beforeAction"/> before each action.</summary>
    public void PlayOut(ICombatPolicy policy, Action<CombatSession>? beforeAction = null)
    {
        while (!IsCapped)
        {
            beforeAction?.Invoke(this);
            Step(policy);
        }
    }

    /// <summary>The next action of a combat that policies play: <paramref name="policy"/>'s, or at a card prompt the
    /// answer of the session's prompt policy.</summary>
    public CombatAction Next(ICombatPolicy policy) => Prompt is { } prompt ? Answer(prompt) : policy.Choose(Decision());

    /// <summary>The session's prompt policy's answer to a prompt, as an action.</summary>
    private CombatAction Answer(CardChoice prompt) => CombatAction.Pick(_prompts.Choose(new ChoiceDecision(prompt)).Picks);

    /// <summary>Ends the player's turn and goes on to their next one: a card prompt on the way (an enemy's move can ask
    /// one) gets the session's prompt policy's answer.</summary>
    public void EndTurn()
    {
        Execute(CombatAction.EndTurn);
        while (Prompt is { } prompt) Execute(Answer(prompt));
    }

    /// <summary>Takes the next action of a combat that policies play (<see cref="Next"/>).</summary>
    public void Step(ICombatPolicy policy) => Execute(Next(policy));

    /// <summary>Takes the action and pumps to where the player can act again: their turn, or a card prompt.</summary>
    public void Execute(CombatAction action)
    {
        if ((action.Kind == CombatActionKind.Pick) != (Prompt != null))
            throw new InvalidOperationException(Prompt != null ? $"{action} while a card prompt waits for its answer" : $"{action} with no card prompt to answer");
        if (action.Kind == CombatActionKind.Pick) _selector.Answer(action.Picks);
        else
        {
            _trace.Add(new(TraceKind.Action, action));
            if (action.Kind == CombatActionKind.EndTurn) _endedTurn = Pcs.TurnNumber;
            CombatActions.Start(State, Player, action);
        }
        Settle();
        ActionsExecuted++;
    }

    /// <summary>Answers the waiting prompt with a recorded answer, the game's own indexes, and pumps on.</summary>
    private void Replay(int[] picks)
    {
        _selector.Replay(picks);
        Settle();
        ActionsExecuted++;
    }

    /// <summary>
    /// Replaces everything the player cannot know with a sample drawn from <paramref name="seed"/>. The visible state
    /// is unchanged. A search that branches only from determinized states cannot use the real future, and different
    /// seeds give different plausible futures.
    /// </summary>
    public void Determinize(ulong seed)
    {
        if (Prompt != null) throw new InvalidOperationException("determinize while a card prompt waits: the middle of an action cannot be sampled");
        _trace.Add(new(TraceKind.Determinize, Seed: seed));
        // Seeded only by the caller, never by the run, so nothing about the real seed leaks into the sample.
        Resample(new Rng(seed));
    }

    /// <summary>
    /// Draws the hidden state from <paramref name="sample"/>: every seeded stream (shuffles, generated cards, monster
    /// moves, random targets, ...), the order of the draw pile, the ids of cards outside the hand (the game numbers
    /// them in draw order), and each monster's queued move among those showing the same intents.
    /// Not yet modeled: draw-pile cards whose position the player does know (e.g. one just put on top), and which of
    /// those moves the monster's history makes likelier.
    /// </summary>
    private void Resample(Rng sample)
    {
        foreach (var type in Enum.GetValues<RunRngType>()) Reseed(Run.Rng.GetRng(type), sample);
        foreach (var type in Enum.GetValues<PlayerRngType>()) Reseed(Player.PlayerRng.GetRng(type), sample);
        foreach (var creature in State.Enemies.Concat(State.Allies))
            if (creature.Monster is { IsMutable: true } monster) Reseed(monster.Rng, sample);

        // The real order is hidden, so start from an order that depends only on what the player can see.
        var draw = Pcs.DrawPile;
        var cards = draw.Cards.OrderBy(Observe.CardKey, StringComparer.Ordinal).ToList();
        for (int i = cards.Count - 1; i > 0; i--)
        {
            int j = sample.NextInt(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        foreach (var card in cards) draw.MoveToBottomInternal(card);
        // The pile's order is a uniform draw nobody saw: its public record starts again.
        DrawPileLog.Shuffled(draw);
        var db = NetCombatCardDb.Instance;
        CombatBuilder.NumberCards(Pcs, [.. Pcs.Hand.Cards.Select(db.GetCardId)]);

        Creature[] targets = [Player.Creature];
        foreach (var enemy in State.Enemies)
        {
            if (enemy.IsDead || enemy.Monster?.MoveStateMachine is not { } machine) continue;
            var monster = enemy.Monster;
            var moves = CombatBuilder.MovesShowing(enemy, targets, CombatBuilder.Shows(monster.NextMove, enemy, targets));
            if (moves.Count < 2) continue;
            var move = moves[sample.NextInt(moves.Count)];
            if (move == monster.NextMove) continue;
            if (machine.StateLog.Count > 0 && machine.StateLog[^1] == monster.NextMove) machine.StateLog[^1] = move;
            monster.SetMoveImmediate(move, forceTransition: true);
        }

        static void Reseed(Rng rng, Rng sample) => rng.LoadFromSerializable(new Rng(sample.NextUnsignedLong()).ToSerializable());
    }

    /// <summary>FNV-1a: a string hash that is the same in every process, unlike string.GetHashCode.</summary>
    internal static ulong StableHash(string text)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in text) hash = (hash ^ c) * 1099511628211;
        return hash;
    }

    /// <summary>Pumps until the player can act again (on a later turn, after ending one), a card prompt waits, or the
    /// combat has ended. A fight past the round cap is lost there (<see cref="FightCap"/>).</summary>
    private void Settle()
    {
        void Pump()
        {
            _pump.RunUntil(() =>
            {
                if (Prompt != null) return true;
                if (IsOver) return _pump.Pending == 0 && !RunManager.Instance.ActionExecutor.IsRunning;
                return WaitingForInput && (_endedTurn == null || Pcs.TurnNumber > _endedTurn);
            }, Describe);
            _pump.Drain();
        }
        Pump();
        if (Prompt != null) return;
        if (FightCap.Enforce(_pump, Player, Describe)) Pump();
        _endedTurn = null;
    }

    public string Describe()
    {
        var st = CombatManager.Instance.DebugOnlyGetState();
        var pcs = Player.PlayerCombatState;
        var executor = RunManager.Instance.ActionExecutor;
        return $"inProgress={CombatManager.Instance.IsInProgress} side={st?.CurrentSide} phase={pcs?.Phase} turn={pcs?.TurnNumber} " +
               $"executorRunning={executor.IsRunning} paused={executor.IsPaused} action={executor.CurrentlyRunningAction} " +
               $"actionsDisabled={CombatManager.Instance.PlayerActionsDisabled} pending={_pump.Pending}";
    }

    public string Summary()
    {
        var st = State;
        var pcs = Player.PlayerCombatState;
        var me = Player.Creature;
        // Uids as in actions: "play:7:2" plays card #7 at creature #2.
        var enemies = string.Join(" ", st.Enemies.Select(e => $"#{e.CombatId}:{e.Monster?.Id.Entry}({e.CurrentHp}/{e.MaxHp}{(e.Block > 0 ? $" b{e.Block}" : "")})"));
        var hand = pcs == null ? "" : string.Join(" ", pcs.Hand.Cards.Select(c =>
            $"#{NetCombatCardDb.Instance.GetCardId(c)}:{c.Id.Entry}{(c.CurrentUpgradeLevel > 0 ? "+" : "")}"));
        return $"round {st.RoundNumber} hp {me.CurrentHp}/{me.MaxHp} block {me.Block} energy {pcs?.Energy} | {enemies} | hand [{hand}]";
    }

    /// <summary>
    /// The game's card db lets go of the piles it listens to only when a combat ends. A session left in the middle of
    /// one (a world of search, a node's clone, a combat built from an observation) would leave the db holding its piles,
    /// and through them its whole run, for as long as the process lives; and a combat written out for the engine later
    /// in that process would carry every such run (the export starts from the db). This is what the combat's end does
    /// there; nothing where no combat is going on.
    /// </summary>
    public static void CardDbLetsGo() => CardDbCombatEnded.Invoke(NetCombatCardDb.Instance, [null]);

    private static readonly System.Reflection.MethodInfo CardDbCombatEnded =
        typeof(NetCombatCardDb).GetMethod("OnCombatEnded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new MissingMethodException("NetCombatCardDb.OnCombatEnded: the game's card db changed");

    public void Dispose()
    {
        _selectorScope.Dispose();
        // Nothing runs while a prompt waits: the game stands where it asked until its run is cleaned up.
        _pump.Drain();
        CardDbLetsGo();
        RunManager.Instance.CleanUp(graceful: true);
        _selector.Drop();
        _pump.Drain();
        LocalContext.NetId = 1UL;
    }
}
