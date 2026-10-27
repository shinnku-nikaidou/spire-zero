using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using Sts2Ai.GameBridge;

namespace Sts2Ai.Mod;

/// <summary>
/// A pulsing purple ring on one point of the map (docs: output §3): it lives in the mod's layer and follows the
/// point's node on screen every frame, since the map scrolls. The map screen keeps its point nodes in a private
/// dictionary; a new act replaces them.
/// </summary>
internal sealed class MapMark
{
    private const int Segments = 48;

    private readonly Line2D _ring = new() { Width = 7, DefaultColor = new Color(0.75f, 0.35f, 1f), Closed = true, Antialiased = true, Visible = false };
    private string? _action;
    private NMapPoint? _point;

    public MapMark(CanvasLayer layer) => layer.AddChild(_ring);

    /// <summary>Rings the point a map action ("map:COL,ROW") goes to, or nothing for null; once per frame.</summary>
    public void Show(string? action, bool visible)
    {
        if (action != _action || _point != null && !GodotObject.IsInstanceValid(_point))
            (_action, _point) = (action, action != null ? Node(action) : null);
        _ring.Visible = visible && _point != null && _point.IsVisibleInTree();
        if (!_ring.Visible) return;
        var xf = _point!.GetGlobalTransformWithCanvas();
        float pulse = 1 + 0.1f * Mathf.Sin(Time.GetTicksMsec() / 1000f * Mathf.Tau);
        float radius = (Math.Max(_point.Size.X, _point.Size.Y) / 2 + 14) * xf.Scale.X * pulse;
        _ring.Position = xf * (_point.Size / 2);
        _ring.Points = Enumerable.Range(0, Segments).Select(i => Vector2.FromAngle(i * Mathf.Tau / Segments) * radius).ToArray();
    }

    private static NMapPoint? Node(string action) =>
        NMapScreen.Instance is { } screen
        && AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary").GetValue(screen) is Dictionary<MapCoord, NMapPoint> points
            ? points.FirstOrDefault(p => MapActions.Text(p.Key) == action).Value
            : null;
}
