using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

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
/// </summary>
public sealed class SolverRoute(Budget budget)
{
    private SlSolver? _solver;
    /// <summary>Turn searches from where the solver began, after the actions given.</summary>
    private Func<IReadOnlyList<CombatAction>, TurnSearcher>? _from;
    /// <summary>The actions from where the solver began to its root, and the combat's since, which the root moves by
    /// at the next decision searched.</summary>
    private readonly List<CombatAction> _played = [], _since = [];
    /// <summary>The pick from the root, and how many of its steps the combat took since.</summary>
    private TurnEnd? _line;
    private int _taken;

    /// <summary>How many solvers the route began: one a combat while the combat goes the picks' way.</summary>
    public int Begun { get; private set; }

    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    /// <param name="start">Takes the combat in where the decision stands: turn searches from there after the actions
    /// given; null where it cannot.</param>
    /// <param name="beam">The beam's decision, where no solver can begin.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IReadOnlyList<EnemyTurns> history,
        Func<Func<IReadOnlyList<CombatAction>, TurnSearcher>?> start, Func<CombatDecisionResult> beam)
    {
        ulong hash = ProtocolJson.Hash(observation);
        // Past the line's last step the combat goes on where it ends; the root's search there shows whether it does.
        bool along = _line is { } line && (_taken < line.Actions.Count
            ? line.Hashes[_taken] == hash && observation.Takes(line.Actions[_taken], legal)
            : line.Status == CombatStatus.InProgress);
        if (PimcBrain.Forced(observation, legal) is { } forced)
        {
            if (along)
            {
                _since.Add(_taken < _line!.Actions.Count ? _line.Actions[_taken] : forced);
                _taken++;
            }
            else Drop();
            return PimcBrain.Only(forced);
        }
        long nodes = 0;
        if (along && _since.Count > 0)
        {
            long before = _solver!.Nodes;
            _played.AddRange(_since);
            _solver.Advance([.. _since], history, _from!(_played));
            _since.Clear();
            nodes += _solver.Nodes - before;
        }
        else if (!along && !Begin(observation, history, start, ref nodes))
        {
            Drop();
            return beam();
        }
        var plan = Search(ref nodes);
        // The engine's combat is not this one: it played a step otherwise than the game.
        if (plan.Line!.Hashes[0] != hash)
        {
            if (!Begin(observation, history, start, ref nodes))
            {
                Drop();
                return beam();
            }
            plan = Search(ref nodes);
            if (plan.Line!.Hashes[0] != hash)
                throw new InvalidOperationException("the combat taken in is not the combat the decision shows");
        }
        (_line, _taken) = (plan.Line, 1);
        _since.Add(plan.Line.Actions[0]);
        return plan.Result with { Nodes = (int)Math.Min(nodes, int.MaxValue) };
    }

    /// <summary>A new solver where the decision stands, from the beam's plan there; false where the combat cannot be
    /// taken in.</summary>
    private bool Begin(CombatObservation observation, IReadOnlyList<EnemyTurns> history, Func<Func<IReadOnlyList<CombatAction>, TurnSearcher>?> start,
        ref long nodes)
    {
        if (start() is not { } from) return false;
        Drop();
        _from = from;
        _solver = new SlSolver(history, budget.TurnDecay);
        var seed = SlPlanner.Search(budget.For(observation.Room) with { Solve = 0 }, history, from([]));
        _solver.Seed(seed);
        nodes += seed.Nodes;
        Begun++;
        return true;
    }

    /// <summary>The decision's search: the budget's nodes more, and the pick then.</summary>
    private SlPlan Search(ref long nodes)
    {
        long before = _solver!.Nodes;
        _solver.Run(_from!(_played), budget.Solve);
        nodes += _solver.Nodes - before;
        return _solver.Result(budget.Replan);
    }

    private void Drop()
    {
        (_solver, _from, _line, _taken) = (null, null, null, 0);
        _played.Clear();
        _since.Clear();
    }
}
