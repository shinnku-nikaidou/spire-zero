using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>
/// The SL world of a decision, inside this process: the real combat itself, replayed exactly, which tells states apart
/// by the full hidden state (<see cref="HiddenState.Full"/>); the lockstep check's own second world. The game allows one
/// live combat per process, so a world keeps its own trace and, when it is used while something else is live, rebuilds
/// itself by replay. (NoSL's worlds are drawn in the engine, from the real combat: Harness's EnginePimc.)
/// A card prompt an action raises is a state of a world: its answer is the next action. Only the prompts asked while a
/// world's combat starts get <see cref="FirstPicks"/>: they come before the state the world begins at.
/// </summary>
internal sealed class SimWorlds : IWorldSource, IDisposable
{
    private readonly Pump _pump;
    private readonly CombatSnapshot _root;
    /// <summary>The run around the combat, for the leaves.</summary>
    private readonly RunView _run;
    private CombatSession? _live;
    private World? _owner;

    private SimWorlds(Pump pump, CombatSnapshot root, RunView run)
    {
        _pump = pump;
        _root = root;
        _run = run;
    }

    /// <param name="run">The run around the combat, for the leaves (<see cref="World.Run"/>).</param>
    public static SimWorlds SL(Pump pump, CombatSnapshot real, RunView run) => new(pump, real, run);

    public PlayEnvironment Environment => PlayEnvironment.SL;

    public IReadOnlyList<ICombatWorld> Worlds(int k, ulong searchSeed) => [new World(this, [])];

    public void Dispose()
    {
        _live?.Dispose();
        _live = null;
        _owner = null;
    }

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

        /// <summary>The run around the combat with the combat's relics, potions, HP, max HP and gold as they stand.</summary>
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

        public ulong HiddenKey() => HiddenState.Full(CombatSession.State, Live.Player);

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
