using System.Runtime.ExceptionServices;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>The combat a solver begins on, taken in where a decision stands (<see cref="SolverRoute"/>): in the engine,
/// where the search runs (plans/2026-11-18-2132-sl-search-rust.md).</summary>
/// <param name="Observe">The combat's observation from there after the actions given.</param>
/// <param name="Plan">The beam's plan from there.</param>
/// <param name="Solver">A solver from there, with the record of enemy moves there and the pick's turn decay.</param>
public sealed record TakenCombat(Func<IReadOnlyList<CombatAction>, CombatObservation> Observe,
    Func<Budget, IReadOnlyList<EnemyTurns>, SlPlan> Plan,
    Func<IReadOnlyList<EnemyTurns>, double, ICombatSolver> Solver);

/// <summary>
/// max's combat route (plans/2026-11-18-1412-max-solver.md, 2.3–2.5): one solver a combat (<see cref="ICombatSolver"/>), which
/// searches <see cref="Budget.Solve"/> more nodes at each decision, then answers with its pick (the user, 2026-11-18,
/// of the ways auto-play could wait for it: 「第二種」). The same budget gives the same decisions, so self-play and the
/// tests measure what max plays; the mod's brain may give a decision more, never less.
///
/// The solver begins where the combat stands, taken in once (<c>start</c>), from the beam's plan that the rest of the
/// budget makes there (<see cref="TakenCombat.Plan"/>), so its pick is never worse than the beam's. At each decision
/// after, if the combat went the pick's way, the observation being the one its line expected before its next step, the
/// solver's root moves along it (<see cref="ICombatSolver.Advance"/>) and keeps what it found below. Otherwise (the player
/// moved elsewhere, a new combat began, the engine played a step otherwise) a new solver begins. A decision with one
/// thing to do is answered without search, and the root moves at the next one. Where the combat cannot be taken in
/// (no live combat, and no clone of the node at a card prompt) the beam decides.
///
/// In the background (<paramref name="background"/>: the mod's brain, whose game takes its time between decisions) the
/// solver searches on a thread of its own the combat long. The root moves along each step the combat is expected to
/// take as soon as the route gives it, and the solver searches on there until the next decision. That decision is
/// answered once the solver has searched at least <see cref="Budget.Solve"/> nodes since its root moved there, or at
/// once when nothing is left, so it is never searched less than without the background, and what was found while the
/// game played the step stays. <see cref="Rest"/> stops it. Meanwhile the host is told the pick so far
/// (<see cref="CombatDecision.Early"/>), at once and before any search: the rest of the plan given at the last answer
/// while the combat goes its way, the beam's plan where a solver begins; then again each time the solver's pick changes
/// (the user, 2026-11-18: 「ai得能及時給出來怎麼走，並且同時在搜索更好的」). A host that moved on from the decision
/// (<see cref="CombatDecision.Hurry"/>) gets the pick at once. After the answer, while the solver searches on from where
/// it leads, the host is told the answer again each time the plan after its action changes, a better line found or the
/// pick there proved (<see cref="CombatDecision.After"/>), until the next decision (the user, 2026-11-19, of the panel
/// showing the answer's plan unchanged while the solver had found a better one: 「肯定要更新啊」); the plan told last is
/// then the line the combat goes along from that decision.
/// </summary>
public sealed class SolverRoute(Budget budget, bool background = false)
{
    private ICombatSolver? _solver;
    /// <summary>The combat's steps since the solver's root, which the root moves by at the next decision searched.</summary>
    private readonly List<CombatAction> _since = [];
    /// <summary>The solver searching in the background, if it does.</summary>
    private Background? _background;
    /// <summary>The pick from the root, how many of its steps the combat took since, and how many of them the plan
    /// given with it held.</summary>
    private TurnEnd? _line;
    private int _taken, _given;
    /// <summary>The pick so far the host was last told at this decision, as it shows it (<see cref="Shown"/>).</summary>
    private string? _told;

