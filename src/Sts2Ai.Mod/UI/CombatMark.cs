using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod.UI;

/// <summary>A combat answer's steps up to this turn's end, in order; null for any other answer.</summary>
internal sealed record CombatPlan(IReadOnlyList<CombatAction> Steps)
{
    public static CombatPlan? Of(Recommendation answer, Observation obs)
    {
        if (obs.Screen is not CombatScreen) return null;
        var steps = new List<CombatAction>();
        foreach (var text in Describe.Turn(answer.Action, answer.Plan ?? []).Turn)
            if (CombatAction.TryParse(text, out var step)) steps.Add(step);
        return new(steps);
    }
}

/// <summary>
/// A combat plan's marks (docs: output §3): each card the plan plays this turn is outlined in purple with its step's
/// number, each target is ringed with the numbers of the steps aimed at it, and the end turn button is ringed with its
/// step's number. The marks live in the mod's layer and follow the game's nodes on screen every frame, since cards
/// move, fan out and are pooled; a card or creature that is gone has no mark. The settings pick which marks show.
/// </summary>
internal sealed class CombatMark(CanvasLayer layer)
{
    private const float Pad = 10;

    private readonly List<Line2D> _lines = [];
    private readonly List<(PanelContainer Box, Label Label)> _badges = [];
    private int _line;
    private int _badge;

    /// <summary>Marks a plan, or nothing for null; once per frame.</summary>
    public void Show(CombatPlan? plan, bool visible)
    {
        (_line, _badge) = (0, 0);
        var settings = Settings.Current;
        var st = CombatManager.Instance.IsInProgress ? CombatManager.Instance.DebugOnlyGetState() : null;
        if (visible && plan != null && st != null && NCombatRoom.Instance is { } room)
        {
            var targets = new Dictionary<uint, List<int>>();
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                if (step.Target is uint target)
                {
                    if (!targets.TryGetValue(target, out var steps)) targets[target] = steps = [];
                    steps.Add(i + 1);
                }
                if (step.Kind == CombatActionKind.PlayCard && settings.Numbers && Card(step.Card!.Value) is { } card)
                {
                    var corners = Corners(card, 0);
                    Outline(corners, 4);
                    Badge($"{i + 1}", corners[1], 34);
                }
                if (step.Kind == CombatActionKind.EndTurn && settings.Marks && room.Ui.EndTurnButton is { } end && end.IsVisibleInTree())
                {
                    var corners = Corners(end, Pad);
                    Outline(corners, 3);
                    Badge($"{i + 1}", corners[1], 34);
                }
            }
            if (settings.Marks)
                foreach (var (uid, steps) in targets)
                {
                    var creature = st.Enemies.FirstOrDefault(c => c.CombatId == uid) ?? st.Allies.FirstOrDefault(c => c.CombatId == uid);
                    if (creature == null || room.GetCreatureNode(creature)?.Hitbox is not { } hitbox || !hitbox.IsVisibleInTree()) continue;
                    var corners = Corners(hitbox, Pad);
                    Outline(corners, 3);
                    Badge(string.Join(" ", steps), (corners[0] + corners[1]) / 2, 30);
                }
        }
        for (int i = _line; i < _lines.Count; i++) _lines[i].Visible = false;
        for (int i = _badge; i < _badges.Count; i++) _badges[i].Box.Visible = false;
    }

    /// <summary>The card's hitbox in hand: it turns and moves with the card, as the card's own node may not.</summary>
    private static NClickableControl? Card(uint uid)
    {
        if (!NetCombatCardDb.Instance.TryGetCard(uid, out var card) || card == null) return null;
        var hitbox = NPlayerHand.Instance?.GetCardHolder(card)?.Hitbox;
        return hitbox != null && hitbox.IsVisibleInTree() ? hitbox : null;
    }

    /// <summary>The node's corners on screen, clockwise from the top left, <paramref name="pad"/> outside it.</summary>
    private static Vector2[] Corners(Control node, float pad)
    {
        var xf = node.GetGlobalTransformWithCanvas();
        var size = node.Size;
        // The pad is in screen pixels, so it is undone from the node's scale.
        var p = new Vector2(pad / Math.Max(xf.Scale.X, 0.01f), pad / Math.Max(xf.Scale.Y, 0.01f));
        return [xf * -p, xf * new Vector2(size.X + p.X, -p.Y), xf * (size + p), xf * new Vector2(-p.X, size.Y + p.Y)];
    }

    private void Outline(Vector2[] corners, float width)
    {
        if (_line == _lines.Count)
        {
            var line = new Line2D { DefaultColor = Panel.Accent, Closed = true, Antialiased = true, JointMode = Line2D.LineJointMode.Round };
            layer.AddChild(line);
            _lines.Add(line);
        }
        var mark = _lines[_line++];
        mark.Width = width;
        mark.Points = corners;
        mark.Visible = true;
    }

    /// <summary>A purple badge centred on <paramref name="at"/>.</summary>
    private void Badge(string text, Vector2 at, int height)
    {
        if (_badge == _badges.Count)
        {
            var frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            var style = Panel.Box(Panel.Accent, 17, Color.FromHtml("#100c18"), 3);
            style.ContentMarginLeft = style.ContentMarginRight = 8;
            frame.AddThemeStyleboxOverride("panel", style);
            var caption = Panel.Text("", 17, Panel.Ink);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.VerticalAlignment = VerticalAlignment.Center;
            frame.AddChild(caption);
            layer.AddChild(frame);
            _badges.Add((frame, caption));
        }
        var (box, label) = _badges[_badge++];
        if (box.Theme != Fonts.Theme) box.Theme = Fonts.Theme;
        label.Text = text;
        box.CustomMinimumSize = new Vector2(height, height);
        box.Size = box.GetCombinedMinimumSize();
        box.Position = at - box.Size / 2;
        box.Visible = true;
    }
}
