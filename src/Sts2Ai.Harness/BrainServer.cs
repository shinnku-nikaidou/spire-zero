using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// The brain's end of the wire. Each run on a connection gets its own Harness from
/// <paramref name="harness"/>, made at hello with the champion as a player sees it (<see cref="Ready.Shown"/>; it refuses the run by throwing); each decision is routed through it and
/// answered with a recommendation, in order. A decision it cannot answer (an illegal pick, an uncovered surface, an
/// observation that does not match its hash) gets an error instead, and the host ends the run. An SL checkpoint gets
/// no answer: one that Harness refuses fails the run's next decision; each SL decision brings the steps since it. An
/// answer the host says it played (<see cref="Executed"/>) is handed to the run's Harness with the state it was given at
/// (<see cref="Dispatcher.Played"/>). <paramref name="ended"/> gets each run's Harness at
/// run_end; <paramref name="received"/> every message a host sends; <paramref name="answered"/> the run's Harness after
/// each answer is sent, so its decision log can be written as it grows.
///
/// A brain that searches on before it answers (max's solver in the background) sends its pick so far first
/// (<see cref="Recommendation.Searching"/>), and the host's Cancel of the decision it is on makes it answer at once:
/// messages are read on a thread of their own, and handled in order as before. One that searches on after it answers
/// sends the plan after the answer each time it changes (<see cref="Recommendation.After"/>), from its searching thread,
/// after the answer: every message goes out whole, one at a time.
/// </summary>
public sealed class BrainServer(Func<Hello, (Dispatcher Run, string Shown)> harness, Action<Hello, Dispatcher, RunEnd>? ended = null,
    Action<WireMessage>? received = null, Action<Dispatcher>? answered = null)
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
        var inbox = new BlockingCollection<WireMessage>();
        var gate = new object();
        var sending = new object();
        void Send(WireMessage message)
        {
            lock (sending) wire.Send(message);
        }
        (long Id, CancellationTokenSource Hurry)? deciding = null;
        Exception? lost = null;
        var reader = new Thread(() =>
        {
            try
            {
                while (wire.Receive() is { } message)
                {
                    if (message is Cancel cancel)
                        lock (gate)
                            if (deciding is { } at && at.Id == cancel.DecisionId) at.Hurry.Cancel();
                    inbox.Add(message);
                }
            }
            catch (Exception e)
            {
                lost = e;
            }
            finally
            {
                inbox.CompleteAdding();
            }
        }) { IsBackground = true, Name = "brain server reader" };
        reader.Start();
        Hello? hello = null;
        Dispatcher? run = null;
        long last = -1;
        string? refused = null;
        // Each answered decision's state (MacroAnswer.State), until the host says it played the answer.
        var states = new Dictionary<long, ulong>();
        foreach (var message in inbox.GetConsumingEnumerable())
        {
            received?.Invoke(message);
            switch (message)
            {
                case Hello next:
                    run?.Rest();
                    (hello, run, last, refused) = (next, null, -1, null);
                    states.Clear();
                    try
                    {
                        (run, var shown) = harness(next);
                        Send(new Ready(run.Champion, shown));
                    }
                    catch (Exception e)
                    {
                        Send(new WireError(Describe(e)));
                    }
                    break;
                case DecisionRequest request:
                    using (var hurry = new CancellationTokenSource())
                    {
                        lock (gate) deciding = (request.DecisionId, hurry);
                        try
                        {
                            if (run == null) throw new InvalidDataException($"decision {request.DecisionId} outside a run");
                            if (refused != null) throw new InvalidDataException(refused);
                            if (request.DecisionId <= last) throw new InvalidDataException($"decision {request.DecisionId} after decision {last}");
                            last = request.DecisionId;
                            run.Steps(request.Trace);
                            // The plan after the answer goes out after it: the last one told before it, then each one told.
                            bool sent = false;
                            Recommendation? held = null;
                            void Later(Recommendation later)
                            {
                                lock (sending)
                                    if (sent) wire.Send(later);
                                    else held = later;
                            }
                            var (recommendation, state) = Answer(run, request, Send, hurry.Token, Later);
                            if (state is { } given) states[request.DecisionId] = given;
                            lock (sending)
                            {
                                wire.Send(recommendation);
                                sent = true;
                                if (held != null) wire.Send(held);
                            }
                            answered?.Invoke(run);
                        }
                        catch (Exception e)
                        {
                            Send(new WireError(Describe(e)));
                        }
                        finally
                        {
                            lock (gate) deciding = null;
                        }
                    }
                    break;
                case NodeCheckpoint checkpoint:
                    try
                    {
                        if (run == null) throw new InvalidDataException($"{message.GetType().Name} outside a run");
                        run.Checkpoint(checkpoint);
                    }
                    catch (Exception e)
                    {
                        refused ??= Describe(e);
                    }
                    break;
                case RunEnd end:
                    run?.Rest();
                    if (run != null) ended?.Invoke(hello!, run, end);
                    (hello, run) = (null, null);
                    break;
                case Executed { Ok: true } executed:
                    if (run != null && states.Remove(executed.DecisionId, out var played)) run.Played(played);
                    break;
                // A Cancel has hurried its decision as it came (the reader), and each decision is answered before
                // the next is taken, so there is no search to drop.
                case Executed or Cancel:
                    break;
                default:
                    Send(new WireError($"unexpected {message.GetType().Name}"));
                    break;
            }
        }
        run?.Rest();
        if (lost != null) ExceptionDispatchInfo.Capture(lost).Throw();
    }

    /// <returns>The answer, and the state it was given at, if the run keeps one (<see cref="MacroAnswer.State"/>).</returns>
    /// <param name="send">Sends the pick so far of a combat brain that searches on (<see cref="CombatDecision.Early"/>).</param>
    /// <param name="hurry">Cancelled when the host cancels the decision (<see cref="CombatDecision.Hurry"/>).</param>
    /// <param name="later">Sends the answer again with each plan after it of a combat brain that searches on after it
    /// answers (<see cref="CombatDecision.After"/>).</param>
    private static (Recommendation Answer, ulong? State) Answer(Dispatcher harness, DecisionRequest request, Action<Recommendation> send,
        CancellationToken hurry, Action<Recommendation> later)
    {
        var obs = request.Obs;
        // A field this build does not know is dropped on reading, so a mod built from newer wire types fails here.
        if (DecisionRequest.HashOf(obs) != request.ObsHash)
            throw new InvalidDataException($"obs_hash {request.ObsHash} is not the observation's: are the mod and the brain built from the same commit?");
        // The live combat an SL host sends, with the run around it as a combat's leaf reads it, as a host in this process gives it.
        Func<CombatExport>? export = request.Export is { } bytes ? () => new CombatExport(Convert.FromBase64String(bytes), obs.Run.WithoutPreviews()) : null;
        void Early(CombatDecisionResult result, string action, bool plan)
        {
            var note = harness.Note(result);
            send(new Recommendation(request.DecisionId, request.ObsHash, action, note.Brain, plan ? note.Plan : null, note.Candidates, true, note.Nodes,
                note.Proved));
        }
        void After(CombatDecisionResult result, string action, bool plan)
        {
            var note = harness.Note(result);
            later(new Recommendation(request.DecisionId, request.ObsHash, action, note.Brain, plan ? note.Plan : null, note.Candidates, Nodes: note.Nodes,
                Proved: note.Proved, After: true));
        }
        var (action, note, state) = (request.Surface, obs.Screen) switch
        {
            (Surfaces.Combat, CombatScreen screen) => Text(harness.Decide(new CombatDecision([.. request.Legal.Select(CombatAction.Parse)], () => screen.Combat, _ => obs,
                export, r => Early(r, r.First.ToString(), plan: true), hurry, r => After(r, r.First.ToString(), plan: true)))),
            // A card prompt's plan goes on with plays of the combat, which the prompt's screen has no cards to name.
            (Surfaces.CardChoice, CardChoiceScreen screen) => Text(harness.Decide(new ChoiceDecision(screen.Choice, () => obs,
                early: r => Early(r, MacroAction.Pick(r.First.Picks).ToString(), plan: false), hurry: hurry,
                after: r => After(r, MacroAction.Pick(r.First.Picks).ToString(), plan: false),
                prompted: request.Prompt is { } at
                    ? () => new PromptCombat(new CombatExport(Convert.FromBase64String(at.Export), at.Run), at.Opening, at.Steps, at.DrawLog)
                    : null))),
            (Surfaces.Combat or Surfaces.CardChoice, _) => throw new InvalidDataException($"a {request.Surface} decision without its screen"),
            _ => Text(harness.Decide(new MacroDecision(request.Surface, [.. request.Legal.Select(MacroAction.Parse)], () => obs))),
        };
        return (new Recommendation(request.DecisionId, request.ObsHash, action, note.Brain, note.Plan, note.Candidates, Nodes: note.Nodes,
            Proved: note.Proved), state);
    }

    private static (string Action, DecisionNote Note, ulong? State) Text((CombatAction Action, DecisionNote Note) decided) =>
        (decided.Action.ToString(), decided.Note, null);

    private static (string Action, DecisionNote Note, ulong? State) Text((ChoiceAnswer Answer, DecisionNote Note) decided) =>
        (MacroAction.Pick(decided.Answer.Picks).ToString(), decided.Note, decided.Answer.State);

    private static (string Action, DecisionNote Note, ulong? State) Text((MacroAnswer Answer, DecisionNote Note) decided) =>
        (decided.Answer.Action.ToString(), decided.Note, decided.Answer.State);

    private static string Describe(Exception e) => $"{e.GetType().Name}: {e.Message}";
}
