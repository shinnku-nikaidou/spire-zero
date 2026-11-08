using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Brains;

/// <summary>A run's brain as its host sees it: the three policies, and what the run's record names.</summary>
internal interface IRunBrain : ICombatPolicy, IChoicePolicy, IMacroPolicy
{
    string Champion { get; }

    /// <summary>How many decisions each brain made.</summary>
    IReadOnlyDictionary<string, int> Brains { get; }

    /// <summary>Harness's decision log, when Harness is in this process and logging.</summary>
    IReadOnlyList<DecisionEntry> Log { get; }

    /// <summary>Harness's <see cref="Dispatcher.Replans"/>, when Harness is in this process.</summary>
    int Replans { get; }

    /// <summary>SL: the host arrived at a node (docs: protocol/wire §6).</summary>
    void Checkpoint(NodeCheckpoint checkpoint);

    /// <summary>SL: a decision taken in the current node, sent before the next decision.</summary>
    void Act(NodeAction action);

    /// <summary>Once, when the run is over; <paramref name="failure"/> if it failed.</summary>
    void End(RunEnd end, string? failure);
}

/// <summary>Harness in this process.</summary>
internal sealed class LocalBrain(Dispatcher harness) : IRunBrain
{
    public string Champion => harness.Champion;
    public IReadOnlyDictionary<string, int> Brains => harness.Brains;
    public IReadOnlyList<DecisionEntry> Log => harness.Log;
    public int Replans => harness.Replans;
    public CombatAction Choose(CombatDecision decision) => harness.Choose(decision);
    public IReadOnlyList<int> Choose(ChoiceDecision decision) => harness.Choose(decision);
    public string Choose(MacroDecision decision) => harness.Choose(decision);
    public void Checkpoint(NodeCheckpoint checkpoint) => harness.Checkpoint(checkpoint);
    public void Act(NodeAction action) => harness.Act(action);
    public void End(RunEnd end, string? failure) { }
}

/// <summary>
/// SL (docs: environments/information §7): before each decision, tells the brain what it has not heard of the current node: its
/// checkpoint on arrival, then the decisions taken in it since, card prompts included. <see cref="Session"/> is the run,
/// set once it has started.
/// </summary>
internal sealed class NodeExport(IRunBrain brain) : IRunBrain
{
    private int _node = -1;
    private int _start = -1;
    private int _sent;

    public RunSession? Session { get; set; }

    public string Champion => brain.Champion;
    public IReadOnlyDictionary<string, int> Brains => brain.Brains;
    public IReadOnlyList<DecisionEntry> Log => brain.Log;
    public int Replans => brain.Replans;

    public CombatAction Choose(CombatDecision decision)
    {
        Flush();
        return brain.Choose(decision);
    }

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        Flush();
        return brain.Choose(decision);
    }

    public string Choose(MacroDecision decision)
    {
        Flush();
        return brain.Choose(decision);
    }

    public void Checkpoint(NodeCheckpoint checkpoint) => brain.Checkpoint(checkpoint);
    public void Act(NodeAction action) => brain.Act(action);
    public void End(RunEnd end, string? failure) => brain.End(end, failure);

    /// <summary>A node's decisions begin at a new place in the run's trace, as each node has at least its travel.</summary>
    private void Flush()
    {
        var session = Session ?? throw new InvalidOperationException("a decision before the run started");
        var clone = session.Clone();
        if (session.NodeStart != _start)
        {
            (_node, _start, _sent) = (_node + 1, session.NodeStart, 0);
            brain.Checkpoint(new NodeCheckpoint(_node, clone.Save, clone.AutoWin));
        }
        for (; _sent < clone.Trace.Count; _sent++) brain.Act(new NodeAction(_node, _sent, clone.Trace[_sent]));
    }
}

