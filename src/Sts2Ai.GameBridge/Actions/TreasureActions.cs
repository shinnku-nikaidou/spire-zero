using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>An opened chest: "relic:i" takes relic i of the offered ones, "relic_skip" leaves them.</summary>
public static class TreasureActions
{
    public const string Skip = "relic_skip";

    public static TreasureScreen Screen(IReadOnlyList<RelicModel> relics) => new() { Relics = [.. relics.Select(r => r.Id.Entry)] };

    public static List<string> Legal(IReadOnlyList<RelicModel> relics) =>
        [.. Enumerable.Range(0, relics.Count).Select(i => $"relic:{i}"), Skip];

    /// <summary>Votes as the relic buttons do; the pick runs on the action queue.</summary>
    public static void Start(IReadOnlyList<RelicModel> relics, string action)
    {
        if (!Legal(relics).Contains(action)) throw ActionText.Illegal(action, Surfaces.Treasure);
        var picking = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (action == Skip) picking.SkipRelicLocally();
        else picking.PickRelicLocally(ActionText.Index(action["relic:".Length..]));
    }
}
