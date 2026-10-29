using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// What pimc values its leaves with (docs: combat-brain §4): --leaf FILE's weights (the default ones without it), and
/// --combat-net FILE's net, which then values the leaves still in progress at those weights' prices.
/// </summary>
internal sealed record LeafChoice(LeafFile? Weights, string? WeightsPath, CombatNet? Net, string? NetPath)
{
    public static LeafChoice Read(string? weightsPath, string? netPath)
    {
        var weights = weightsPath == null ? null : ProtocolJson.Deserialize<LeafFile>(File.ReadAllText(weightsPath));
        var net = netPath == null ? null : CombatNet.Load(ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(netPath)), weights?.Weights);
        return new(weights, weightsPath, net, netPath);
    }

    public bool IsDefault => Weights == null && Net == null;

    /// <summary>
    /// Fails for a combat net of another environment: it predicts the endings of another policy, and SL's planner wins
    /// more from the same state than NoSL's search (docs: learning §2). A file without one (c1–c4) plays in either.
    /// </summary>
    public void Check(PlayEnvironment env)
    {
        // OLD NETS: a file without an environment is c1–c4's; remove once no champion uses them.
        if (Net?.Env is { } own && own != env.ToString())
            throw new NotSupportedException($"combat net {Net.Name} predicts {own}'s endings, not {env}'s");
    }

    /// <summary>What follows pimc in the brain's name: the weights' name, then the net's, each when given.</summary>
    public string Suffix => (Weights != null ? $" {Weights.Name}" : "") + (Net != null ? $" {Net.Name}" : "");

    public ICombatLeaf Leaf => Net ?? (ICombatLeaf)new HandLeaf(Weights?.Weights);

    /// <summary>The options that give workers the same leaf: they start from the same folder, by full path anyway.</summary>
    public IReadOnlyList<string>? WorkerOptions()
    {
        var options = new List<string>();
        if (WeightsPath != null) options.AddRange(["--leaf", Path.GetFullPath(WeightsPath)]);
        if (NetPath != null) options.AddRange(["--combat-net", Path.GetFullPath(NetPath)]);
        return options.Count > 0 ? options : null;
    }
}