/// <summary>
/// A run's brain at the other end of the wire (docs: protocol/wire): hello when the run starts, each decision with its
/// observation, and the recommendation as the answer. An action is reported executed when the game reaches the next
/// decision or the end of the run; run_end follows.
/// </summary>
internal sealed class WireBrain : IRunBrain
{
    private readonly WireStream _wire;
    private readonly Dictionary<string, int> _brains = [];
    private long _next;
    private (long Id, string Action)? _pending;

    private WireBrain(WireStream wire, string champion)
    {
        _wire = wire;
        Champion = champion;
    }

    public static WireBrain Start(WireStream wire, Hello hello)
    {
        wire.Send(hello);
        return new WireBrain(wire, wire.Expect<Ready>().Champion);
    }

    public string Champion { get; }
    public IReadOnlyDictionary<string, int> Brains => _brains;
    public IReadOnlyList<DecisionEntry> Log => [];
    public int Replans => 0;

    public CombatAction Choose(CombatDecision decision) => CombatAction.Parse(Ask(Surfaces.Combat,
        decision.RunObservation ?? throw new InvalidOperationException("a combat decision outside a run"), [.. decision.Legal.Select(a => a.ToString())]));

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        var action = Ask(Surfaces.CardChoice, decision.Observation ?? throw new InvalidOperationException("a card prompt outside a run"), []);
        if (!action.StartsWith("pick:")) throw new InvalidDataException($"{action} does not answer a card prompt");
        return [.. action["pick:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
    }

    public string Choose(MacroDecision decision) => Ask(decision.Surface, decision.Observation, decision.Legal);

    public void Checkpoint(NodeCheckpoint checkpoint)
    {
        Report(null);
        _wire.Send(checkpoint);
    }

    public void Act(NodeAction action)
    {
        Report(null);
        _wire.Send(action);
    }

    public void End(RunEnd end, string? failure)
    {
        Report(failure);
        _wire.Send(end);
    }

    private string Ask(string surface, Observation obs, IReadOnlyList<string> legal)
    {
        Report(null);
        long id = _next++;
        string hash = DecisionRequest.HashOf(obs);
        _wire.Send(new DecisionRequest(id, hash, surface, obs, legal));
        var answer = _wire.Expect<Recommendation>();
        if (answer.DecisionId != id || answer.ObsHash != hash)
            throw new InvalidDataException($"a recommendation for decision {answer.DecisionId} ({answer.ObsHash}), not {id} ({hash})");
        _brains[answer.Brain] = _brains.GetValueOrDefault(answer.Brain) + 1;
        _pending = (id, answer.Action);
        return answer.Action;
    }

    /// <summary>The previous decision's action has run: the game went on to the next decision, or to the end.</summary>
    private void Report(string? failure)
    {
        if (_pending is not { } pending) return;
        _wire.Send(new Executed(pending.Id, pending.Action, failure == null, failure));
        _pending = null;
    }
}

/// <summary>A host's connection to a brain listening on a Unix socket.</summary>
internal sealed class BrainConnection : IDisposable
{
    private readonly NetworkStream _stream;

    public BrainConnection(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        _stream = new NetworkStream(socket, ownsSocket: true);
        Wire = new WireStream(_stream);
    }

    public WireStream Wire { get; }

    /// <summary>Runs <paramref name="server"/> for one connection on a background thread, behind an abstract socket, and connects to it.</summary>
    public static BrainConnection InProcess(BrainServer server)
    {
        var path = $"\0sts2ai-brain-{Environment.ProcessId}";
        var listener = BrainServer.Bind(path);
        new Thread(() =>
        {
            using (listener)
            {
                using var socket = listener.Accept();
                try
                {
                    server.Serve(socket);
                }
                catch (Exception e)
                {
                    Logs.Sim.LogError("the brain in this process stopped: {Type}: {Message}", e.GetType().Name, e.Message);
                }
            }
        }) { IsBackground = true, Name = "brain" }.Start();
        return new BrainConnection(path);
    }

    public void Dispose() => _stream.Dispose();
}
