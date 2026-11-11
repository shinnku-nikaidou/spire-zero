using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>
/// The worlds for one decision, inside this process (docs: protocol/worlds §1). The game allows one live combat per
/// process, so the worlds take turns: each keeps its own trace and, when it is used while something else is live,
/// rebuilds itself by replay.
///
/// NoSL worlds start from a combat built to show the observation (<see cref="CombatBuilder"/>), never from the real
/// combat, and each is determinized with its own seed. The SL world is the real combat itself, replayed exactly, and
/// tells states apart by the full hidden state (<see cref="HiddenState.Full"/>).
/// A card prompt an action raises is a state of a world: its answer is the next action. Only the prompts asked while a
/// world's combat starts get <see cref="FirstPicks"/>: they come before the state the world begins at.
/// </summary>
internal sealed class SimWorlds : IWorldSource, IDisposable
{
    private readonly Pump _pump;
    private readonly CombatSnapshot _root;
    private readonly RunView _run;
    private CombatSession? _live;
    private World? _owner;

    private SimWorlds(Pump pump, PlayEnvironment environment, CombatSnapshot root, RunView run)
    {
        _pump = pump;
        Environment = environment;
        _root = root;
        _run = run;
    }

    /// <exception cref="InvalidOperationException">The combat built from the observation does not show it exactly: search
    /// would explore some other combat (docs: harness/routing §6).</exception>
    /// <param name="history">The enemies' intents in the rounds so far, for the monsters' move logs.</param>
    /// <param name="run">The run around the combat, for the leaves (<see cref="World.Run"/>).</param>
    public static SimWorlds NoSL(Pump pump, CombatObservation observation, string buildSeed, RunView run, IReadOnlyList<EnemyTurns>? history = null)
    {
        var root = new CombatSnapshot(CombatBuilder.Setup(observation, buildSeed, history), []);
        using (var built = CombatSession.Start(pump, root.Setup, FirstPicks.Instance))
            if (built.BuildDifferences.Count > 0)
                throw new InvalidOperationException($"not built as observed: {string.Join("; ", built.BuildDifferences.Take(4))}");
        return new(pump, PlayEnvironment.NoSL, root, run);
    }

    /// <param name="run">The run around the combat, for the leaves (<see cref="World.Run"/>).</param>
    public static SimWorlds SL(Pump pump, CombatSnapshot real, RunView run) => new(pump, PlayEnvironment.SL, real, run);

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
                source._live = CombatSession.Restore(source._pump, source._root with { Trace = [.. source._root.Trace, .. trace] }, FirstPicks.Instance);
                source._owner = this;
                _current = true;
                Sync();
                return source._live;
            }
        }

        /// <summary>
        /// Takes the trace from the live combat, which also records the answers given while it starts (a relic that
        /// asks at the start of each turn): a restore gives the start those again.
        /// </summary>
        private void Sync()
        {
            trace.Clear();
            trace.AddRange(source._live!.Trace.Skip(source._root.Trace.Count));
        }

        public CombatObservation Observe() => Live.Observation();

        /// <summary>
        /// The run around the combat with the combat's relics, potions, HP, max HP and gold as they stand: a combat built
        /// here has a run of its own with neither the real deck (it holds the combat's piles) nor the real map.
        /// </summary>
        public RunView Run()
        {
            var player = Live.Player;
            return source._run with
            {
                Relics = [.. player.Relics.Select(GameBridge.Observe.Relic)],
                Potions = [.. player.PotionSlots.Select(p => p?.Id.Entry)],
                Hp = player.Creature.CurrentHp,
                MaxHp = player.Creature.MaxHp,
                Gold = player.Gold,
            };
        }

        public ulong HiddenKey() => source.Environment == PlayEnvironment.SL ? HiddenState.Full(CombatSession.State, Live.Player) : HiddenState.Key(CombatSession.State, Live.Player);

        public IReadOnlyList<CombatAction> Legal() => CombatSession.IsOver ? [] : Live.LegalActions();

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

        public WorldSnapshot Snapshot() => new([.. trace.Select(e => e.ToString())]);

        public void Restore(WorldSnapshot snapshot)
        {
            trace.Clear();
            trace.AddRange(snapshot.Steps.Select(TraceEvent.Parse));
            _current = false;
        }
    }
}
