using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>
/// The rest of a combat searched a little at a time with no state dropped, from a root that moves along as the combat
/// goes on: the engine's solver (engine/crates/sts2ai-engine/src/search/solver.rs, plans/2026-11-18-2132-sl-search-rust.md),
/// what <see cref="SolverRoute"/> plays. One thread uses it at a time.
/// </summary>
public interface ICombatSolver : IDisposable
{
    /// <summary>The actions simulated so far, over every turn's search.</summary>
    long Nodes { get; }

    /// <summary>The states reached and not searched yet.</summary>
    int Left { get; }

    /// <summary>The actions simulated when the pick's first action last changed: a record of how soon an answer
    /// settles (plans/2026-11-20-1231-silent-shiv-poison.md, 2.4), which nothing the search does reads.</summary>
    long Settled { get; }

    /// <summary>A line another search found from the root: the beam's plan.</summary>
    void Seed(SlPlan plan);

    /// <summary>Searches on for at least <paramref name="nodes"/> more actions, or until nothing is left; once
    /// <paramref name="stop"/> is cancelled it stops where it stands with an <see cref="OperationCanceledException"/>.</summary>
    void Run(long nodes, CancellationToken stop = default);

    /// <summary>The combat went on from the root by <paramref name="played"/>, to where its record of enemy moves is
    /// <paramref name="moves"/>.</summary>
    void Advance(IReadOnlyList<CombatAction> played, IReadOnlyList<EnemyTurns> moves, CancellationToken stop = default);

    /// <summary>The pick so far, as a plan.</summary>
    SlPlan Result(int replan);
}
