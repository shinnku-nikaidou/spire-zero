using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>
/// The rest of a combat searched a little at a time with no state dropped, from a root that moves along as the combat
/// goes on (<see cref="SlSolver"/>, or the engine's, plans/2026-11-18-2132-sl-search-rust.md, C): what
/// <see cref="SolverRoute"/> plays. One thread uses it at a time.
/// </summary>
public interface ICombatSolver : IDisposable
{
    /// <summary>The actions simulated so far, over every turn's search.</summary>
    long Nodes { get; }

    /// <summary>The states reached and not searched yet.</summary>
    int Left { get; }

    /// <summary>A line another search found from the root (<see cref="SlSolver.Seed"/>).</summary>
    void Seed(SlPlan plan);

    /// <summary>Searches on for at least <paramref name="nodes"/> more actions, or until nothing is left
    /// (<see cref="SlSolver.Run"/>); once <paramref name="stop"/> is cancelled it stops where it stands with an
    /// <see cref="OperationCanceledException"/>.</summary>
    void Run(long nodes, CancellationToken stop = default);

    /// <summary>The combat went on from the root by <paramref name="played"/>, to where its record of enemy moves is
    /// <paramref name="moves"/> (<see cref="SlSolver.Advance"/>).</summary>
    void Advance(IReadOnlyList<CombatAction> played, IReadOnlyList<EnemyTurns> moves, CancellationToken stop = default);

    /// <summary>The pick so far, as a plan (<see cref="SlSolver.Result"/>).</summary>
    SlPlan Result(int replan);
}

/// <summary><see cref="SlSolver"/> with its turns searched by <paramref name="after"/>: from where the solver began, after
/// the actions given.</summary>
public sealed class SearchingSolver(SlSolver solver, Func<IReadOnlyList<CombatAction>, CancellationToken, TurnSearcher> after) : ICombatSolver
{
    /// <summary>The actions from where the solver began to its root.</summary>
    private readonly List<CombatAction> _played = [];

    public long Nodes => solver.Nodes;
    public int Left => solver.Left;
    public void Seed(SlPlan plan) => solver.Seed(plan);
    public void Run(long nodes, CancellationToken stop = default) => solver.Run(after(_played, stop), nodes);

    public void Advance(IReadOnlyList<CombatAction> played, IReadOnlyList<EnemyTurns> moves, CancellationToken stop = default)
    {
        if (played.Count == 0) return;
        _played.AddRange(played);
        solver.Advance(played, moves, after(_played, stop));
    }

    public SlPlan Result(int replan) => solver.Result(replan);

    public void Dispose()
    {
    }
}
