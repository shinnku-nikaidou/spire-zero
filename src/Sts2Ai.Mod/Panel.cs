using Godot;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>How a panel message reads: plain, something the player can fix or wait out, or an error.</summary>
internal enum Tone { Plain, Warn, Error }

/// <summary>
/// The mod's panel over the game (docs: output §2): a card with the champion, the mod's status and an SL/NoSL switch,
/// then either the brain's answer (the next action, the plan after it, the actions it scored) or a message saying why
/// there is none. It also owns the marks the answer draws on the game's own nodes (<see cref="MapMark"/>,
/// <see cref="CombatMark"/>). F8 hides and shows all of it; the settings (<see cref="Settings"/>) pick which parts show.
/// The card's content is built again only when what it shows changes, not every frame.
/// </summary>
internal sealed class Panel
{
    public static readonly Color Accent = new(0.75f, 0.35f, 1f);
    public static readonly Color Ink = Color.FromHtml("#14081f");
    private static readonly Color Fg = Color.FromHtml("#f3effa");
    private static readonly Color Muted = Color.FromHtml("#b5abc8");
    private static readonly Color Rule = Color.FromHtml("#2e2540");
    private static readonly Color Track = Color.FromHtml("#221b30");
    private static readonly Color Dim = Color.FromHtml("#5a4d72");
    private static readonly Color Warn = Color.FromHtml("#f5b544");
    private static readonly Color Error = Color.FromHtml("#ff8a70");

    private const float BarWidth = 96;

    private readonly PanelContainer _card;
    private readonly StyleBoxFlat _cardStyle;
    private readonly Label _champion;
    private readonly Label _status;
    private readonly (Button Button, PlayEnvironment Env)[] _switch;
    private readonly VBoxContainer _body;
    private readonly MapMark _map;
    private readonly CombatMark _combat;
    private bool _f8;
    private bool _hidden;
    private object? _built;
    private PlayEnvironment? _styled;

    /// <param name="switched">What the SL/NoSL switch does; null leaves it out (the mod is off).</param>
    public Panel(SceneTree tree, Action<PlayEnvironment>? switched)
    {
        var layer = new CanvasLayer { Layer = 100 };
        _cardStyle = Box(Colors.Black, 14, Rule);
        _cardStyle.SetContentMarginAll(16);
        _cardStyle.ContentMarginLeft = _cardStyle.ContentMarginRight = 18;
        _cardStyle.ShadowColor = new Color(0, 0, 0, 0.4f);
        _cardStyle.ShadowSize = 12;
        _card = Quiet(new PanelContainer { Position = new Vector2(24, 132), CustomMinimumSize = new Vector2(420, 0) });
        _card.AddThemeStyleboxOverride("panel", _cardStyle);
        var column = Column(14);
        _card.AddChild(column);

        var head = Row(10);
        head.AddChild(Badge("AI", 28, 8, Accent, null, 12));
        var names = Column(0);
        names.AddChild(_champion = Text("", 17, Fg));
        names.AddChild(_status = Text("", 13, Muted));
        head.AddChild(names);
        head.AddChild(Quiet(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }));
        if (switched != null)
        {
            var frame = Quiet(new PanelContainer());
            var frameStyle = Box(Colors.Transparent, 10, Color.FromHtml("#3a3050"));
            frameStyle.SetContentMarginAll(3);
            frame.AddThemeStyleboxOverride("panel", frameStyle);
            var pair = Row(2);
            frame.AddChild(pair);
            _switch = [(Switch("SL"), PlayEnvironment.SL), (Switch("NoSL"), PlayEnvironment.NoSL)];
            foreach (var (button, env) in _switch)
            {
                button.Pressed += () => switched(env);
                pair.AddChild(button);
            }
            head.AddChild(frame);
        }
        else _switch = [];
        head.AddChild(Text("F8", 12, Muted));
        column.AddChild(head);

