using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>The plan of what a beam over turns found elsewhere (the engine's, plans/2026-11-18-2132-sl-search-rust.md,
/// C), as <see cref="SlPlanner.Search"/> makes it of its own.</summary>
public static class PlanOf
{
    /// <param name="byFirst">The best line after each first action, in the order the first actions were first found.</param>
    public static SlPlan Found(TurnEnd pick, double from, IReadOnlyList<TurnEnd> byFirst, bool proved, int replan, int nodes, int turns) =>
        SlPlanner.Picked(pick, from, byFirst.ToDictionary(line => line.Actions[0]), proved, replan, nodes, turns);
}
