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
/// Card prompts inside a world are answered at random, from a generator seeded by the world's seed and its depth,
/// so the same search gives the same answers.
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

    public static SimWorlds NoSL(Pump pump, CombatObservation observation, string buildSeed) =>
        new(pump, PlayEnvironment.NoSL, new CombatSnapshot(CombatBuilder.Setup(observation, buildSeed), []));

    public static SimWorlds SL(Pump pump, CombatSnapshot real) => new(pump, PlayEnvironment.SL, real);

    public PlayEnvironment Environment { get; }

    public IReadOnlyList<ICombatWorld> Worlds(int k, ulong searchSeed) => Environment == PlayEnvironment.SL
        ? [new World(this, [], searchSeed)]
        : Enumerable.Range(0, k).Select(i =>
        {
            ulong seed = Mix(searchSeed, (ulong)i);
            return (ICombatWorld)new World(this, [new TraceEvent(TraceKind.Determinize, Seed: seed)], seed);
        }).ToList();

    public void Dispose()
    {
        _live?.Dispose();
        _live = null;
        _owner = null;
    }

    private static ulong Mix(ulong a, ulong b) => CombatSession.StableHash($"{a}:{b}");

    private sealed class World(SimWorlds source, List<TraceEvent> trace, ulong seed) : ICombatWorld
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
                var choices = new RandomChoices(new Random((int)Mix(seed, (ulong)trace.Count)));
                source._live = CombatSession.Restore(source._pump, source._root with { Trace = [.. source._root.Trace, .. trace] }, choices);
                source._owner = this;
                _current = true;
                return source._live;
            }
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
            var live = Live;
            int before = live.Trace.Count;
            live.Execute(action);
            trace.AddRange(live.Trace.Skip(before));
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
