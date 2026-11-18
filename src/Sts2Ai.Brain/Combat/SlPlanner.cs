using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>The SL planner's answer, and how its search went. A losing line's plan stops at the end of its
/// <see cref="Budget.Replan"/>th turn, unless the loss is proved.</summary>
/// <param name="Value">The value of the line's end: the leaf at the end of the combat, or at the last state searched
/// when the line stops before it.</param>
/// <param name="End">How the line ends: won, lost, or still in progress where the search stopped.</param>
/// <param name="Nodes">The actions simulated, over all the turns' searches.</param>
/// <param name="Turns">How many turns the beams searched, the dive's included.</param>
/// <param name="Proved">Whether the line is the best there is: the full beam left nothing out, with no turn's search out
/// of nodes or keeping fewer states than it found, no state dropped from the beam, and every line at the end of the
/// combat.</param>
/// <param name="Line">The whole line picked, from the decision, past the turns its plan stops at.</param>
/// <param name="From">For a losing line, the value of the turn start it died from, which the pick compares losses by.</param>
public sealed record SlPlan(CombatDecisionResult Result, double Value, CombatStatus End, int Nodes, int Turns, bool Proved,
    TurnEnd? Line = null, double From = 0);

/// <summary>
/// The SL planner as a combat route plays it, one per run: a plan is followed step by step with a
/// <see cref="TurnPlan"/>, and planned again only where it does not hold or has run out. An unproved losing plan runs out
/// after its <see cref="Budget.Replan"/> turns; after <see cref="Budget.LossPlans"/> of them in a row, the last is played
/// to its end. A combat ends with its first plan that is not an unproved loss, or with the run, so the plans in a row are
/// the combat's. Each plan has the budget of the combat's room (<see cref="Budget.For"/>).
/// </summary>
public sealed class SlRoute(Budget budget)
{
    private readonly TurnPlan _plan = new();
    private int _losses;

    /// <param name="plan">Plans the rest of the combat at the decision with the budget it is given.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<Budget, SlPlan> plan) =>
        _plan.Decide(observation, legal, () =>
        {
            if (PimcBrain.Forced(observation, legal) is { } forced) return PimcBrain.Only(forced);
            var fight = budget.For(observation.Room);
            var made = plan(fight.LossPlans > 0 && _losses + 1 >= fight.LossPlans ? fight with { Replan = 0 } : fight);
            _losses = made.End == CombatStatus.Lost && !made.Proved ? _losses + 1 : 0;
            return made.Result;
        });
}

/// <summary>
/// The SL planner's numbers as the C# side names them. The planner itself, the beam over turns, the solver and every
/// turn's search, is in the engine (engine/crates/sts2ai-engine/src/search/, with its reasons;
/// plans/2026-11-18-2132-sl-search-rust.md), and <see cref="PlanOf"/> makes the plan of what it found.
/// </summary>
public static class SlPlanner
{
    /// <summary>
    /// The nodes of a turn's search with no limit (<see cref="Budget.NodesPerWorld"/>): every
    /// state of the turn is tried, every answer of every prompt, and every end kept; nothing is cut. On the turns measured
    /// (plans/2026-11-18-1412-max-solver.md), a whole turn took 1,158 to 72,512 nodes, where a budget of 300 reached 19
    /// of 226 ways to play one.
    /// </summary>
    public const int Unlimited = int.MaxValue;

    /// <summary>
    /// The turn decay of a plan made for a player (<see cref="Hello.TurnDecay"/>, the mod's option; the user, 2026-11-16);
    /// self-play has none. A fight of a Silent with Nunchaku was planned for 35 turns where 8 won with the same HP: the
    /// slow win played one attack fewer, and V valued the run with the relic's counter at 7 above the one with it at 8 by
    /// 0.25 %. With this a turn takes 0.1 % of a line's value; 1 HP was worth 1.3 % of V there, so a line still waits
    /// about 13 turns for it. Times, not less: a loss stays 0 below every win, and the cost is the same share of V
    /// wherever V stands.
    /// </summary>
    public const double PlayerTurnDecay = 0.999;
}
