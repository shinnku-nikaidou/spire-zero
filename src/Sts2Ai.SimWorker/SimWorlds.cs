using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// The worlds for one decision, inside this process (docs: protocol §7). The game allows one live combat per
/// process, so the worlds take turns: each keeps its own trace and, when it is used while something else is live,
/// rebuilds itself by replay.
///
/// NoSL worlds start from a combat built to show the observation (<see cref="CombatBuilder"/>), never from the real
/// combat, and each is determinized with its own seed. The SL world is the real combat itself, replayed exactly.
/// Card prompts inside a world are answered by <see cref="PromptBrain"/>, the run's own prompt brain, so search
/// expects the picks the run will make.
/// </summary>
internal sealed class SimWorlds : IWorldSource, IDisposable
{
    private readonly Pump _pump;
    private readonly CombatSnapshot _root;
    private CombatSession? _live;
    private World? _owner;

    private SimWorlds(Pump pump, PlayEnvironment environment, CombatSnapshot root)
    {
        _pump = pump;
        Environment = environment;
        _root = root;
    }

    /// <exception cref="InvalidOperationException">The combat built from the observation does not show it exactly: search
    /// would explore some other combat (docs: harness §7).</exception>
    public static SimWorlds NoSL(Pump pump, CombatObservation observation, string buildSeed)
    {
        var root = new CombatSnapshot(CombatBuilder.Setup(observation, buildSeed), []);
        using (var built = CombatSession.Start(pump, root.Setup, PromptBrain.Instance))
            if (built.BuildDifferences.Count > 0)
                throw new InvalidOperationException($"not built as observed: {string.Join("; ", built.BuildDifferences.Take(4))}");
        return new(pump, PlayEnvironment.NoSL, root);
    }

    public static SimWorlds SL(Pump pump, CombatSnapshot real) => new(pump, PlayEnvironment.SL, real);

    public PlayEnvironment Environment { get; }

    public IReadOnlyList<ICombatWorld> Worlds(int k, ulong searchSeed) => Environment == PlayEnvironment.SL
        ? [new World(this, [])]
        : Enumerable.Range(0, k).Select(i =>
        {
            ulong seed = Mix(searchSeed, (ulong)i);
            return (ICombatWorld)new World(this, [new TraceEvent(TraceKind.Determinize, Seed: seed)]);
        }).ToList();

    public void Dispose()
    {
        _live?.Dispose();
        _live = null;
        _owner = null;
    }

    private static ulong Mix(ulong a, ulong b) => CombatSession.StableHash($"{a}:{b}");

    private sealed class World(SimWorlds source, List<TraceEvent> trace) : ICombatWorld
    {
        /// <summary>The live combat is this world at its trace; false after a restore, until the next use.</summary>
        private bool _current;

        private CombatSession Live
        {
            get
            {
                if (source._owner == this && _current) return source._live!;
                source._live?.Dispose();
                source._live = null;
                source._live = CombatSession.Restore(source._pump, source._root with { Trace = [.. source._root.Trace, .. trace] }, PromptBrain.Instance);
                source._owner = this;
                _current = true;
                Sync();
                return source._live;
            }
        }

        /// <summary>
        /// Takes the trace from the live combat, which also records the prompts asked while it starts (a relic that
        /// asks at the start of each turn). Replay answers prompts from the recorded picks in order, so a trace
        /// without those would hand a later prompt's pick to them.
        /// </summary>
        private void Sync()
        {
            trace.Clear();
            trace.AddRange(source._live!.Trace.Skip(source._root.Trace.Count));
        }

        public CombatObservation Observe() => Live.Observation();

        public IReadOnlyList<CombatAction> Legal() => Live.IsOver ? [] : Live.LegalActions();

        public CombatStatus Status => Live.Outcome switch
        {
            CombatOutcome.InProgress => CombatStatus.InProgress,
            CombatOutcome.Loss => CombatStatus.Lost,
            _ => CombatStatus.Won,
        };

        public void Execute(CombatAction action)
        {
            Live.Execute(action);
            Sync();
        }

        public WorldSnapshot Snapshot() => new(trace.Select(e => e.ToString()).ToList());

        public void Restore(WorldSnapshot snapshot)
        {
            trace.Clear();
            trace.AddRange(snapshot.Steps.Select(TraceEvent.Parse));
            _current = false;
        }
    }
}
