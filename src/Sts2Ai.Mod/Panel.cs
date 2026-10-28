using Godot;

namespace Sts2Ai.Mod;

/// <summary>
/// A few lines of text over the game: the mod's status and the brain's answer, and the answer's route on the map. Above
/// them a box chooses SL or NoSL assist. F8 shows or hides it.
/// </summary>
internal sealed class Panel
{
    private readonly VBoxContainer _box;
    private readonly Label _label;
    private readonly MapMark _mark;
    private bool _f8;

    /// <param name="sl">Whether the SL box starts ticked.</param>
    /// <param name="toggled">What ticking the box does; null leaves the box out (the mod is off).</param>
    public Panel(SceneTree tree, bool sl, Action<bool>? toggled)
    {
        var layer = new CanvasLayer { Layer = 100 };
        _box = new VBoxContainer { Position = new Vector2(24, 132), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (toggled != null)
        {
            // The game's theme draws the box faint, so the text says which is on.
            var box = new CheckBox
            {
                Text = Choice(sl),
                ButtonPressed = sl,
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            };
            Outline(box);
            box.Toggled += on =>
            {
                box.Text = Choice(on);
                toggled(on);
            };
            _box.AddChild(box);
        }
        _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        Outline(_label);
        _box.AddChild(_label);
        layer.AddChild(_box);
        _mark = new MapMark(layer);
        tree.Root.CallDeferred(Node.MethodName.AddChild, layer);
    }

    private static string Choice(bool sl) => sl ? "[x] SL assist" : "[ ] SL assist (now NoSL)";

    private static void Outline(Control control)
    {
        control.AddThemeFontSizeOverride("font_size", 22);
        control.AddThemeColorOverride("font_outline_color", Colors.Black);
        control.AddThemeConstantOverride("outline_size", 8);
    }

    /// <summary>Reads the toggle key; once per frame.</summary>
    public void Poll()
    {
        bool down = Input.IsKeyPressed(Key.F8);
        if (down && !_f8) _box.Visible = !_box.Visible;
        _f8 = down;
    }

    public void Show(string status, params string[] lines) => Show(status, lines, null);

    /// <param name="route">The answer's route, when it goes somewhere on the map.</param>
    public void Show(string status, string[] lines, MapRoute? route)
    {
        _label.Text = string.Join("\n", lines.Prepend($"AI · {status}"));
        _mark.Show(route, _box.Visible);
    }
}
