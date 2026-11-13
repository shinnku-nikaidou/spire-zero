using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sts2Ai.Brain;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// <see cref="SlPlanner"/> with its turns searched in the engine, in threads of this process. The live combat is taken
/// in once a plan (<see cref="CombatDecision.Export"/>); each state the beam keeps is then a copy of that world with
/// the state's path applied, searched in a thread of its own, and the turns come back in the order they were asked
/// for, so a plan is the same on any number of threads. With the same budget and leaf it plans what
/// <see cref="PooledPlanner"/> plans on the node's clone, as long as the engine plays the combat as the game does.
///
/// The engine takes a combat in only where the game stands between actions. A plan rooted at a card prompt (one that
/// opens its combat, one a plan ran out at, one where a step did not hold) starts on the node's clone instead, which
/// is asked what follows each answer (<see cref="PromptRoot"/>): the search is the same one, over a world that is the
/// clone's up to where the player can play again and the engine's from there.
/// </summary>
/// <param name="leaf">Prices the lines; asked from every thread at once.</param>
/// <param name="threads">How many turns are searched at a time.</param>
public sealed class EnginePlanner(ICombatLeaf leaf, int threads)
{
    private readonly SlPlanner _planner = new(leaf);

    /// <summary>Plans the rest of the combat that <paramref name="live"/> is.</summary>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    /// <param name="imported">Told the engine's time to take the combat in, in milliseconds.</param>
    public SlPlan Plan(CombatExport live, Budget budget, IReadOnlyList<EnemyTurns> history, Action<double>? imported = null)
    {
        long started = Stopwatch.GetTimestamp();
        using var root = EngineWorld.Import(live.Bytes, live.Run);
        imported?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return Search(budget, history, root.Clone);
    }

    /// <summary>Plans the rest of the combat from the card prompt it waits on, which <paramref name="crossing"/>'s
    /// clone stands at.</summary>
    public SlPlan Plan(PromptRoot.Crossing crossing, Budget budget, IReadOnlyList<EnemyTurns> history) =>
        Search(budget, history, () => new PromptRoot(crossing));

    /// <param name="start">A world of its own at the plan's root, for one turn's search.</param>
    private SlPlan Search<TWorld>(Budget budget, IReadOnlyList<EnemyTurns> history, Func<TWorld> start) where TWorld : ICombatWorld, IDisposable =>
        SlPlanner.Search(budget, history, (jobs, nodes, keep) =>
        {
            // One world from one thread at a time: each job's world is made here, before the threads start.
            var worlds = jobs.Select(_ => start()).ToArray();
            var found = new TurnSearch[jobs.Count];
            try
            {
                Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                {
                    foreach (var action in jobs[i].Path) worlds[i].Execute(action);
                    found[i] = _planner.SearchTurn(worlds[i], jobs[i].History, nodes, keep);
                });
            }
            catch (AggregateException e)
            {
                // The first failure as it was thrown: a run's record keeps one line of it.
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            finally
            {
                foreach (var world in worlds) world.Dispose();
            }
            return found;
        });

    /// <summary>As Harness's combat route in SL, one per run, like <see cref="PooledPlanner.Route"/>.</summary>
    /// <param name="name">The brain's name in reports: plan and its leaf's name, as on the node's clone.</param>
    /// <param name="pool">The workers that play the node's clone across a card prompt.</param>
    public CombatRoute Route(Budget budget, string name, WorkerPool pool)
    {
        var route = new SlRoute(budget);
        // The search's processor time: this process's threads, and the workers' where a prompt is crossed.
        var meter = new PlanMeter(() =>
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime + pool.CpuTime();
        });
        return new(name, (decision, _, node, history) => route.Decide(decision.Observation, decision.Legal, b =>
        {
            if (decision.Observation.Prompt is { } prompt)
            {
                var clone = node ?? throw new InvalidOperationException("the SL planner has no node clone: it plays in SL only");
                using var crossing = new PromptRoot.Crossing(paths =>
                    [.. pool.Map([.. paths.Select((path, i) => (WorkerRequest)new SlPromptRequest(i, clone, path))])
                        .Select(r => r.Crossed ?? throw new InvalidDataException($"request {r.Id}: no crossing in the reply"))]);
                var plan = meter.Count(() =>
                {
                    // The prompt itself and each answer the first turn's search will try, crossed side by side.
                    crossing.Ahead([[], .. PromptAnswers.Of(prompt, b.NodesPerWorld, out bool _).Select(answer => (IReadOnlyList<CombatAction>)[answer])]);
                    return Plan(crossing, b, history);
                });
                meter.Crossed(crossing.Crossed);
                return plan;
            }
            long started = Stopwatch.GetTimestamp();
            var live = decision.Export ?? throw new InvalidOperationException("the host gives no live combat for the engine: --combat-world engine needs a host that holds the game");
            double exportMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return meter.Count(() => Plan(live, b, history, importMs => meter.Imported(live.Bytes.Length, exportMs, importMs)));
        }), budget, () => meter.Tally);
    }
}
