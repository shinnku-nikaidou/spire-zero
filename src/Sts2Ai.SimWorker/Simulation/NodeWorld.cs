using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>
/// The SL world of a combat decision in a run: the node's clone, loaded and replayed, is the real
/// combat at the decision. Each action is one more step of the clone; a restore loads the clone again with the steps
/// it keeps, since the game allows one live run per process. A card prompt an action raises is a state of the world, as
/// it is a decision of the run: its answer is the next action, recorded among the steps. States are told apart by the
/// full hidden state (<see cref="HiddenState.Full"/>).
/// </summary>
internal sealed class NodeWorld(Pump pump, NodeClone root) : ICombatWorld, IDisposable
{
    private readonly List<string> _steps = [];
    private readonly NextAction _next = new();
    private readonly Checkpoint.Opened _checkpoint = Checkpoint.Open(root.Save);
    private RunSession? _live;

    private RunSession Live => _live ??= RunSession.Load(pump, root with { Trace = [.. root.Trace, .. _steps] }, _next, _next, _next,
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

    public CombatObservation Observe() => Live.Combat();

    public RunView Run() => Live.View(previews: false);

    /// <summary>The combat where it stands, written out for the engine: between actions only, not at a card prompt.</summary>
    public CombatExport Export() => LiveCombat.Export(Live.Player, Run());

    public ulong HiddenKey()
    {
        var (state, player) = Combat;
        return HiddenState.Full(state, player);
    }

    public IReadOnlyList<CombatAction> Legal()
    {
        if (Status != CombatStatus.InProgress || Live.Prompt != null) return [];
        var (state, player) = Combat;
        return CombatActions.Legal(state, player);
    }

    public CombatStatus Status =>
        Live.Player.Creature.IsDead ? CombatStatus.Lost : CombatManager.Instance.IsInProgress ? CombatStatus.InProgress : CombatStatus.Won;

    public void Execute(CombatAction action)
    {
        var live = Live;
        _next.Action = action;
        live.Step();
        if (_next.Action != null) throw new InvalidOperationException($"no decision of the combat to take {action} at: {Live.Describe()}");
        Live.Settle();
        _steps.Clear();
        _steps.AddRange(Live.Trace.Skip(root.Trace.Count));
    }

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

    /// <summary>Plays the action search gave, once: a play at a combat decision, an answer at a card prompt. The world
    /// never leaves the combat, so no other screen is asked.</summary>
    private sealed class NextAction : ICombatPolicy, IChoicePolicy, IMacroPolicy
    {
        public CombatAction? Action;

        private CombatAction Take(bool answer)
        {
            var action = Action ?? throw new InvalidOperationException("a decision of the combat with no action from search");
            if ((action.Kind == CombatActionKind.Pick) != answer)
                throw new InvalidOperationException(answer ? $"search gave {action} where a card prompt waits" : $"search gave {action} where no card prompt waits");
            Action = null;
            return action;
        }

        public CombatAction Choose(CombatDecision decision) => Take(answer: false);

        public ChoiceAnswer Choose(ChoiceDecision decision) => new([.. Take(answer: true).Picks]);

        public MacroAnswer Choose(MacroDecision decision) => throw new InvalidOperationException($"search left the combat for {decision.Surface}");
    }
}
