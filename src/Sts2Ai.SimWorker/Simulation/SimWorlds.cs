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
/// Card prompts inside a world are answered by <see cref="PromptBrain"/>, the run's own prompt brain, so search
/// expects the picks the run will make, unless an action carries its own answer; an SL world may be given the prompt
/// policy of its real combat instead.
/// </summary>
internal sealed class SimWorlds : IWorldSource, IDisposable
{
    private readonly Pump _pump;
    private readonly CombatSnapshot _root;
    private readonly SearchPrompts _prompts;
    private readonly RunView _run;
    private CombatSession? _live;
    private World? _owner;

    private SimWorlds(Pump pump, PlayEnvironment environment, CombatSnapshot root, RunView run, IChoicePolicy? prompts = null)
    {
        _pump = pump;
        _prompts = new(prompts ?? PromptBrain.Instance);
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
        using (var built = CombatSession.Start(pump, root.Setup, PromptBrain.Instance))
            if (built.BuildDifferences.Count > 0)
                throw new InvalidOperationException($"not built as observed: {string.Join("; ", built.BuildDifferences.Take(4))}");
        return new(pump, PlayEnvironment.NoSL, root, run);
    }

    /// <param name="run">The run around the combat, for the leaves (<see cref="World.Run"/>).</param>
    /// <param name="prompts">Answers the card prompts, as in the real combat; null: <see cref="PromptBrain"/>.</param>
    public static SimWorlds SL(Pump pump, CombatSnapshot real, RunView run, IChoicePolicy? prompts = null) => new(pump, PlayEnvironment.SL, real, run, prompts);

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
                source._live = CombatSession.Restore(source._pump, source._root with { Trace = [.. source._root.Trace, .. trace] }, source._prompts);
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
            var live = Live;
            source._prompts.Arm(action.Pick);
            live.Execute(action.Plain);
            Sync();
        }

        public PromptSeen? LastPrompt => source._prompts.Seen;

        public WorldSnapshot Snapshot() => new([.. trace.Select(e => e.ToString())]);

        public void Restore(WorldSnapshot snapshot)
        {
            trace.Clear();
            trace.AddRange(snapshot.Steps.Select(TraceEvent.Parse));
            _current = false;
        }
    }
}
