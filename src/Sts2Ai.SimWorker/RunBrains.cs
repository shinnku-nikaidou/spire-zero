using System.Net.Sockets;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>A run's brain as its host sees it: the three policies, and what the run's record names.</summary>
internal interface IRunBrain : ICombatPolicy, IChoicePolicy, IMacroPolicy
{
    string Champion { get; }

    /// <summary>How many decisions each brain made.</summary>
    IReadOnlyDictionary<string, int> Brains { get; }

    /// <summary>Harness's decision log, when Harness is in this process and logging.</summary>
    IReadOnlyList<DecisionEntry> Log { get; }

    /// <summary>Once, when the run is over; <paramref name="failure"/> if it failed.</summary>
    void End(RunEnd end, string? failure);
}

/// <summary>Harness in this process.</summary>
internal sealed class LocalBrain(Dispatcher harness) : IRunBrain
{
    public string Champion => harness.Champion;
    public IReadOnlyDictionary<string, int> Brains => harness.Brains;
    public IReadOnlyList<DecisionEntry> Log => harness.Log;
    public CombatAction Choose(CombatDecision decision) => harness.Choose(decision);
    public IReadOnlyList<int> Choose(ChoiceDecision decision) => harness.Choose(decision);
    public string Choose(MacroDecision decision) => harness.Choose(decision);
    public void End(RunEnd end, string? failure) { }
}

/// <summary>
/// A run's brain at the other end of the wire (docs: protocol §2): hello when the run starts, each decision with its
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

    public CombatAction Choose(CombatDecision decision) => CombatAction.Parse(Ask(Surfaces.Combat,
        decision.RunObservation ?? throw new InvalidOperationException("a combat decision outside a run"), decision.Legal.Select(a => a.ToString()).ToList()));

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        var action = Ask(Surfaces.CardChoice, decision.Observation ?? throw new InvalidOperationException("a card prompt outside a run"), []);
        if (!action.StartsWith("pick:")) throw new InvalidDataException($"{action} does not answer a card prompt");
        return action["pick:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
    }

    public string Choose(MacroDecision decision) => Ask(decision.Surface, decision.Observation, decision.Legal);

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
                    Console.Error.WriteLine($"brain: {e.GetType().Name}: {e.Message}");
                }
            }
        }) { IsBackground = true, Name = "brain" }.Start();
        return new BrainConnection(path);
    }

    public void Dispose() => _stream.Dispose();
}
