using Godot;

namespace Sts2Ai.Mod;

/// <summary>A few lines of text over the game: the mod's status and the brain's answer. F8 shows or hides it.</summary>
internal sealed class Panel
{
    private readonly Label _label;
    private bool _f8;

    public Panel(SceneTree tree)
    {
        var layer = new CanvasLayer { Layer = 100 };
        _label = new Label { Position = new Vector2(24, 140), MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeFontSizeOverride("font_size", 22);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 8);
        layer.AddChild(_label);
        tree.Root.CallDeferred(Node.MethodName.AddChild, layer);
    }

    /// <summary>Reads the toggle key; once per frame.</summary>
    public void Poll()
    {
        bool down = Input.IsKeyPressed(Key.F8);
        if (down && !_f8) _label.Visible = !_label.Visible;
        _f8 = down;
    }

    public void Show(string status, params string[] lines) =>
        _label.Text = string.Join("\n", lines.Prepend($"AI · {status}"));
}
