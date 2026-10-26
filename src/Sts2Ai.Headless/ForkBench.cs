using System.Diagnostics;

namespace Sts2Ai.Headless;

/// <summary>
/// What branching costs. Restoring is start-up plus replaying the trace, so it grows with depth. A search branch
/// is restore + determinize + a rollout to the end of the combat with the placeholder policy.
/// </summary>
internal static class ForkBench
{
    public static int Run(Pump pump, Dictionary<string, string> opts)
    {
        int n = Cli.Int(opts, "n", 30);
        int seed = Cli.Int(opts, "seed", 1);
        int branches = Cli.Int(opts, "branches", 20);
        var (act, rooms, pool) = Cli.Pool(opts);
        var restores = new List<(int Depth, double Ms)>();
        var branchMs = new List<double>();
        long rootDepths = 0, rolloutSteps = 0;

        Console.WriteLine($"forkbench: {n} combats, act {act} ({rooms}: {pool.Count} encounters), {branches} branches from mid-combat, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = new Stopwatch();
        for (int i = 0; i < n; i++)
        {
            var setup = Cli.Setup(opts, pool[i % pool.Count], act, Cli.MakeSeed(seed, i));
            var snapshots = new List<CombatSnapshot>();
            var rng = new Random(seed * 1_000_003 + i);
            using (var session = CombatSession.Start(pump, setup, new RandomChoices(rng)))
                Cli.PlayOut(session, "play", rng, s => snapshots.Add(s.Snapshot()));

            // Up to 12 depths per combat, spread over the whole trace.
            int step = Math.Max(1, snapshots.Count / 12);
            for (int k = 0; k < snapshots.Count; k += step)
            {
                clock.Restart();
                using (CombatSession.Restore(pump, snapshots[k], new RandomChoices(new Random(0)))) { }
                restores.Add((Depth(snapshots[k]), clock.Elapsed.TotalMilliseconds));
            }

            var root = snapshots[snapshots.Count / 2];
            int rootDepth = Depth(root);
            for (int b = 0; b < branches; b++)
            {
                clock.Restart();
                var branchRng = new Random(b);
                using (var branch = CombatSession.Restore(pump, root, new RandomChoices(branchRng)))
                {
                    branch.Determinize((ulong)b + 1);
                    Cli.PlayOut(branch, "play", branchRng);
                    rolloutSteps += branch.ActionsExecuted - rootDepth;
                }
                branchMs.Add(clock.Elapsed.TotalMilliseconds);
                rootDepths += rootDepth;
            }
            HeadlessGame.TakeErrors();
        }

        // Least-squares fit: restore ms = start + perAction * depth.
        double meanX = restores.Average(r => r.Depth), meanY = restores.Average(r => r.Ms);
        double slope = restores.Sum(r => (r.Depth - meanX) * (r.Ms - meanY)) / Math.Max(1e-9, restores.Sum(r => (r.Depth - meanX) * (r.Depth - meanX)));
        double intercept = meanY - slope * meanX;
        Console.WriteLine($"restore (incl. dispose), {restores.Count} samples: {intercept:F2} ms + {slope:F3} ms per replayed action");
        foreach (var (lo, hi) in new[] { (0, 0), (1, 5), (6, 10), (11, 20), (21, 40), (41, int.MaxValue) })
        {
            var bucket = restores.Where(r => r.Depth >= lo && r.Depth <= hi).Select(r => r.Ms).Order().ToList();
            if (bucket.Count > 0)
                Console.WriteLine($"  depth {lo,2}..{(hi == int.MaxValue ? "" : hi.ToString()),-3} n={bucket.Count,4}  median {bucket[bucket.Count / 2]:F2} ms  p90 {bucket[(int)(bucket.Count * 0.9)]:F2} ms");
        }
        var sorted = branchMs.Order().ToList();
        double total = branchMs.Sum() / 1000;
        Console.WriteLine($"branch = restore + determinize + rollout: {branchMs.Count} branches, mean root depth {rootDepths / (double)branchMs.Count:F1}, " +
                          $"mean rollout {rolloutSteps / (double)branchMs.Count:F1} actions");
        Console.WriteLine($"  median {sorted[sorted.Count / 2]:F2} ms, p90 {sorted[(int)(sorted.Count * 0.9)]:F2} ms, {branchMs.Count / total:F0} branches/s on one core");
        return 0;
    }

    private static int Depth(CombatSnapshot snapshot) => snapshot.Trace.Count(e => e.Kind == TraceKind.Action);
}