        _body = Column(10);
        column.AddChild(_body);
        layer.AddChild(_card);
        _map = new MapMark(layer);
        _combat = new CombatMark(layer);
        tree.Root.CallDeferred(Node.MethodName.AddChild, layer);
    }

    /// <summary>Reads the toggle key; once per frame.</summary>
    public void Poll()
    {
        bool down = Input.IsKeyPressed(Key.F8);
        if (down && !_f8) _hidden = !_hidden;
        _f8 = down;
    }

    /// <summary>Nothing over the game: the player turned assist off.</summary>
    public void Hide()
    {
        _card.Visible = false;
        _map.Show(null, false);
        _combat.Show(null, false);
    }

    /// <summary>A message instead of an answer.</summary>
    /// <param name="sub">A second, quieter line: the reason, or what happens next.</param>
    public void Show(string champion, string status, PlayEnvironment? env, string message, string? sub = null, Tone tone = Tone.Plain)
    {
        Head(champion, status, env);
        Build((message, sub, tone), () => Message(message, sub, tone));
        Marks(null, null);
    }

    /// <summary>The brain's answer, and the marks it draws on the map or in combat.</summary>
    /// <param name="foot">Which brain answered, and how long it took.</param>
    public void Show(string champion, string status, PlayEnvironment env, Shown answer, string foot, MapRoute? route, CombatPlan? plan)
    {
        Head(champion, status, env);
        Build((answer, foot), () => Answer(answer, foot));
        Marks(route, plan);
    }

    private void Head(string champion, string status, PlayEnvironment? env)
    {
        var settings = Settings.Current;
        _card.Visible = !_hidden && settings.Panel;
        var fill = Color.FromHtml("#100c18") with { A = settings.Opacity };
        if (_cardStyle.BgColor != fill) _cardStyle.BgColor = fill;
        _champion.Text = champion;
        _status.Text = status;
        if (env == _styled) return;
        _styled = env;
        foreach (var (button, which) in _switch) Restyle(button, which == env);
    }

    private void Marks(MapRoute? route, CombatPlan? plan)
    {
        var settings = Settings.Current;
        _map.Show(settings.Route == RouteMode.Off ? null : route, !_hidden, settings.Route == RouteMode.Whole);
        _combat.Show(plan, !_hidden);
    }

    /// <summary>Builds the body again when what it shows, or the settings it is drawn with, changed.</summary>
    private void Build(object what, Action build)
    {
        var key = (what, Settings.Current with { Env = default, Assist = default });
        if (Equals(key, _built)) return;
        _built = key;
        foreach (var child in _body.GetChildren())
        {
            // Out of the body now, so the old content is never laid out beside the new.
            _body.RemoveChild(child);
            child.QueueFree();
        }
        build();
        // A control outside a container only grows to fit its content; the card shrinks back to it here.
        _card.CallDeferred(Control.MethodName.ResetSize);
    }

    private void Message(string message, string? sub, Tone tone)
    {
        var color = tone switch { Tone.Warn => Warn, Tone.Error => Error, _ => Fg };
        var row = Row(10);
        var dot = Quiet(new PanelContainer { CustomMinimumSize = new Vector2(10, 10), SizeFlagsVertical = Control.SizeFlags.ShrinkBegin });
        dot.AddThemeStyleboxOverride("panel", Box(tone == Tone.Plain ? Accent : color, 5));
        var margin = Quiet(new MarginContainer());
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddChild(dot);
        row.AddChild(margin);
        var lines = Column(4);
        lines.AddChild(Wrapped(Text(message, 19, color)));
        if (sub != null) lines.AddChild(Wrapped(Text(sub, 14, Muted)));
        row.AddChild(lines);
        _body.AddChild(row);
    }

    private void Answer(Shown answer, string foot)
    {
        var next = Row(12);
        next.AddChild(answer.Route ? Ring(32) : Badge("1", 32, 16, Accent, null, 16));
        next.AddChild(Wrapped(Text(answer.Next, 24, Fg)));
        _body.AddChild(next);
        if (answer.Route && answer.Then.Count > 0)
        {
            _body.AddChild(Text("ROUTE TO THE BOSS", 12, Muted));
            _body.AddChild(Wrapped(Text(string.Join("  ·  ", answer.Then), 15, Fg)));
        }
        else
        {
            for (int i = 0; i < answer.Then.Count; i++)
            {
                var step = Row(12);
                step.AddChild(Indent(4));
                step.AddChild(Badge($"{i + 2}", 24, 12, null, Accent, 12));
                step.AddChild(Wrapped(Text(answer.Then[i], 17, Fg)));
                _body.AddChild(step);
            }
        }
        if (answer.Later != null)
        {
            var later = Row(0);
            later.AddChild(Indent(44));
            later.AddChild(Text(answer.Later, 14, Muted));
            _body.AddChild(later);
        }
        var mode = Settings.Current.Considered;
        if (mode != ConsideredMode.Off && answer.Options.Count > 0) Options(answer.Options, mode == ConsideredMode.Bars);
        _body.AddChild(Text(foot, 13, Muted));
    }

    /// <summary>The actions the brain scored, with each value's confidence interval as a bar on one shared scale.</summary>
    private void Options(IReadOnlyList<Shown.Option> options, bool bars)
    {
        _body.AddChild(Quiet(new ColorRect { Color = Rule, CustomMinimumSize = new Vector2(0, 1) }));
        var head = Row(0);
        head.AddChild(Text("CONSIDERED", 12, Muted));
        head.AddChild(Quiet(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }));
        head.AddChild(Text("value ±CI", 12, Muted));
        _body.AddChild(head);
        var grid = Quiet(new GridContainer { Columns = bars ? 3 : 2 });
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        double lo = options.Min(o => o.Value - o.Ci), hi = options.Max(o => o.Value + o.Ci);
        double pad = Math.Max((hi - lo) * 0.1, 1e-6);
        (lo, hi) = (lo - pad, hi + pad);
        float At(double v) => (float)((v - lo) / (hi - lo) * BarWidth);
        for (int i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var color = i == 0 ? Accent : Fg;
            var name = Text(o.Name, 15, color);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            name.ClipText = true;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            grid.AddChild(name);
            var value = Text($"{o.Value:0.0} ±{o.Ci:0.0}", 15, color);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.CustomMinimumSize = new Vector2(92, 0);
            grid.AddChild(value);
            if (!bars) continue;
            var bar = Quiet(new Control { CustomMinimumSize = new Vector2(BarWidth, 16) });
            bar.AddChild(Strip(0, BarWidth, 3, 10, Track));
            bar.AddChild(Strip(At(o.Value - o.Ci), Math.Max(At(o.Value + o.Ci) - At(o.Value - o.Ci), 3), 3, 10, i == 0 ? Accent : Dim));
            bar.AddChild(Quiet(new ColorRect { Color = Fg, Position = new Vector2(At(o.Value) - 1, 0), Size = new Vector2(2, 16) }));
            grid.AddChild(bar);
        }
        _body.AddChild(grid);
    }

    private static Control Strip(float x, float width, float y, float height, Color color)
    {
        var strip = Quiet(new Godot.Panel { Position = new Vector2(x, y), Size = new Vector2(width, height) });
        strip.AddThemeStyleboxOverride("panel", Box(color, (int)(height / 2)));
        return strip;
    }

    private static Button Switch(string text)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 34) };
        button.AddThemeFontSizeOverride("font_size", 14);
        return button;
    }

    private static void Restyle(Button button, bool on)
    {
        var fill = Box(on ? Accent : Colors.Transparent, 7);
        fill.ContentMarginLeft = fill.ContentMarginRight = 14;
        var hover = Box(on ? Accent : Color.FromHtml("#2a2238"), 7);
        hover.ContentMarginLeft = hover.ContentMarginRight = 14;
        foreach (var state in new[] { "normal", "pressed", "focus" }) button.AddThemeStyleboxOverride(state, fill);
        button.AddThemeStyleboxOverride("hover", hover);
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            button.AddThemeColorOverride(state, on ? Ink : Muted);
    }

    /// <summary>A round badge with a number or a word: filled with <paramref name="fill"/>, or drawn as a ring of <paramref name="ring"/>.</summary>
    private static Control Badge(string text, int size, int radius, Color? fill, Color? ring, int fontSize)
    {
        var badge = Quiet(new PanelContainer { CustomMinimumSize = new Vector2(size, size), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        badge.AddThemeStyleboxOverride("panel", Box(fill ?? Colors.Transparent, radius, ring, 2));
        var label = Text(text, fontSize, fill != null ? Ink : Fg);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        badge.AddChild(label);
        return badge;
    }

    /// <summary>A small copy of the map's ring, for a map answer.</summary>
    private static Control Ring(int size)
    {
        var ring = Quiet(new PanelContainer { CustomMinimumSize = new Vector2(size, size), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        ring.AddThemeStyleboxOverride("panel", Box(Colors.Transparent, size / 2, Accent, 4));
        return ring;
    }

    public static StyleBoxFlat Box(Color fill, int radius, Color? border = null, int width = 1)
    {
        var box = new StyleBoxFlat { BgColor = fill, AntiAliasing = true };
        box.SetCornerRadiusAll(radius);
        if (border is { } color)
        {
            box.BorderColor = color;
            box.SetBorderWidthAll(width);
        }
        return box;
    }

    public static Label Text(string text, int size, Color color)
    {
        var label = Quiet(new Label { Text = text });
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static Label Wrapped(Label label)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(200, 0);
        return label;
    }

    private static Control Indent(int width) => Quiet(new Control { CustomMinimumSize = new Vector2(width, 0) });

    private static VBoxContainer Column(int gap)
    {
        var box = Quiet(new VBoxContainer());
        box.AddThemeConstantOverride("separation", gap);
        return box;
    }

    private static HBoxContainer Row(int gap)
    {
        var box = Quiet(new HBoxContainer());
        box.AddThemeConstantOverride("separation", gap);
        return box;
    }

    /// <summary>The panel takes no clicks but the switch's: the game under it gets them.</summary>
    private static T Quiet<T>(T control) where T : Control
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        return control;
    }
}
