using System.Text.Json;
using Sts2Ai.Brain.Combat;
using Sts2Ai.Brain.Macro;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The nets' inputs for a trainer, made by the code that feeds the nets in play
/// (<see cref="RunNet.Tokens"/>, <see cref="CombatTokens.Of"/>), so that a state is read one way only. A trainer starts
/// it and asks line by line; each request is a letter and a decision as the decision log holds it (a log's line as it is,
/// or any JSON with the fields named), and gets one line of JSON back:
/// <list type="bullet">
/// <item><c>R</c>, a macro decision: <c>{"run": X}</c>, the run it was shown (<c>run_view</c>) as V reads it
/// (<see cref="RunNet.Write"/>); when it is a map move (<c>surface</c> "map", <c>chosen</c> "map:C,R") also
/// <c>"entered": X</c>, the move's afterstate, which is what V reads when it values the move and no log shows; when it
/// logs event options (<c>event</c>, <see cref="EventLine"/>) also <c>"event"</c>, for each in order
/// <c>{"run": X, "end": X, "ending": E}</c>: the run with the option pending, as V in NoSL reads it, and where its line
/// ended, so read too (no "end" where it gave none);</item>
/// <item><c>C</c>, a combat decision: <c>{"tokens": T}</c>, its state (<c>obs</c>) with the combat's record of enemy moves
/// (<c>history</c>) as the combat net reads them.</item>
/// </list>
/// Each is read in the decision's environment (<c>env</c>; SL if none), as its brain read it; an event option's runs in
/// NoSL, whose V they teach.
/// A request that cannot be read gets <c>{"error": "..."}</c>. An empty line asks for nothing: the answers so far are
/// sent, which a trainer that waits for them must ask for. The first line out says who is encoding: the build's commit,
/// the names of a run's numbers and the combat net's lags. Which decisions are samples, and their labels, stay the
/// trainer's.
/// </summary>
public static class TokenServer
{
    /// <summary>What a request is read for.</summary>
    private sealed record Decision(string? Surface, string? Chosen, CombatObservation? Obs, RunView? RunView, IReadOnlyList<EnemyTurns>? History,
        string? Env, IReadOnlyList<EventLine>? Event);

    public static void Serve(TextReader requests, Stream stdout, string commit)
    {
        using var output = new BufferedStream(stdout, 1 << 16);
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteString("commit", commit);
        json.WritePropertyName("state");
        TokenJson.Strings(json, RunNet.StateNames);
        json.WriteNumber("lags", CombatTokens.Lags);
        json.WriteEndObject();
        End(json, output);
        output.Flush();
        while (requests.ReadLine() is { } request)
        {
            if (request.Length == 0)
            {
                output.Flush();
                continue;
            }
            json.WriteStartObject();
            try
            {
                Answer(request, json);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidDataException)
            {
                // Whatever of the answer was written is dropped with the writer's state.
                json.Reset();
                json.WriteStartObject();
                json.WriteString("error", e.Message);
            }
            json.WriteEndObject();
            End(json, output);
        }
    }

    private static void End(Utf8JsonWriter json, Stream output)
    {
        json.Flush();
        json.Reset();
        output.WriteByte((byte)'\n');
    }

    private static void Answer(string request, Utf8JsonWriter json)
    {
        var decision = ProtocolJson.Deserialize<Decision>(request[1..]) ?? throw new InvalidDataException("a request with no decision");
        var env = decision.Env == null ? PlayEnvironment.SL
            : Enum.TryParse<PlayEnvironment>(decision.Env, out var named) ? named : throw new InvalidDataException($"no environment {decision.Env}");
        switch (request[0])
        {
            case 'R':
                var run = decision.RunView ?? throw new InvalidDataException("a macro decision with no run_view");
                json.WritePropertyName("run");
                RunNet.Write(json, run, env);
                if (decision.Surface == Surfaces.Map && decision.Chosen != null && MacroAction.Parse(decision.Chosen) is { Verb: MacroAction.Map } move)
                {
                    json.WritePropertyName("entered");
                    RunNet.Write(json, ScreenAfterstates.Entered(run, move.Coord), env);
                }
                if (decision.Event is not { } lines) break;
                json.WriteStartArray("event");
                foreach (var line in lines)
                {
                    json.WriteStartObject();
                    json.WritePropertyName("run");
                    RunNet.Write(json, run with { Pending = line.Option }, PlayEnvironment.NoSL);
                    if (line.End != null)
                    {
                        json.WritePropertyName("end");
                        RunNet.Write(json, line.End, PlayEnvironment.NoSL);
                    }
                    if (line.Ending != null) json.WriteString("ending", line.Ending);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                break;
            case 'C':
                var obs = decision.Obs ?? throw new InvalidDataException("a combat decision with no obs");
                json.WritePropertyName("tokens");
                CombatTokens.Of(obs, decision.History ?? [], CombatTokens.Lags, env).Write(json);
                break;
            default:
                throw new InvalidDataException($"a request starts with R or C, not {request[0]}");
        }
    }
}
