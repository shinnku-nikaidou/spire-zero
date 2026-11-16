using Godot;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// A notice over the game until the player closes it: a title, a text, a line to copy (selectable, with a button that
/// puts it on the clipboard), in the game's font for its language. Above the panel, and kept across the game's scenes,
/// as the panel is.
/// </summary>
internal sealed class Notice
{
    private static readonly Color Fg = Color.FromHtml("#f3effa");
    private static readonly Color Lead = Color.FromHtml("#d9a6ff");
    private static readonly Color Fill = Color.FromHtml("#2a2238");

    private readonly CanvasLayer _layer = new() { Layer = 101 };

    public Notice(SceneTree tree, string title, string text, string copy)
    {
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var card = new PanelContainer { CustomMinimumSize = new Vector2(640, 0), Theme = Fonts.Theme };
        var style = Box(Color.FromHtml("#100c18"), 14, Panel.Accent, 2);
        style.SetContentMarginAll(26);
        card.AddThemeStyleboxOverride("panel", style);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 16);

        column.AddChild(Text(title, 26, Lead));
        var body = Text(text, 18, Fg);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(body);

        var line = new LineEdit { Text = copy, Editable = false, SelectingEnabled = true, ContextMenuEnabled = false };
        line.AddThemeFontSizeOverride("font_size", 18);
        line.AddThemeColorOverride("font_uneditable_color", Fg);
        line.AddThemeStyleboxOverride("read_only", Box(Color.FromHtml("#0b0812"), 6, Color.FromHtml("#3a3050"), 1, 10));
        column.AddChild(line);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 10);
        var copyButton = Button(L10n.T("Copy"), false);
        copyButton.Pressed += () =>
        {
            DisplayServer.ClipboardSet(copy);
            copyButton.Text = L10n.T("Copied");
        };
        var close = Button(L10n.T("OK"), true);
        close.Pressed += () => _layer.QueueFree();
        buttons.AddChild(copyButton);
        buttons.AddChild(close);
        column.AddChild(buttons);

        card.AddChild(column);
        center.AddChild(card);
        _layer.AddChild(shade);
        _layer.AddChild(center);
        tree.Root.CallDeferred(Node.MethodName.AddChild, _layer);
    }

    private static Label Text(string text, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static Button Button(string text, bool primary)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(120, 0) };
        button.AddThemeFontSizeOverride("font_size", 18);
        button.AddThemeColorOverride("font_color", primary ? Panel.Ink : Fg);
        button.AddThemeColorOverride("font_hover_color", primary ? Panel.Ink : Fg);
        button.AddThemeColorOverride("font_pressed_color", primary ? Panel.Ink : Fg);
        var normal = primary ? Box(Panel.Accent, 8, null, 0, 10) : Box(Fill, 8, Color.FromHtml("#3a3050"), 1, 10);
        var hover = primary ? Box(Lead, 8, null, 0, 10) : Box(Color.FromHtml("#3a3050"), 8, Color.FromHtml("#5a4d72"), 1, 10);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", hover);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        return button;
    }

    private static StyleBoxFlat Box(Color fill, int radius, Color? border = null, int borderWidth = 0, int margin = 0)
    {
        var box = new StyleBoxFlat { BgColor = fill };
        box.SetCornerRadiusAll(radius);
        if (border is { } b)
        {
            box.BorderColor = b;
            box.SetBorderWidthAll(borderWidth);
        }
        if (margin > 0)
        {
            box.SetContentMarginAll(margin / 2f);
            box.ContentMarginLeft = box.ContentMarginRight = margin;
        }
        return box;
    }
}
