using System.CommandLine;
using System.Text.Json.Nodes;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// clones: the SL export round trip (docs: mod §6, §12). Reads what a host sent the brain (brain --wire), and for each
/// decision asked in SL loads its node's clone, the checkpoint and the steps sent before the decision, as the brain's
/// search does. The clone must ask the same decision next, observation for observation; any difference is reported
/// with the first field that differs.
/// </summary>
internal static class ClonesCommand
{
    private static readonly Option<string> Wire = Opt.Required<string>("--wire", "a host's messages, one JSON line each, as brain --wire writes them");

    public static Command Command()
    {
        var command = Opt.Command("clones", "replays each SL decision of a host's messages on its node's clone, and compares observations", [Wire]);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        string? env = null;
        NodeCheckpoint? node = null;
        var steps = new List<string>();
        int decisions = 0, equal = 0, outside = 0;
        foreach (var line in File.ReadLines(r.GetValue(Wire)!))
        {
            switch (ProtocolJson.Deserialize<WireMessage>(line))
            {
                case Hello hello:
                    (env, node) = (hello.Env, null);
                    break;
                case NodeCheckpoint checkpoint:
                    (node, steps) = (checkpoint, []);
                    break;
                case NodeAction action:
                    if (action.NodeIndex != node?.NodeIndex || action.Seq != steps.Count)
                        throw new CliException($"node action {action.NodeIndex}:{action.Seq} after {node?.NodeIndex}:{steps.Count - 1}");
                    steps.Add(action.Action);
                    break;
                case DecisionRequest request when env == nameof(PlayEnvironment.SL):
                    if (node == null)
                    {
                        outside++;
                        break;
                    }
                    decisions++;
                    var (asked, failure) = NextDecision(pump, new NodeClone(node.Checkpoint, steps.ToList()));
                    string where = $"node {node.NodeIndex} step {steps.Count}, decision {request.DecisionId} ({request.Surface})";
                    if (asked == null) Console.WriteLine($"{where}: the clone fails: {failure}");
                    else if (DecisionRequest.HashOf(asked) != request.ObsHash)
                        Console.WriteLine($"{where}: the clone asks another decision: {Difference(ProtocolJson.Serialize(request.Obs), ProtocolJson.Serialize(asked))}");
                    else equal++;
                    break;
            }
        }
        Console.WriteLine($"clones: {equal} of {decisions} SL decisions asked the same on their node's clone" +
                          (outside > 0 ? $"; {outside} came before any checkpoint" : ""));
        return equal == decisions ? 0 : 1;
    }

    /// <summary>The observation of the decision the clone asks once its steps are replayed, or why it asks none.</summary>
    private static (Observation? Asked, string? Failure) NextDecision(Pump pump, NodeClone clone)
    {
        var next = new Next();
        try
        {
            // A card prompt can be asked while the last step plays out, inside the load.
            using var session = RunSession.Load(pump, clone, next, next, next);
            if (next.Asked == null) session.Step();
        }
        catch (Exception e)
        {
            return (null, $"{e.GetType().Name}: {Logs.FirstLine(e.Message)}");
        }
        return (next.Asked, next.Asked == null ? "no decision after its steps" : null);
    }

    /// <summary>
    /// Keeps the observation of the first decision it is asked. It answers every decision with its first option rather
    /// than stopping the clone: card prompts, card rewards and bundles are asked inside the game's own tasks.
    /// </summary>
    private sealed class Next : ICombatPolicy, IChoicePolicy, IMacroPolicy
    {
        public Observation? Asked { get; private set; }

        public CombatAction Choose(CombatDecision decision)
        {
            Asked ??= decision.RunObservation;
            return decision.Legal[0];
        }

        IReadOnlyList<int> IChoicePolicy.Choose(ChoiceDecision decision)
        {
            Asked ??= decision.Observation;
            return Enumerable.Range(0, decision.Choice.Min).ToList();
        }

        public string Choose(MacroDecision decision)
        {
            Asked ??= decision.Observation;
            return decision.Legal[0];
        }
    }

    /// <summary>The first path where two JSON documents differ, with both values.</summary>
    private static string Difference(string expected, string actual)
    {
        return Walk(JsonNode.Parse(expected), JsonNode.Parse(actual), "") ?? "the same JSON with another hash";

        static string? Walk(JsonNode? a, JsonNode? b, string path)
        {
            switch (a, b)
            {
                case (JsonObject x, JsonObject y):
                    foreach (var key in x.Select(p => p.Key).Union(y.Select(p => p.Key)))
                        if (Walk(x[key], y[key], $"{path}.{key}") is { } found) return found;
                    return null;
                case (JsonArray x, JsonArray y):
                    for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
                        if (Walk(i < x.Count ? x[i] : null, i < y.Count ? y[i] : null, $"{path}[{i}]") is { } found) return found;
                    return null;
                default:
                    string sa = a?.ToJsonString() ?? "absent", sb = b?.ToJsonString() ?? "absent";
                    return sa == sb ? null : $"{path}: game {Logs.FirstLine(sa)}, clone {Logs.FirstLine(sb)}";
            }
        }
    }
}
