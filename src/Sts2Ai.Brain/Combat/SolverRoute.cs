using System.Runtime.ExceptionServices;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>The combat a solver begins on, taken in where a decision stands (<see cref="SolverRoute"/>).</summary>
/// <param name="After">Turn searches from there after the actions given. Once the token is cancelled they stop, with an
/// <see cref="OperationCanceledException"/>.</param>
/// <param name="Observe">The combat's observation from there after the actions given.</param>
public sealed record TakenCombat(Func<IReadOnlyList<CombatAction>, CancellationToken, TurnSearcher> After,
    Func<IReadOnlyList<CombatAction>, CombatObservation> Observe);

/// <summary>
/// max's combat route (plans/2026-11-18-1412-max-solver.md, 2.3–2.5): one <see cref="SlSolver"/> a combat, which
/// searches <see cref="Budget.Solve"/> more nodes at each decision, then answers with its pick (the user, 2026-11-18,
/// of the ways auto-play could wait for it: 「第二種」). The same budget gives the same decisions, so self-play and the
/// tests measure what max plays; the mod's brain may give a decision more, never less.
///
/// The solver begins where the combat stands, taken in once (<c>start</c>), from the beam's plan that the rest of the
/// budget makes there (<see cref="SlPlanner.Search"/>), so its pick is never worse than the beam's. At each decision
/// after, if the combat went the pick's way, the observation being the one its line expected before its next step, the
/// solver's root moves along it (<see cref="SlSolver.Advance"/>) and keeps what it found below. Otherwise (the player
/// moved elsewhere, a new combat began, the engine played a step otherwise) a new solver begins. A decision with one
/// thing to do is answered without search, and the root moves at the next one. Where the combat cannot be taken in
/// (a card prompt off the line) the beam decides.
///
/// In the background (<paramref name="background"/>: the mod's brain, whose game takes its time between decisions) the
/// solver searches on a thread of its own the combat long. The root moves along each step the combat is expected to
/// take as soon as the route gives it, and the solver searches on there until the next decision. That decision is
/// answered once the solver has searched at least <see cref="Budget.Solve"/> nodes since its root moved there, or at
/// once when nothing is left, so it is never searched less than without the background, and what was found while the
/// game played the step stays. <see cref="Rest"/> stops it. Meanwhile the host is told the pick so far
/// (<see cref="CombatDecision.Early"/>): the rest of the plan given at the last answer while the combat goes its way, the
/// beam's plan where a solver begins. A host that moved on from the decision (<see cref="CombatDecision.Hurry"/>) gets
/// the pick at once.
/// </summary>
public sealed class SolverRoute(Budget budget, bool background = false)
{
    private SlSolver? _solver;
    private TakenCombat? _from;
    /// <summary>The actions from where the solver began to its root, and the combat's since, which the root moves by
    /// at the next decision searched.</summary>
    private readonly List<CombatAction> _played = [], _since = [];
    /// <summary>The solver searching in the background, if it does.</summary>
    private Background? _background;
    /// <summary>The pick from the root, how many of its steps the combat took since, and how many of them the plan
    /// given with it held.</summary>
    private TurnEnd? _line;
    private int _taken, _given;