    // Under _afterLock: the answer whose plan after it the host is told (a token of its own, null when none), the answer
    // as the host was last told it (Shown), and where it was told again, the whole line from that decision and how many of
    // its steps the plan told held.
    private readonly object _afterLock = new();
    private object? _afterFor;
    private string? _afterShown;
    private TurnEnd? _afterLine;
    private int _afterGiven;

    /// <summary>How many solvers the route began: one a combat while the combat goes the picks' way.</summary>
    public int Begun { get; private set; }

    /// <summary>The last decision's search: the solver's nodes into it when its answer's first action last changed (0:
    /// the answer it began with); null in the background, whose solver its own thread owns.</summary>
    private long? _settled;

    /// <summary>The last decision's search: the pick it began with (where a solver begins, the beam's plan it begins from;
    /// else the pick once the root moved there), as a candidate; null in the background. Beside the answer and
    /// <see cref="_settled"/>, what an answer that changed late gained (plans/2026-11-20-1231-silent-shiv-poison.md, 2.4).
    /// A record, which nothing the search does reads.</summary>
    private Candidate? _opened;

    /// <summary>A solver begun here that has not searched yet: the beam's plan it begins from.</summary>
    private SlPlan? _unsearched;

    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    /// <param name="start">Takes the combat in where the decision stands; null where it cannot.</param>
    /// <param name="beam">The beam's decision, where no solver can begin.</param>
    /// <param name="early">In the background, told the pick so far before the decision's search.</param>
    /// <param name="hurry">In the background, cancelled when the host moved on: the decision's search ends there.</param>
    /// <param name="after">In the background, told the answer again with each plan after its action the search shows
    /// otherwise, until the next decision.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IReadOnlyList<EnemyTurns> history,
        Func<TakenCombat?> start, Func<CombatDecisionResult> beam, Action<CombatDecisionResult>? early = null, CancellationToken hurry = default,
        Action<CombatDecisionResult>? after = null)
    {
        Quiet();
        ulong hash = ProtocolJson.Hash(observation);
        // Past the line's last step the combat goes on where it ends; the root's search there shows whether it does.
        bool along = _line is { } line && (_taken < line.Actions.Count
            ? line.Hashes[_taken] == hash && observation.Takes(line.Actions[_taken], legal)
            : line.Status == CombatStatus.InProgress);
        if (PimcBrain.Forced(observation, legal) is { } forced)
        {
            if (along) Took(_taken < _line!.Actions.Count ? _line.Actions[_taken] : forced);
            else Drop();
            return PimcBrain.Only(forced);
        }
        long nodes = 0;
        _told = null;
        if (along && _background != null && _taken < _given) Tell(early, Given());
        if (!along && !Begin(observation, history, start, early, ref nodes))
        {
            Drop();
            return beam();
        }
        var plan = Search(history, early, hurry, ref nodes);
        // The engine's combat is not this one: it played a step otherwise than the game.
        if (plan.Line!.Hashes[0] != hash)
        {
            if (!Begin(observation, history, start, early, ref nodes))
            {
                Drop();
                return beam();
            }
            plan = Search(history, early, hurry, ref nodes);
            if (plan.Line!.Hashes[0] != hash)
                throw new InvalidOperationException("the combat taken in is not the combat the decision shows");
        }
        (_line, _taken, _given) = (plan.Line, 0, plan.Result.Plan.Count + 1);
        Took(plan.Line.Actions[0]);
        var answer = plan.Result with
        {
            Nodes = (int)Math.Min(nodes, int.MaxValue), Proved = plan.Proved, Settled = _settled is { } s ? (int)Math.Min(s, int.MaxValue) : null,
            Opened = _opened,
        };
        if (after != null && _background != null) Watch(answer, after);
        return answer;
    }

    /// <summary>From now on, until the next decision, <paramref name="after"/> is told <paramref name="answer"/> again each
    /// time the solver's pick from where it leads shows its plan otherwise (<see cref="After"/>).</summary>
    private void Watch(CombatDecisionResult answer, Action<CombatDecisionResult> after)
    {
        var (line, token) = (_line!, new object());
        lock (_afterLock) (_afterFor, _afterShown, _afterLine) = (token, Shown(answer), null);
        _background!.Watch((pick, since) =>
        {
            var (told, whole) = After(answer, line, pick, since);
            lock (_afterLock)
            {
                if (_afterFor != token || Shown(told) == _afterShown) return;
                (_afterShown, _afterLine, _afterGiven) = (Shown(told), whole, told.Plan.Count + 1);
                after(told);
            }
        });
    }

    /// <summary>The answer with the plan after its action as the solver's pick from there has it: that pick's steps, its
    /// line's chance to win and HP left, whether it is proved, and the nodes searched since the decision was asked; and the
    /// whole line from the decision, <paramref name="line"/>'s first step and then the pick's.</summary>
    /// <param name="since">The nodes the solver searched since the answer.</param>
    private static (CombatDecisionResult Told, TurnEnd Line) After(CombatDecisionResult answer, TurnEnd line, SlPlan pick, long since)
    {
        var rest = pick.Line!;
        var whole = rest with { Actions = [line.Actions[0], .. rest.Actions], Hashes = [line.Hashes[0], .. rest.Hashes] };
        var top = Top(answer.First, rest);
        var told = answer with
        {
            Plan = [pick.Result.First, .. pick.Result.Plan],
            PlanHashes = [rest.Hashes[0], .. pick.Result.PlanHashes ?? []],
            Candidates = [top, .. answer.Candidates.Skip(1)],
            Nodes = (int)Math.Min((answer.Nodes ?? 0) + since, int.MaxValue),
            Proved = pick.Proved,
        };
        return (told, whole);
    }

    /// <summary>The plan after the last answer is told no more. Where it was told again, the line told last is the line the
    /// combat goes along from that decision (<see cref="Given"/>).</summary>
    private void Quiet()
    {
        _background?.Watch(null);
        lock (_afterLock)
        {
            if (_afterLine is { } told) (_line, _given) = (told, _afterGiven);
            (_afterFor, _afterShown, _afterLine) = (null, null, null);
        }
    }

    /// <summary>Tells the host the pick so far, unless it shows what it was last told.</summary>
    private void Tell(Action<CombatDecisionResult>? early, CombatDecisionResult pick)
    {
        if (early == null || Shown(pick) == _told) return;
        _told = Shown(pick);
        early(pick);
    }

    /// <summary>A pick as the host shows it: its step, the plan after it, and the line's chance to win and HP left.</summary>
    private static string Shown(CombatDecisionResult pick) =>
        string.Join(" ", [pick.First, .. pick.Plan]) + (pick.Candidates is [var top, ..] ? FormattableString.Invariant($" {top.Win} {top.Hp}") : "") +
        (pick.Proved ? " proved" : "");

    /// <summary>The rest of the plan given at the last answer, from the step the combat stands at, with the line's own
    /// value: the pick so far until the decision's search says otherwise.</summary>
    private CombatDecisionResult Given()
    {
        var line = _line!;
        var first = line.Actions[_taken];
        return new(first, [.. line.Actions.Take(_given).Skip(_taken + 1)], [Top(first, line)], [.. line.Hashes.Take(_given).Skip(_taken + 1)]);
    }

    /// <summary><paramref name="first"/> as a candidate with <paramref name="line"/>'s own value, chance to win and HP left.</summary>
    private static Candidate Top(CombatAction first, TurnEnd line) =>
        new(first.ToString(), line.Value, 0, 1, [Math.Round(line.Value, 4)], Math.Round(line.Win, 4), Math.Round(line.Hp, 1));

    /// <summary>No decision of this combat comes now: it ended, or the run did. The solver goes, and with it what it holds.</summary>
    public void Rest() => Drop();

    /// <summary>The combat is to take the line's next step: the root moves along it, at once in the background. Where the
    /// step ends the combat, the solver is done.</summary>
    private void Took(CombatAction step)
    {
        _taken++;
        if (_background == null) _since.Add(step);
        else if (_taken < _line!.Actions.Count || _line.Status == CombatStatus.InProgress) _background.Post(step);
        else Drop();
    }

    /// <summary>A new solver where the decision stands, from the beam's plan there; false where the combat cannot be
    /// taken in.</summary>
    private bool Begin(CombatObservation observation, IReadOnlyList<EnemyTurns> history, Func<TakenCombat?> start,
        Action<CombatDecisionResult>? early, ref long nodes)
    {
        if (start() is not { } from) return false;
        Drop();
        var solver = from.Solver(history, budget.TurnDecay);
        var seed = from.Plan(budget.For(observation.Room) with { Solve = 0 }, history);
        solver.Seed(seed);
        nodes += seed.Nodes;
        Begun++;
        if (background)
        {
            Tell(early, seed.Result);
            _background = new Background(solver, from, history, observation.Round, budget.Replan);
        }
        else (_solver, _unsearched) = (solver, seed);
        return true;
    }

    /// <summary>The decision's search: the budget's nodes more since the root moved here, and the pick then.</summary>
    private SlPlan Search(IReadOnlyList<EnemyTurns> history, Action<CombatDecisionResult>? early, CancellationToken hurry, ref long nodes)
    {
        if (_background != null)
        {
            long begun = nodes;
            var (pick, searched) = _background.Await(budget.Solve, hurry, early == null ? null : (found, since) =>
                Tell(early, found.Result with { Nodes = (int)Math.Min(begun + since, int.MaxValue), Proved = found.Proved }));
            nodes += searched;
            (_settled, _opened) = (null, null);
            return pick;
        }
        long before = _solver!.Nodes;
        if (_since.Count > 0)
        {
            _solver.Advance([.. _since], history);
            _since.Clear();
        }
        var opening = (_unsearched ?? _solver.Result(budget.Replan)).Line!;
        _unsearched = null;
        _solver.Run(budget.Solve);
        nodes += _solver.Nodes - before;
        _settled = Math.Max(0, _solver.Settled - before);
        _opened = Top(opening.Actions[0], opening);
        return _solver.Result(budget.Replan);
    }

    private void Drop()
    {
        _background?.Dispose();
        _solver?.Dispose();
        (_solver, _background, _line, _taken, _given, _unsearched) = (null, null, null, 0, 0, null);
        _since.Clear();
        lock (_afterLock) (_afterFor, _afterShown, _afterLine) = (null, null, null);
    }

    /// <summary>
    /// A solver searching on a thread of its own, which owns it: each step posted moves its root as soon as the thread
    /// is between two of its searches, and between the steps it searches on, a batch of turns at a time, until nothing
    /// is left. After each it shows its pick at the root (<see cref="Await"/>, <see cref="Watch"/>).
    /// </summary>
    private sealed class Background : IDisposable
    {
        private readonly ICombatSolver _solver;
        private readonly TakenCombat _from;
        private readonly int _replan;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        /// <summary>The thread's own: the actions from where the solver began to its root, and the record of enemy moves
        /// there, which a decision joins the round's intents to at the round's first (<see cref="MoveRecord.Witness"/>).</summary>
        private readonly List<CombatAction> _played = [];
        private readonly MoveRecord _record;
        private int _round;

        // Under the lock of _posted: the steps posted, how many the root moved by, and what the solver found there.
        private readonly List<CombatAction> _posted = [];
        private int _moved;
        /// <summary>The solver's nodes when the root last moved, and when the last pick was taken.</summary>
        private long _since, _answered;
        private long _nodes;
        private bool _done;
        private SlPlan? _pick;
        private Exception? _failed;
        /// <summary>Who is told each pick shown where the root stands at the steps posted when it was set
        /// (<see cref="Watch"/>), and how many steps those were.</summary>
        private Action<SlPlan, long>? _watcher;
        private int _watchAt;

        /// <param name="round">The round of the observation the solver began at, whose intents <paramref name="history"/> has
        /// as that decision got it.</param>
        public Background(ICombatSolver solver, TakenCombat from, IReadOnlyList<EnemyTurns> history, int round, int replan)
        {
            (_solver, _from, _record, _round, _replan) = (solver, from, new MoveRecord(history), round, replan);
            _thread = new Thread(Search) { IsBackground = true, Name = "sts2ai solver" };
            _thread.Start();
        }

        public void Post(CombatAction step)
        {
            lock (_posted)
            {
                _posted.Add(step);
                Monitor.PulseAll(_posted);
            }
        }

        /// <summary>From now on <paramref name="watcher"/> gets each pick the solver shows where the root stands at every
        /// step posted so far, with the nodes it searched since the last pick <see cref="Await"/> took: the pick there
        /// already at once, on this thread, and each after a search on the searching thread; none once another watcher, or
        /// none, is set.</summary>
        public void Watch(Action<SlPlan, long>? watcher)
        {
            (SlPlan Pick, long Nodes)? now = null;
            lock (_posted)
            {
                (_watcher, _watchAt) = (watcher, _posted.Count);
                if (watcher != null && _moved == _posted.Count && _pick != null) now = (_pick, _nodes - _answered);
            }
            if (now is { } shown) watcher!(shown.Pick, shown.Nodes);
        }

        /// <summary>Waits until the root stands at every step posted and the solver has searched at least
        /// <paramref name="nodes"/> there, or nothing is left, or <paramref name="hurry"/> is cancelled; its pick then,
        /// and the nodes it searched since the last. Meanwhile <paramref name="found"/> gets each pick it shows from the
        /// root there, with the nodes since the last, on the waiting thread.</summary>
        public (SlPlan Pick, long Nodes) Await(long nodes, CancellationToken hurry, Action<SlPlan, long>? found)
        {
            using var woken = hurry.Register(() =>
            {
                lock (_posted) Monitor.PulseAll(_posted);
            });
            SlPlan? seen = null;
            while (true)
            {
                (SlPlan Pick, long Nodes) shown;
                lock (_posted)
                {
                    while (true)
                    {
                        if (_failed != null) ExceptionDispatchInfo.Capture(_failed).Throw();
                        bool here = _moved == _posted.Count && _pick != null;
                        if (here && (_done || hurry.IsCancellationRequested || _nodes - _since >= nodes))
                        {
                            long searched = _nodes - _answered;
                            _answered = _nodes;
                            return (_pick!, searched);
                        }
                        if (here && found != null && !ReferenceEquals(_pick, seen))
                        {
                            shown = (_pick!, _nodes - _answered);
                            break;
                        }
                        Monitor.Wait(_posted);
                    }
                }
                seen = shown.Pick;
                found!(shown.Pick, shown.Nodes);
            }
        }

        private void Search()
        {
            try
            {
                while (true)
                {
                    CombatAction[] steps;
                    lock (_posted)
                    {
                        while (!_stop.IsCancellationRequested && _moved == _posted.Count && _done) Monitor.Wait(_posted);
                        if (_stop.IsCancellationRequested) return;
                        steps = [.. _posted.Skip(_moved)];
                    }
                    if (steps.Length > 0)
                    {
                        // The record goes on as the decisions there will have it.
                        for (int i = 1; i <= steps.Length; i++)
                            if (_from.Observe([.. _played, .. steps.Take(i)]) is var shown && shown.Round != _round)
                            {
                                _record.Witness(shown);
                                _round = shown.Round;
                            }
                        _played.AddRange(steps);
                        _solver.Advance(steps, _record.History, _stop.Token);
                    }
                    else _solver.Run(1, _stop.Token);
                    var pick = _solver.Result(_replan);
                    Action<SlPlan, long>? watcher;
                    long since;
                    lock (_posted)
                    {
                        if (steps.Length > 0) (_moved, _since) = (_moved + steps.Length, _solver.Nodes);
                        (_nodes, _done, _pick) = (_solver.Nodes, _solver.Left == 0, pick);
                        watcher = _moved == _watchAt && _posted.Count == _watchAt ? _watcher : null;
                        since = _nodes - _answered;
                        Monitor.PulseAll(_posted);
                    }
                    watcher?.Invoke(pick, since);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                lock (_posted)
                {
                    _failed = e;
                    Monitor.PulseAll(_posted);
                }
            }
        }

        /// <summary>Stops the search where it stands, waits for the thread to end, and lets the solver go.</summary>
        public void Dispose()
        {
            _stop.Cancel();
            lock (_posted) Monitor.PulseAll(_posted);
            _thread.Join();
            _stop.Dispose();
            _solver.Dispose();
        }
    }
}
