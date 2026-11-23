using System.CommandLine;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// clones: the SL export round trip. Reads what a host sent the brain (brain --wire), and for each
/// decision asked in SL loads its node's clone, the checkpoint and the steps sent before the decision, as the brain's
/// search does. The clone must ask the same decision next, observation for observation; any difference is reported
/// with the first field that differs.
/// </summary>
internal static class ClonesCommand
{
    private static readonly Option<string> Wire = Opt.Required<string>("--wire",
        "a host's messages, one JSON line each, as brain --wire writes them; or a game session's folder of the mod's logs, which holds them in wire.jsonl");

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
        int decisions = 0, equal = 0, outside = 0;
        string path = r.GetValue(Wire)!;
        // A game session's folder of the mod's logs holds its brains' messages in wire.jsonl.
        if (Directory.Exists(path)) path = Path.Combine(path, "wire.jsonl");
        foreach (var line in File.ReadLines(path))
        {
            switch (ProtocolJson.Deserialize<WireMessage>(line))
            {
                case Hello hello:
                    (env, node) = (hello.Env, null);
                    break;
                case NodeCheckpoint checkpoint:
                    node = checkpoint;
                    break;
                case DecisionRequest request when env == nameof(PlayEnvironment.SL):
                    if (node == null)
                    {
                        outside++;
                        break;
                    }
                    decisions++;
                    var steps = request.Trace ?? throw new CliException($"SL decision {request.DecisionId} without its steps");
                    var (asked, failure) = NextDecision(pump, new NodeClone(node.Checkpoint, steps, node.AutoWin));
                    string where = $"checkpoint {node.NodeIndex} step {steps.Count}, decision {request.DecisionId} ({request.Surface})";
                    if (asked == null) Console.WriteLine($"{where}: the clone fails: {failure}");
                    else if (DecisionRequest.HashOf(asked) != request.ObsHash)
                        Console.WriteLine($"{where}: the clone asks another decision: {ProtocolJson.Difference(request.Obs, asked)}");
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
            // A card reward or a bundle can be asked while the last step plays out, inside the load.
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
    /// than stopping the clone: card rewards and bundles are asked inside the game's own tasks.
    /// </summary>
    private sealed class Next : ICombatPolicy, IChoicePolicy, IMacroPolicy
    {
        public Observation? Asked { get; private set; }

        public CombatAction Choose(CombatDecision decision)
        {
            Asked ??= decision.RunObservation;
            return decision.Legal[0];
        }

        ChoiceAnswer IChoicePolicy.Choose(ChoiceDecision decision)
        {
            Asked ??= decision.Observation;
            return new([.. Enumerable.Range(0, decision.Choice.Min)]);
        }

        public MacroAnswer Choose(MacroDecision decision)
        {
            Asked ??= decision.Observation;
            return new(decision.Legal[0]);
        }
    }
}
