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
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

internal enum CombatOutcome { InProgress, Win, Loss, Truncated }

/// <summary>
/// How a combat starts: a fresh run of <see cref="Character"/> with the loadout, entering the encounter. With
/// <see cref="From"/>, the combat is then made to show that observation (see <see cref="CombatBuilder"/>), and the
/// seed also picks what the observation hides.
/// </summary>
internal sealed record CombatSetup(string Character, EncounterModel Encounter, int ActIndex, string Seed, int Ascension = 0, Loadout? Loadout = null, CombatObservation? From = null);

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
}

/// <summary>A point in a combat, kept as the recipe to rebuild it: see <see cref="CombatSession.Restore"/>.</summary>
internal sealed record CombatSnapshot(CombatSetup Setup, IReadOnlyList<TraceEvent> Trace);

/// <summary>
/// One combat in the real engine: a fresh run with the setup's loadout (by default the character's starting deck
/// and relics), placed directly in the encounter's room. The driver lists legal actions, executes one, and pumps
/// the game until it is waiting for player input again (or the combat is over).
///
/// The game keeps the run and combat in process-wide singletons, so only one session can be live at a time.
/// Branching is done by replay: <see cref="Snapshot"/> records the setup and every action and choice so far,
/// and <see cref="Restore"/> rebuilds that exact state. Same setup and trace, same state: the game draws all its
/// randomness from the run's seeded streams.
/// </summary>
internal sealed class CombatSession : IDisposable
{
    private readonly Pump _pump;
    private readonly List<TraceEvent> _trace;
    private readonly PolicySelector _selector;
    private readonly IDisposable _selectorScope;

    public CombatSetup Setup { get; }
    public Player Player { get; }
    public RunState Run { get; }
    public int ActionsExecuted { get; private set; }
    public IReadOnlyList<TraceEvent> Trace => _trace;
    /// <summary>For a combat built from an observation: what it failed to reproduce, as JSON paths. Empty is exact.</summary>
    public IReadOnlyList<string> BuildDifferences { get; private set; } = [];

    private CombatSession(Pump pump, CombatSetup setup, Player player, RunState run, List<TraceEvent> trace, PolicySelector selector)
    {
        _pump = pump;
        Setup = setup;
        Player = player;
        Run = run;
        _trace = trace;
        _selector = selector;
        _selectorScope = CardSelectCmd.PushSelector(selector);
    }

    public static CombatSession Start(Pump pump, CombatSetup setup, IChoicePolicy policy) => Start(pump, setup, policy, []);

    /// <summary>Rebuilds the combat at <paramref name="snapshot"/>; afterwards, choices come from <paramref name="policy"/>.</summary>
    public static CombatSession Restore(Pump pump, CombatSnapshot snapshot, IChoicePolicy policy)
    {
        var picks = snapshot.Trace.Where(e => e.Kind == TraceKind.Choice).Select(e => e.Picks!);
        var session = Start(pump, snapshot.Setup, policy, picks);
        try
        {
            foreach (var e in snapshot.Trace)
            {
                if (e.Kind == TraceKind.Action) session.Execute(e.Action);
                else if (e.Kind == TraceKind.Determinize) session.Determinize(e.Seed);
            }
            if (session._selector.PendingReplay > 0)
                throw new InvalidOperationException($"replay diverged: {session._selector.PendingReplay} recorded choices were never asked for");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public CombatSnapshot Snapshot() => new(Setup, _trace.ToArray());

    private static CombatSession Start(Pump pump, CombatSetup setup, IChoicePolicy policy, IEnumerable<int[]> replayPicks)
    {
        var character = ModelDb.AllCharacters.First(c => c.Id.Entry.Equals(setup.Character, StringComparison.OrdinalIgnoreCase));
        var player = Player.CreateForNewRun(character, UnlockState.all, LocalContext.NetId!.Value);
        setup.Loadout?.ApplyTo(player);

        var acts = ActModel.GetDefaultList().ToList();
        var encounterAct = ModelDb.ActsByIndex[setup.ActIndex].FirstOrDefault(a => a.AllEncounters.Contains(setup.Encounter));
        if (encounterAct != null) acts[setup.ActIndex] = encounterAct;

        var run = RunState.CreateForTest([player], acts, null, GameMode.Standard, setup.Ascension, setup.Seed);
        run.CurrentActIndex = setup.ActIndex;
        run.Map = new MockSinglePointActMap();
        RunManager.Instance.SetUpTest(run, new NetSingleplayerGameService(), disableCombatStateSync: true, shouldSave: false);

        var trace = new List<TraceEvent>();
        var selector = new PolicySelector(policy, picks => trace.Add(new(TraceKind.Choice, Picks: picks)));
        selector.QueueReplay(replayPicks);
        var session = new CombatSession(pump, setup, player, run, trace, selector);
        try
        {
            var enter = RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: setup.Encounter.ToMutable(), showTransition: false);
            pump.RunUntil(() => enter.IsCompleted, session.Describe);
            if (enter.IsFaulted) throw enter.Exception!.GetBaseException();
            session.Settle();
            if (setup.From is { } obs)
            {
                CombatBuilder.Apply(session.State, player, obs, pump);
                session.Resample(new Rng(StableHash(setup.Seed)));
                session.BuildDifferences = CombatBuilder.Differences(obs, session.Observation());
            }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public CombatState State => CombatManager.Instance.DebugOnlyGetState()
        ?? throw new InvalidOperationException("no combat state");

    private PlayerCombatState Pcs => Player.PlayerCombatState
        ?? throw new InvalidOperationException("player has no combat state");

    public bool IsOver => !CombatManager.Instance.IsInProgress;

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

    public List<CombatAction> LegalActions() => CombatActions.Legal(State, Player);

    public CombatObservation Observation() => Observe.Combat(State, Player);

    public void Execute(CombatAction action)
    {
        _trace.Add(new(TraceKind.Action, action));
        int turn = Pcs.TurnNumber;
        CombatActions.Start(State, Player, action);
        Settle(afterTurn: action.Kind == CombatActionKind.EndTurn ? turn : null);
        ActionsExecuted++;
    }

    /// <summary>
    /// Replaces everything the player cannot know with a sample drawn from <paramref name="seed"/>. The visible state
    /// is unchanged. A search that branches only from determinized states cannot use the real future, and different
    /// seeds give different plausible futures.
    /// </summary>
    public void Determinize(ulong seed)
    {
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
        var db = NetCombatCardDb.Instance;
        CombatBuilder.NumberCards(Pcs, Pcs.Hand.Cards.Select(db.GetCardId).ToList());

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
    private static ulong StableHash(string text)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in text) hash = (hash ^ c) * 1099511628211;
        return hash;
    }

    /// <summary>Pumps until the player can act again (on a later turn, if given) or the combat has ended.</summary>
    private void Settle(int? afterTurn = null)
    {
        _pump.RunUntil(() =>
        {
            if (IsOver) return _pump.Pending == 0 && !RunManager.Instance.ActionExecutor.IsRunning;
            return WaitingForInput && (afterTurn == null || Pcs.TurnNumber > afterTurn);
        }, Describe);
        _pump.Drain();
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

    public void Dispose()
    {
        _selectorScope.Dispose();
        _pump.Drain();
        RunManager.Instance.CleanUp(graceful: true);
        _pump.Drain();
        LocalContext.NetId = 1UL;
    }
}