    /// <summary>How many solvers the route began: one a combat while the combat goes the picks' way.</summary>
    public int Begun { get; private set; }

    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    /// <param name="start">Takes the combat in where the decision stands; null where it cannot.</param>
    /// <param name="beam">The beam's decision, where no solver can begin.</param>
    /// <param name="early">In the background, told the pick so far before the decision's search.</param>
    /// <param name="hurry">In the background, cancelled when the host moved on: the decision's search ends there.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IReadOnlyList<EnemyTurns> history,
        Func<TakenCombat?> start, Func<CombatDecisionResult> beam, Action<CombatDecisionResult>? early = null, CancellationToken hurry = default)
    {
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
        if (along && _background != null && _taken < _given) early?.Invoke(Given());
        if (!along && !Begin(observation, history, start, early, ref nodes))
        {
            Drop();
            return beam();
        }
        var plan = Search(history, hurry, ref nodes);
        // The engine's combat is not this one: it played a step otherwise than the game.
        if (plan.Line!.Hashes[0] != hash)
        {
            if (!Begin(observation, history, start, early, ref nodes))
            {
                Drop();
                return beam();
            }
            plan = Search(history, hurry, ref nodes);
            if (plan.Line!.Hashes[0] != hash)
                throw new InvalidOperationException("the combat taken in is not the combat the decision shows");
        }
        (_line, _taken, _given) = (plan.Line, 0, plan.Result.Plan.Count + 1);
        Took(plan.Line.Actions[0]);
        return plan.Result with { Nodes = (int)Math.Min(nodes, int.MaxValue) };
    }

    /// <summary>The rest of the plan given at the last answer, from the step the combat stands at, with the line's own
    /// value: the pick so far until the decision's search says otherwise.</summary>
    private CombatDecisionResult Given()
    {
        var line = _line!;
        var first = line.Actions[_taken];
        var candidate = new Candidate(first.ToString(), line.Value, 0, 1, [Math.Round(line.Value, 4)], Math.Round(line.Win, 4), Math.Round(line.Hp, 1));
        return new(first, [.. line.Actions.Take(_given).Skip(_taken + 1)], [candidate], [.. line.Hashes.Take(_given).Skip(_taken + 1)]);
    }

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
        var solver = new SlSolver(history, budget.TurnDecay);
        var seed = SlPlanner.Search(budget.For(observation.Room) with { Solve = 0 }, history, from.After([], CancellationToken.None));
        solver.Seed(seed);
        nodes += seed.Nodes;
        Begun++;
        if (background)
        {
            early?.Invoke(seed.Result);
            _background = new Background(solver, from, history, observation.Round, budget.Replan);
        }
        else (_solver, _from) = (solver, from);
        return true;
    }

    /// <summary>The decision's search: the budget's nodes more since the root moved here, and the pick then.</summary>
    private SlPlan Search(IReadOnlyList<EnemyTurns> history, CancellationToken hurry, ref long nodes)
    {
        if (_background != null)
        {
            var (pick, searched) = _background.Await(budget.Solve, hurry);
            nodes += searched;
            return pick;
        }
        long before = _solver!.Nodes;
        if (_since.Count > 0)
        {
            _played.AddRange(_since);
            _solver.Advance([.. _since], history, _from!.After(_played, CancellationToken.None));
            _since.Clear();
        }
        _solver.Run(_from!.After(_played, CancellationToken.None), budget.Solve);
        nodes += _solver.Nodes - before;
        return _solver.Result(budget.Replan);
    }

    private void Drop()
    {
        _background?.Dispose();
        (_solver, _from, _background, _line, _taken, _given) = (null, null, null, null, 0, 0);
        _played.Clear();
        _since.Clear();
    }

    /// <summary>
    /// A solver searching on a thread of its own, which owns it: each step posted moves its root as soon as the thread
    /// is between two of its searches, and between the steps it searches on, a batch of turns at a time, until nothing
    /// is left. After each it shows its pick at the root (<see cref="Await"/>).
    /// </summary>
    private sealed class Background : IDisposable
    {
        private readonly SlSolver _solver;
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

        /// <param name="round">The round of the observation the solver began at, whose intents <paramref name="history"/> has
        /// as that decision got it.</param>
        public Background(SlSolver solver, TakenCombat from, IReadOnlyList<EnemyTurns> history, int round, int replan)
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

        /// <summary>Waits until the root stands at every step posted and the solver has searched at least
        /// <paramref name="nodes"/> there, or nothing is left, or <paramref name="hurry"/> is cancelled; its pick then,
        /// and the nodes it searched since the last.</summary>
        public (SlPlan Pick, long Nodes) Await(long nodes, CancellationToken hurry)
        {
            using var woken = hurry.Register(() =>
            {
                lock (_posted) Monitor.PulseAll(_posted);
            });
            lock (_posted)
            {
                while (_failed == null && !(_moved == _posted.Count && _pick != null && (_done || hurry.IsCancellationRequested || _nodes - _since >= nodes)))
                    Monitor.Wait(_posted);
                if (_failed != null) ExceptionDispatchInfo.Capture(_failed).Throw();
                long searched = _nodes - _answered;
                _answered = _nodes;
                return (_pick!, searched);
            }
        }

        private void Search()
        {
            try
            {
                var search = _from.After(_played, _stop.Token);
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
                        search = _from.After(_played, _stop.Token);
                        _solver.Advance(steps, _record.History, search);
                    }
                    else _solver.Run(search, 1);
                    var pick = _solver.Result(_replan);
                    lock (_posted)
                    {
                        if (steps.Length > 0) (_moved, _since) = (_moved + steps.Length, _solver.Nodes);
                        (_nodes, _done, _pick) = (_solver.Nodes, _solver.Left == 0, pick);
                        Monitor.PulseAll(_posted);
                    }
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

        /// <summary>Stops the search where it stands and waits for the thread to end.</summary>
        public void Dispose()
        {
            _stop.Cancel();
            lock (_posted) Monitor.PulseAll(_posted);
            _thread.Join();
            _stop.Dispose();
        }
    }
}
