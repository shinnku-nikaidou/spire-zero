using System.Text;
using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

/// <summary>
/// A message between a host (the mod in the real game, or a worker hosting a run) and the brain (docs: protocol §2),
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
public abstract record WireMessage
{
    /// <summary>Bumped on any breaking change.</summary>
    public const int Version = 1;

    [JsonPropertyOrder(-1)]
    public int V { get; init; } = Version;
}

/// <summary>Host → brain, at the start of a run.</summary>
/// <param name="Env">"NoSL" or "SL", fixed for the run.</param>
/// <param name="Role">"mod" or "worker".</param>
public sealed record Hello(string GameVersion, string Env, string Role, RunHeader Run) : WireMessage;

/// <param name="RunId">The run's id (<see cref="IdOf"/>).</param>
public sealed record RunHeader(string RunId, string Character, int Ascension)
{
    /// <summary>A run's id: a hash of its seed, so the seed never leaves the host (docs: mod §9).</summary>
    public static string IdOf(string seed) => ProtocolJson.Hash(seed).ToString("x16");
}

/// <summary>Brain → host, the answer to hello: the run is accepted, and which champion plays it.</summary>
public sealed record Ready(string Champion) : WireMessage;

/// <summary>
/// Host → brain, a decision point. <see cref="Legal"/> is empty for a card prompt: its answer is "pick:i,j,…", canonical
/// indexes the prompt accepts (docs: protocol §3).
/// </summary>
/// <param name="DecisionId">From 0, increasing within the run.</param>
/// <param name="ObsHash"><see cref="ProtocolJson.Hash"/> of <see cref="Obs"/>, in hex.</param>
public sealed record DecisionRequest(long DecisionId, string ObsHash, string Surface, Observation Obs, IReadOnlyList<string> Legal) : WireMessage
{
    public static string HashOf(Observation obs) => ProtocolJson.Hash(obs).ToString("x16");
}

/// <summary>Brain → host, the answer to a decision (docs: protocol §6).</summary>
/// <param name="Brain">The brain that decided, from the champion's routing table.</param>
/// <param name="Plan">The rest of the turn after <paramref name="Action"/>, from a searching brain.</param>
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

/// <summary>Where the brain listens and the mod connects, unless told otherwise.</summary>
public static class BrainSocket
{
    /// <summary>
    /// Under the user's state directory: Steam runs the game in a container that shares the home directory with the
    /// host, but not $XDG_RUNTIME_DIR.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state
            ? state
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
        "sts2ai", "brain.sock");
}

/// <summary>One end of a connection: messages as JSON lines.</summary>
public sealed class WireStream(Stream stream)
{
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false));
    private readonly StreamWriter _writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

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
