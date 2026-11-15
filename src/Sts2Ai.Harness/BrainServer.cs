using System.Net.Sockets;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// The brain's end of the wire. Each run on a connection gets its own Harness from
/// <paramref name="harness"/>, made at hello (it refuses the run by throwing); each decision is routed through it and
/// answered with a recommendation, in order. A decision it cannot answer (an illegal pick, an uncovered surface, an
/// observation that does not match its hash) gets an error instead, and the host ends the run. SL node messages get
/// no answer: one that Harness refuses fails the run's next decision. <paramref name="ended"/> gets each run's Harness at
/// run_end; <paramref name="received"/> every message a host sends.
/// </summary>
public sealed class BrainServer(Func<Hello, Dispatcher> harness, Action<Hello, Dispatcher, RunEnd>? ended = null,
    Action<WireMessage>? received = null)
{
    /// <summary>A Unix socket listening at the abstract name <paramref name="path"/> (a leading NUL: no file), for a host in the same process.</summary>
    public static Socket Bind(string path)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        return listener;
    }

    /// <summary>Serves one connection until the host closes it.</summary>
    public void Serve(Socket socket)
    {
        using var stream = new NetworkStream(socket);
        Serve(new WireStream(stream));
    }

    public void Serve(WireStream wire)
    {
        Hello? hello = null;
        Dispatcher? run = null;
        long last = -1;
        string? refused = null;
        while (wire.Receive() is { } message)
        {
            received?.Invoke(message);
            switch (message)
            {
                case Hello next:
                    (hello, run, last, refused) = (next, null, -1, null);
                    try
                    {
                        run = harness(next);
                        wire.Send(new Ready(run.Champion));
                    }
                    catch (Exception e)
                    {
                        wire.Send(new WireError(Describe(e)));
                    }
                    break;
                case DecisionRequest request:
                    try
                    {
                        if (run == null) throw new InvalidDataException($"decision {request.DecisionId} outside a run");
                        if (refused != null) throw new InvalidDataException(refused);
                        if (request.DecisionId <= last) throw new InvalidDataException($"decision {request.DecisionId} after decision {last}");
                        last = request.DecisionId;
                        wire.Send(Answer(run, request));
                    }
                    catch (Exception e)
                    {
                        wire.Send(new WireError(Describe(e)));
                    }
                    break;
                case NodeCheckpoint or NodeAction:
                    try
                    {
                        if (run == null) throw new InvalidDataException($"{message.GetType().Name} outside a run");
                        if (message is NodeCheckpoint checkpoint) run.Checkpoint(checkpoint);
                        else run.Act((NodeAction)message);
                    }
                    catch (Exception e)
                    {
                        refused ??= Describe(e);
                    }
                    break;
                case RunEnd end:
                    if (run != null) ended?.Invoke(hello!, run, end);
                    (hello, run) = (null, null);
                    break;
                // Each decision is answered before the next message is read, so there is no search to drop.
                case Executed or Cancel:
                    break;
                default:
                    wire.Send(new WireError($"unexpected {message.GetType().Name}"));
                    break;
            }
        }
    }

    private static Recommendation Answer(Dispatcher harness, DecisionRequest request)
    {
        var obs = request.Obs;
        // A field this build does not know is dropped on reading, so a mod built from newer wire types fails here.
        if (DecisionRequest.HashOf(obs) != request.ObsHash)
            throw new InvalidDataException($"obs_hash {request.ObsHash} is not the observation's: are the mod and the brain built from the same commit?");
        // The live combat an SL host sends, with the run around it as a combat's leaf reads it, as a host in this process gives it.
        Func<CombatExport>? export = request.Export is { } bytes ? () => new CombatExport(Convert.FromBase64String(bytes), obs.Run.WithoutPreviews()) : null;
        var (action, note) = (request.Surface, obs.Screen) switch
        {
            (Surfaces.Combat, CombatScreen screen) => Text(harness.Decide(new CombatDecision([.. request.Legal.Select(CombatAction.Parse)], () => screen.Combat, _ => obs,
                export))),
            (Surfaces.CardChoice, CardChoiceScreen screen) => Text(harness.Decide(new ChoiceDecision(screen.Choice, () => obs))),
            (Surfaces.Combat or Surfaces.CardChoice, _) => throw new InvalidDataException($"a {request.Surface} decision without its screen"),
            _ => Text(harness.Decide(new MacroDecision(request.Surface, [.. request.Legal.Select(MacroAction.Parse)], () => obs))),
        };
        return new Recommendation(request.DecisionId, request.ObsHash, action, note.Brain, note.Plan, note.Candidates);
    }

    private static (string Action, DecisionNote Note) Text((CombatAction Action, DecisionNote Note) decided) => (decided.Action.ToString(), decided.Note);

    private static (string Action, DecisionNote Note) Text((ChoiceAnswer Answer, DecisionNote Note) decided) =>
        (MacroAction.Pick(decided.Answer.Picks).ToString(), decided.Note);

    private static (string Action, DecisionNote Note) Text((MacroAnswer Answer, DecisionNote Note) decided) => (decided.Answer.Action.ToString(), decided.Note);

    private static string Describe(Exception e) => $"{e.GetType().Name}: {e.Message}";
}
