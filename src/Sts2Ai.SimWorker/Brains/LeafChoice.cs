using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Brains;

/// <summary>
/// V (--value FILE), which l0 values its options by and which prices the combat net's endings, and what the search values
/// its leaves with: --combat-net FILE's net, priced by V, a fight's end by its score with f (--f-net FILE,
/// <see cref="CombatNet.FightValue"/>); none for a placeholder policy, which does not search.
/// </summary>
internal sealed record LeafChoice(CombatNet? Net, string? NetPath, IRunValue? Value, string? ValuePath)
{
    public static LeafChoice Read(string? netPath, string? valuePath, string? savesPath = null)
    {
        IRunValue? value = valuePath == null ? null : RunNet.Read(valuePath);
        if (netPath != null && value == null) throw new CliException("--combat-net needs --value: V prices the combat's endings");
        if ((savesPath != null) != (netPath != null))
            throw new CliException("--combat-net and --f-net go together: a fight's end is its score, HP + max HP + f of the potions it keeps");
        var saves = savesPath == null ? null : SavesNet.Read(savesPath);
        var net = netPath == null ? null : CombatNet.Load(ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(netPath)), value!, saves);
        return new(net, netPath, value, valuePath);
    }

    /// <summary>What follows the search in the brain's name: the net's, and f's after a "+", when given.</summary>
    public string Suffix => Net != null ? $" {Net.Name}{(Net.SavesName is { } f ? $" + {f}" : "")}" : "";

    public ICombatLeaf Leaf => Net ?? throw new InvalidOperationException("no combat net: pimc and plan need --combat-net");
}
