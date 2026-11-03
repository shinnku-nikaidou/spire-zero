using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// What the search values its leaves with (docs: combat-brain/leaf §1): --combat-net FILE's net; none for a placeholder
/// policy, which does not search.
/// </summary>
internal sealed record LeafChoice(CombatNet? Net, string? NetPath)
{
    public static LeafChoice Read(string? netPath) =>
        new(netPath == null ? null : CombatNet.Load(ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(netPath))), netPath);

    /// <summary>What follows the search in the brain's name: the net's, when given.</summary>
    public string Suffix => Net != null ? $" {Net.Name}" : "";

    public ICombatLeaf Leaf => Net ?? throw new InvalidOperationException("no combat net: pimc and plan need --combat-net");

    /// <summary>The options that give workers the same leaf: they start from the same folder, by full path anyway.</summary>
    public IReadOnlyList<string>? WorkerOptions() => NetPath != null ? ["--combat-net", Path.GetFullPath(NetPath)] : null;
}
