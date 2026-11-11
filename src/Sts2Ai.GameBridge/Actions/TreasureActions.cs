using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>An opened chest: "relic:i" takes relic i of the offered ones, "relic_skip" leaves them.</summary>
public static class TreasureActions
{
    public static TreasureScreen Screen(IReadOnlyList<RelicModel> relics) => new() { Relics = [.. relics.Select(r => r.Id.Entry)] };

    public static List<MacroAction> Legal(IReadOnlyList<RelicModel> relics) =>
        [.. Enumerable.Range(0, relics.Count).Select(i => MacroAction.Of(MacroAction.Relic, i)), MacroAction.RelicSkip];

    /// <summary>Votes as the relic buttons do; the pick runs on the action queue.</summary>
    public static void Start(IReadOnlyList<RelicModel> relics, MacroAction action)
    {
        if (!Legal(relics).Contains(action)) throw Illegal.Action(action, Surfaces.Treasure);
        var picking = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (action == MacroAction.RelicSkip) picking.SkipRelicLocally();
        else picking.PickRelicLocally(action.Index);
    }
}
