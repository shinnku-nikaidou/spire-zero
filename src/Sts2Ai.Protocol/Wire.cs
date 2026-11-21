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
public abstract record WireMessage
{
    /// <summary>Bumped on any breaking change.</summary>
    public const int Version = 5;

    [JsonPropertyOrder(-1)]
    public int V { get; init; } = Version;
}

/// <summary>Host → brain, at the start of a run.</summary>
/// <param name="Env">"NoSL" or "SL", fixed for the run.</param>
/// <param name="Role">"mod" or "worker".</param>
/// <param name="Effort">The SL planner's effort the host asks for, fixed for the run: "low", "medium", "high" or
/// "max"; null for the brain's own.</param>
/// <param name="TurnDecay">Whether the SL planner takes a little off a line for each turn it ended, so a fight is not
/// drawn out for nothing, fixed for the run: the mod's option for a player. Self-play does not ask for it.</param>
public sealed record Hello(string GameVersion, string Env, string Role, RunHeader Run, string? Effort = null, bool TurnDecay = false) : WireMessage;

/// <param name="RunId">The run's id (<see cref="IdOf"/>).</param>
public sealed record RunHeader(string RunId, string Character, int Ascension)
{
    /// <summary>A run's id: a hash of its seed, not the seed. An SL checkpoint carries the seed, and so does every combat's
    /// export; NoSL's engine draws it again before any search reads the combat.</summary>
    public static string IdOf(string seed) => ProtocolJson.Hash(seed).ToString("x16");
}

/// <summary>Brain → host, the answer to hello: the run is accepted, and which champion plays it.</summary>
/// <param name="Champion">The champion's full name, as reports and champion files give it.</param>
/// <param name="Shown">The champion as a player sees it on the mod's panel: its nets and the size of its combat search
/// ("net v11-0-td4 + c11-0 beam 8x300").</param>
public sealed record Ready(string Champion, string Shown) : WireMessage;

/// <summary>
/// Host → brain, a decision point. <see cref="Legal"/> is empty for a card prompt: its answer is "pick:i,j,…", canonical
/// indexes the prompt accepts.
/// </summary>
/// <param name="DecisionId">From 0, increasing within the run.</param>
/// <param name="ObsHash"><see cref="ProtocolJson.Hash"/> of <see cref="Obs"/>, in hex.</param>
/// <param name="Export">At a combat decision between actions (not at a card prompt): the live combat as the bridge's
/// <c>EngineExport</c> wrote it, in base64, for the engine to take in. SL's search plays it; NoSL's draws its worlds from
/// it, with everything the player cannot see drawn again in the engine before any search reads it (the draw pile's
/// order from the public record the export carries).</param>
/// <param name="Trace">SL only, and always there: every step taken since the latest <see cref="NodeCheckpoint"/>, in the
/// order the steps took effect, as "surface action" (a card prompt's picks as the game's own indexes). With the
/// checkpoint it is the clone this decision is asked on (<see cref="NodeClone"/>). Sent whole each time: a host whose
/// player went back (into a room left for the map, out of a card reward unanswered) sends a shorter one.</param>
/// <param name="Prompt">At a card prompt during a combat: the combat for the engine to stand at it, as the host last wrote
/// it out in the fight and the steps since (<see cref="PromptExport"/>); none where the host's record of the fight's
/// steps broke.</param>
public sealed record DecisionRequest(long DecisionId, string ObsHash, string Surface, Observation Obs, IReadOnlyList<string> Legal, string? Export = null,
    IReadOnlyList<string>? Trace = null, PromptExport? Prompt = null)
    : WireMessage
{
    public static string HashOf(Observation obs) => ProtocolJson.Hash(obs).ToString("x16");
}

/// <summary>
/// The combat at a card prompt it waits on, as the host gives it for the engine to stand there
/// (plans/2026-11-21-1213-nosl-prompt-worlds.md): the game is in the middle of an action, which no export can be
/// taken in.
/// </summary>
/// <param name="Export">The combat as the host last wrote it out in the fight, in base64, as
/// <see cref="DecisionRequest.Export"/>.</param>
/// <param name="Opening">Whether it was written as the combat began, before its first turn.</param>
/// <param name="Run">The run as it stood when it was written.</param>
/// <param name="Steps">The steps taken since, in order, as <see cref="DecisionRequest.Trace"/> names them.</param>
/// <param name="DrawLog">The draw pile's public record at the prompt, as an export's <c>sim.draw_log</c>.</param>
public sealed record PromptExport(string Export, bool Opening, RunView Run, IReadOnlyList<string> Steps, string DrawLog);

/// <summary>Brain → host, the answer to a decision.</summary>
/// <param name="Brain">The brain that decided, from the champion's routing table.</param>
/// <param name="Plan">What the brain planned after <paramref name="Action"/>: the rest of the turn from a searching
/// combat brain, the rest of the act's route from the macro brain on the map.</param>
/// <param name="Candidates">The first actions it scored, best first.</param>
/// <param name="Searching">True on the brain's pick so far while it searches on (max's solver in the mod's brain): one
/// comes at once, another each time the pick changes, and the final answer to the same decision follows, which is the
/// one the host's AI plays. Null on a final answer.</param>
/// <param name="Nodes">The actions a combat search simulated for the decision so far.</param>
/// <param name="Proved">True when the solver proved its pick the best there is: on a pick after the answer
/// (<paramref name="After"/>), the plan after the answer's action.</param>
/// <param name="After">True on the plan after the final answer, by a brain that searches on from where its answer leads
/// (max's solver in the mod's brain): the same action, with the plan after it, its chance to win and HP left and whether
/// it is proved, as the search has them now. One comes each time they change, until the host asks the next decision.
/// Null otherwise.</param>
public sealed record Recommendation(long DecisionId, string ObsHash, string Action, string Brain,
    IReadOnlyList<string>? Plan = null, IReadOnlyList<Candidate>? Candidates = null, bool? Searching = null, long? Nodes = null,
    bool? Proved = null, bool? After = null) : WireMessage;

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
/// Host → brain, SL only: a checkpoint, before the first decision after it: one of the game's saves (on arrival at a map
/// point, or inside a room the game marked finished: after a won fight, after an ancient) and the relic fields it drops,
/// opaque outside the simulator. Checkpoints are numbered from 0 in the run. <see cref="AutoWin"/> is the run's rule,
/// which the clones keep (<see cref="NodeClone.AutoWin"/>); null in the normal game, and always from the real game.
/// </summary>
public sealed record NodeCheckpoint(int NodeIndex, string Checkpoint, IReadOnlyList<int>? AutoWin = null) : WireMessage;


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
