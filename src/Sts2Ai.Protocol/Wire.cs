using System.Text;
using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

/// <summary>
/// A message between a host (the mod in the real game, or a worker hosting a run) and the brain,
/// one JSON line each, with its type and the protocol version. A connection carries runs one after another, each from
/// hello to run_end.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Ready), "ready")]
[JsonDerivedType(typeof(DecisionRequest), "decision")]
[JsonDerivedType(typeof(Recommendation), "recommendation")]
[JsonDerivedType(typeof(Executed), "executed")]
[JsonDerivedType(typeof(Cancel), "cancel")]
[JsonDerivedType(typeof(RunEnd), "run_end")]
[JsonDerivedType(typeof(WireError), "error")]
[JsonDerivedType(typeof(NodeCheckpoint), "node_checkpoint")]
[JsonDerivedType(typeof(NodeAction), "node_action")]
public abstract record WireMessage
{
    /// <summary>Bumped on any breaking change.</summary>
    public const int Version = 2;

    [JsonPropertyOrder(-1)]
    public int V { get; init; } = Version;
}

/// <summary>Host → brain, at the start of a run.</summary>
/// <param name="Env">"NoSL" or "SL", fixed for the run.</param>
/// <param name="Role">"mod" or "worker".</param>
/// <param name="Effort">The SL planner's effort the host asks for, fixed for the run: "low", "medium", "high" or
/// "max"; null for the brain's own.</param>
public sealed record Hello(string GameVersion, string Env, string Role, RunHeader Run, string? Effort = null) : WireMessage;

/// <param name="RunId">The run's id (<see cref="IdOf"/>).</param>
public sealed record RunHeader(string RunId, string Character, int Ascension)
{
    /// <summary>A run's id: a hash of its seed, so in NoSL the seed never leaves the host; an SL checkpoint carries it.</summary>
    public static string IdOf(string seed) => ProtocolJson.Hash(seed).ToString("x16");
}

/// <summary>Brain → host, the answer to hello: the run is accepted, and which champion plays it.</summary>
public sealed record Ready(string Champion) : WireMessage;

/// <summary>
/// Host → brain, a decision point. <see cref="Legal"/> is empty for a card prompt: its answer is "pick:i,j,…", canonical
/// indexes the prompt accepts.
/// </summary>
/// <param name="DecisionId">From 0, increasing within the run.</param>
/// <param name="ObsHash"><see cref="ProtocolJson.Hash"/> of <see cref="Obs"/>, in hex.</param>
/// <param name="Export">SL only, at a combat decision between actions (not at a card prompt): the live combat as the
/// bridge's <c>EngineExport</c> wrote it, in base64, for the engine to take in. It holds the hidden state, so a NoSL
/// host never sends it.</param>
public sealed record DecisionRequest(long DecisionId, string ObsHash, string Surface, Observation Obs, IReadOnlyList<string> Legal, string? Export = null)
    : WireMessage
{
    public static string HashOf(Observation obs) => ProtocolJson.Hash(obs).ToString("x16");
}

/// <summary>Brain → host, the answer to a decision.</summary>
/// <param name="Brain">The brain that decided, from the champion's routing table.</param>
/// <param name="Plan">What the brain planned after <paramref name="Action"/>: the rest of the turn from a searching
/// combat brain, the rest of the act's route from the macro brain on the map.</param>
/// <param name="Candidates">The first actions it scored, best first.</param>
public sealed record Recommendation(long DecisionId, string ObsHash, string Action, string Brain,
    IReadOnlyList<string>? Plan = null, IReadOnlyList<Candidate>? Candidates = null) : WireMessage;

/// <summary>Host → brain: the action taken for a decision, and whether it worked.</summary>
public sealed record Executed(long DecisionId, string Action, bool Ok, string? Error = null) : WireMessage;

/// <summary>Host → brain: the state moved on, so the brain drops that decision.</summary>
public sealed record Cancel(long DecisionId) : WireMessage;

/// <summary>Host → brain, at the end of a run.</summary>
/// <param name="Reason">"won", "lost", "truncated" (the decision cap), "failed" or "aborted".</param>
public sealed record RunEnd(bool Won, int Floor, string Reason) : WireMessage;

/// <summary>Brain → host, instead of an answer: the brain could not take the run or answer the decision, so the host ends the run.</summary>
public sealed record WireError(string Message) : WireMessage;

/// <summary>
/// Host → brain, SL only: the node's checkpoint on arrival, before the node's first decision: the game's
/// save and the relic fields it drops, opaque outside the simulator. Nodes are numbered from 0 in
/// the run. <see cref="AutoWin"/> is the run's rule, which the node's clones keep (<see cref="NodeClone.AutoWin"/>); null
/// in the normal game, and always from the real game.
/// </summary>
public sealed record NodeCheckpoint(int NodeIndex, string Checkpoint, IReadOnlyList<int>? AutoWin = null) : WireMessage;

/// <summary>
/// Host → brain, SL only: a decision taken in the node, numbered by <see cref="Seq"/> from 0, as "surface action" (a
/// card prompt's picks as the game's own indexes). Sent before the next decision.
/// </summary>
public sealed record NodeAction(int NodeIndex, int Seq, string Action) : WireMessage;


/// <summary>One end of a connection: messages as JSON lines, read from <paramref name="input"/> and written to <paramref name="output"/>.</summary>
public sealed class WireStream(Stream input, Stream output)
{
    private readonly StreamReader _reader = new(input, new UTF8Encoding(false));
    private readonly StreamWriter _writer = new(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

    /// <summary>Both ways on one stream, such as a socket's.</summary>
    public WireStream(Stream stream) : this(stream, stream) { }

    public void Send(WireMessage message) => _writer.WriteLine(ProtocolJson.Serialize(message));

    /// <summary>The next message; null once the other end has closed.</summary>
    public WireMessage? Receive()
    {
        if (_reader.ReadLine() is not { } line) return null;
        var message = ProtocolJson.Deserialize<WireMessage>(line);
        if (message.V != WireMessage.Version) throw new InvalidDataException($"protocol version {message.V}, not {WireMessage.Version}");
        return message;
    }

    /// <summary>The next message, which must be a <typeparamref name="T"/>; an error from the brain is thrown.</summary>
    public T Expect<T>() where T : WireMessage => Receive() switch
    {
        T message => message,
        WireError error => throw new InvalidOperationException($"brain: {error.Message}"),
        null => throw new IOException("the other end closed the connection"),
        var other => throw new InvalidDataException($"expected {typeof(T).Name}, got {other.GetType().Name}"),
    };
}
