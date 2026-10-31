using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// What pimc values its leaves with (docs: combat-brain/leaf §2, §3): --leaf FILE's weights (the default ones without it), and
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
