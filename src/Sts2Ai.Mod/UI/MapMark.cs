using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>A route on the map: where the player is (null before the act's first point), then the points to go to.</summary>
internal sealed record MapRoute(MapCoord? At, IReadOnlyList<MapCoord> Points)
{
    /// <summary>A map answer's route: its point, then the points the brain planned after it; null for any other answer.</summary>
    public static MapRoute? Of(Recommendation answer, Observation obs)
    {
        if (!ActionText.Is(answer.Action, "map", out _)) return null;
        var visited = obs.Run.Map.Visited;
        var at = visited.Count > 0 ? new MapCoord(visited[^1].Col, visited[^1].Row) : (MapCoord?)null;
        return new(at, (answer.Plan ?? []).Prepend(answer.Action).Select(Coord).ToList());
    }

    private static MapCoord Coord(string action)
    {
        ActionText.Is(action, "map", out var arg);
        var at = arg.Split(',').Select(int.Parse).ToArray();
        return new(at[0], at[1]);
    }
}

/// <summary>
/// A route on the map (docs: output §3): a pulsing purple ring on its next point, and a purple line along each path
/// from where the player is to its end. It lives in the mod's layer and follows the game's nodes on screen every frame,
/// since the map scrolls. The map screen keeps its point nodes and each path's dots in private dictionaries; a new act
/// replaces them.
/// </summary>
internal sealed class MapMark
{
    private const int Segments = 48;
    private static readonly Color Purple = Panel.Accent;

    private readonly CanvasLayer _layer;
    private readonly Line2D _ring = new() { Width = 7, DefaultColor = Purple, Closed = true, Antialiased = true, Visible = false };
    private readonly List<(Line2D Line, IReadOnlyList<TextureRect> Dots)> _paths = [];
    private MapRoute? _route;
    private NMapPoint? _point;

    public MapMark(CanvasLayer layer)
    {
        _layer = layer;
        layer.AddChild(_ring);
    }

    /// <summary>Marks a route, or nothing for null; once per frame.</summary>
    /// <param name="whole">The lines along the route too, not only the ring on its next point.</param>
    public void Show(MapRoute? route, bool visible, bool whole = true)
    {
        if (route != _route || _point != null && !GodotObject.IsInstanceValid(_point)) Find(route);
        _ring.Visible = visible && _point != null && _point.IsVisibleInTree();
        foreach (var (line, dots) in _paths)
        {
            // A path's line runs from its first dot to its last, so it stops short of the points' icons.
            line.Visible = _ring.Visible && whole && GodotObject.IsInstanceValid(dots[0]) && GodotObject.IsInstanceValid(dots[^1]);
            if (line.Visible) line.Points = [Center(dots[0]), Center(dots[^1])];
        }
        if (!_ring.Visible) return;
        var xf = _point!.GetGlobalTransformWithCanvas();
        float pulse = 1 + 0.1f * Mathf.Sin(Time.GetTicksMsec() / 1000f * Mathf.Tau);
        float radius = (Math.Max(_point.Size.X, _point.Size.Y) / 2 + 14) * xf.Scale.X * pulse;
        _ring.Position = xf * (_point.Size / 2);
        _ring.Points = Enumerable.Range(0, Segments).Select(i => Vector2.FromAngle(i * Mathf.Tau / Segments) * radius).ToArray();
    }

    private void Find(MapRoute? route)
    {
        foreach (var (line, _) in _paths) line.QueueFree();
        _paths.Clear();
        (_route, _point) = (route, null);
        if (route == null || NMapScreen.Instance is not { } screen) return;
        var points = (Dictionary<MapCoord, NMapPoint>)AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary").GetValue(screen)!;
        var paths = (Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>>)AccessTools.Field(typeof(NMapScreen), "_paths").GetValue(screen)!;
        _point = points.GetValueOrDefault(route.Points[0]);
        var stops = route.At is { } at ? route.Points.Prepend(at).ToList() : route.Points;
        foreach (var (from, to) in stops.Zip(stops.Skip(1)))
        {
            if (!paths.TryGetValue((from, to), out var dots) || dots.Count == 0) continue;
            var line = new Line2D { Width = 5, DefaultColor = Purple with { A = 0.8f }, Antialiased = true, Visible = false };
            _layer.AddChild(line);
            _paths.Add((line, dots));
        }
    }

    private static Vector2 Center(Control node) => node.GetGlobalTransformWithCanvas() * (node.Size / 2);
}
