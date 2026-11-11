using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// The node's clone at a decision outside combat (<see cref="IMacroClone"/>), its paths played in a <see cref="WorkerPool"/>
/// (docs: macro-brain/search §6): the paths are split into one request per worker, in order, each loading the clone once per
/// path (<see cref="SlMacroRequest"/>). A path's afterstate depends on the clone and the path only, so the split cannot
/// change a result.
/// </summary>
public sealed class PooledClone(WorkerPool pool, NodeClone node) : IMacroClone
{
    public IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<MacroAction>> paths)
    {
        if (paths.Count == 0) return [];
        int parts = Math.Min(paths.Count, pool.Count);
        var requests = Enumerable.Range(0, parts)
            .Select(i => (WorkerRequest)new SlMacroRequest(i, node, [.. paths.Skip(i * paths.Count / parts).Take((i + 1) * paths.Count / parts - i * paths.Count / parts)]))
            .ToList();
        var afters = pool.Map(requests).SelectMany(r => r.Afters ?? throw new InvalidDataException($"request {r.Id}: no afterstates in the reply")).ToList();
        if (afters.Count != paths.Count) throw new InvalidDataException($"{afters.Count} afterstates for {paths.Count} paths");
        return afters;
    }
}
