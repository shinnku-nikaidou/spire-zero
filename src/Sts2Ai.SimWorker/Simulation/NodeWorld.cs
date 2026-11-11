using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>
/// The SL world of a combat decision in a run (docs: simulator/worlds §7): the node's clone, loaded and replayed, is the real
/// combat at the decision. Each action is one more step of the clone; a restore loads the clone again with the steps
/// it keeps, since the game allows one live run per process. Card prompts inside the world are answered by
/// <see cref="FirstPicks"/>, as in the run, unless the action carries its own answer, and recorded among the steps. States are told apart by the full hidden
/// state (<see cref="HiddenState.Full"/>).
/// </summary>
internal sealed class NodeWorld(Pump pump, NodeClone root) : ICombatWorld, IDisposable
{
    private readonly List<string> _steps = [];
    private readonly NextAction _next = new();
    private readonly SearchPrompts _prompts = new(FirstPicks.Instance);
    private readonly Checkpoint.Opened _checkpoint = Checkpoint.Open(root.Save);
    private RunSession? _live;

    private RunSession Live => _live ??= RunSession.Load(pump, root with { Trace = [.. root.Trace, .. _steps] }, _prompts, _next, _next,
        opened: _checkpoint);

    /// <summary>The live combat, once the clone is loaded.</summary>
    private (CombatState State, Player Player) Combat
    {
        get
        {
            var player = Live.Player;
            return (CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat state"), player);
        }
    }

    public CombatObservation Observe()
    {
        var (state, player) = Combat;
        return GameBridge.Observe.Combat(state, player);
    }

    public RunView Run() => Live.View(previews: false);

    public ulong HiddenKey()
    {
        var (state, player) = Combat;
        return HiddenState.Full(state, player);
    }

    public IReadOnlyList<CombatAction> Legal()
    {
        if (Status != CombatStatus.InProgress) return [];
        var (state, player) = Combat;
        return CombatActions.Legal(state, player);
    }

    public CombatStatus Status =>
        Live.Player.Creature.IsDead ? CombatStatus.Lost : CombatManager.Instance.IsInProgress ? CombatStatus.InProgress : CombatStatus.Won;

    public void Execute(CombatAction action)
    {
        var live = Live;
        _next.Action = action.Plain;
        _prompts.Arm(action);
        live.Step();
        if (_next.Action != null) throw new InvalidOperationException($"no combat decision to play {action} at: {Live.Describe()}");
        Live.Settle();
        _steps.Clear();
        _steps.AddRange(Live.Trace.Skip(root.Trace.Count));
    }

    public PromptSeen? LastPrompt => _prompts.Seen;

    public WorldSnapshot Snapshot() => new([.. _steps]);

    public void Restore(WorldSnapshot snapshot)
    {
        _live?.Dispose();
        _live = null;
        _steps.Clear();
        _steps.AddRange(snapshot.Steps);
    }

    public void Dispose()
    {
        _live?.Dispose();
        _live = null;
    }

    /// <summary>Plays the action search gave, once; the world never leaves the combat, so no other screen is asked.</summary>
    private sealed class NextAction : ICombatPolicy, IMacroPolicy
    {
        public CombatAction? Action;

        public CombatAction Choose(CombatDecision decision)
        {
            var action = Action ?? throw new InvalidOperationException("a combat decision with no action from search");
            Action = null;
            return action;
        }

        public MacroAnswer Choose(MacroDecision decision) => throw new InvalidOperationException($"search left the combat for {decision.Surface}");
    }
}
