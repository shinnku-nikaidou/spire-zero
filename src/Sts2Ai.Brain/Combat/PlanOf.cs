using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>The plan of what the engine's beam or solver found (plans/2026-11-18-2132-sl-search-rust.md).</summary>
public static class PlanOf
{
    /// <summary>
    /// The plan of the line picked, with the best line after each first action as the candidates, the pick's first. A
    /// loss the search may have been wrong about (not <paramref name="proved"/>) is played <paramref name="replan"/> turns
    /// at a time; a proved one has nothing better.
    /// </summary>
    /// <param name="from">For a losing pick, the value of the turn start it died from.</param>
    /// <param name="byFirst">The best line after each first action, in the order the first actions were first found.</param>
    public static SlPlan Found(TurnEnd pick, double from, IReadOnlyList<TurnEnd> byFirst, bool proved, int replan, int nodes, int turns)
    {
        var candidates = byFirst.OrderByDescending(line => line.Actions[0] == pick.Actions[0]).ThenByDescending(line => line.Value)
            .Select(line => new Candidate(line.Actions[0].ToString(), line.Value, 0, 1, [Math.Round(line.Value, 4)],
                Math.Round(line.Win, 4), Math.Round(line.Hp, 1))).ToList();
        int played = pick.Actions.Count;
        if (pick.Status == CombatStatus.Lost && !proved && replan > 0)
            for (int i = 0, ends = 0; i < pick.Actions.Count; i++)
                if (pick.Actions[i].Kind == CombatActionKind.EndTurn && ++ends == replan)
                {
                    played = i + 1;
                    break;
                }
        var result = new CombatDecisionResult(pick.Actions[0], [.. pick.Actions.Take(played).Skip(1)], candidates,
            [.. pick.Hashes.Take(played).Skip(1)], Nodes: nodes, Turns: turns);
        return new(result, pick.Value, pick.Status, nodes, turns, proved, pick, pick.Status == CombatStatus.Lost ? from : 0);
    }
}
