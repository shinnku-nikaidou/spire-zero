using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>Map travel: "map:COL,ROW".</summary>
public static class MapActions
{
    /// <summary>
    /// Where the player can travel next: the rules of <c>NMapScreen.RecalculateTravelability</c>. Sorted by
    /// (row, col), since <see cref="MapPoint.Children"/> is a hash set whose order differs between processes.
    /// </summary>
    public static List<MapCoord> Travelable(RunState run)
    {
        var map = run.Map;
        var visited = run.VisitedMapCoords;
        if (visited.Count == 0) return [map.StartingMapPoint.coord];
        var last = visited[^1];
        if (map.SecondBossMapPoint != null && last == map.BossMapPoint.coord) return [map.SecondBossMapPoint.coord];
        if (last.row == map.GetRowCount() - 1) return [map.BossMapPoint.coord];
        var point = map.GetPoint(last) ?? throw new InvalidOperationException($"last visited coord {last} is not on the map");
        return [.. MapTravel.GetTravelablePointsFrom(run, point).Select(p => p.coord).OrderBy(c => c.row).ThenBy(c => c.col)];
    }

    public static MapScreen Screen(RunState run) => new() { Travelable = [.. Travelable(run).Select(Observe.Coord)] };

    public static List<string> Legal(RunState run) => [.. Travelable(run).Select(Text)];

    public static string Text(MapCoord c) => $"map:{c.col},{c.row}";

    /// <summary>Votes for the point, as clicking it on the map screen does; the move runs on the action queue.</summary>
    public static void Start(RunState run, Player player, string action)
    {
        var travelable = Travelable(run);
        int i = travelable.FindIndex(c => Text(c) == action);
        if (i < 0) throw ActionText.Illegal(action, Surfaces.Map);
        var vote = new MapVote { coord = travelable[i], mapGenerationCount = RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount };
        var source = new MapLocation(run.CurrentMapCoord, run.CurrentActIndex);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new VoteForMapCoordAction(player, source, vote));
    }
}
