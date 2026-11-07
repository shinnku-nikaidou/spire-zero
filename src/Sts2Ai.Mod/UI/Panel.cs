using System.Globalization;
using Godot;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>How a panel message reads: plain, something the player can fix or wait out, or an error.</summary>
internal enum Tone { Plain, Warn, Error }

/// <summary>What the player asks the AI to do, from the toolbar or its keys (docs: mod/auto §4).</summary>
internal enum Command
{
    /// <summary>F5: play by itself until paused; or pause.</summary>
    Auto,

    /// <summary>F10: play the next action only.</summary>
    Step,

    /// <summary>F11: play to the end of the turn in combat, to the next map decision elsewhere.</summary>
    Finish,
}

/// <summary>What the toolbar and the status strip show (<see cref="Assist"/>).</summary>
/// <param name="Auto">Auto-play is on: the first button pauses it, and the others are off.</param>
/// <param name="Step">The step button's label on this screen; null when there is nothing to play.</param>
/// <param name="Finish">The finish button's label on this screen; null when there is nothing to play.</param>
/// <param name="Pill">The AI's state in a word or two.</param>
/// <param name="Win">V's chance to win the run at the latest macro decision; null before one.</param>
/// <param name="Delta">Its change from the macro decision before.</param>
/// <param name="Recent">What the AI played last, newest first.</param>
internal sealed record Drive(bool Auto, string? Step, string? Finish, string Pill, Tone PillTone, double? Win, double? Delta, IReadOnlyList<string> Recent)
{
    /// <summary>Records are compared by reference for lists: this compares what is shown.</summary>
    public string Key => $"{Auto}|{Pill}|{PillTone}|{Win}|{Delta}|{string.Join("\n", Recent)}";
}

/// <summary>
/// The mod's panel over the game (docs: output §2): a card with the champion, the mod's status and an SL/NoSL switch,
/// the toolbar that lets the AI play (auto, one step, to the end of the turn or the room), then either the brain's
/// answer (the next action, the plan after it, the options it scored with their chances to win) or a message saying
/// why there is none. It also owns the marks the answer draws on the game's own nodes (<see cref="MapMark"/>,
/// <see cref="CombatMark"/>). F8 hides and shows all of it; the settings (<see cref="Settings"/>) pick which parts show.
/// The card's content is built again only when what it shows changes, not every frame.
/// </summary>
internal sealed class Panel
{
    public static readonly Color Accent = new(0.75f, 0.35f, 1f);
    public static readonly Color Ink = Color.FromHtml("#14081f");
    private static readonly Color Fg = Color.FromHtml("#f3effa");
    private static readonly Color Lead = Color.FromHtml("#d9a6ff");
    private static readonly Color Muted = Color.FromHtml("#b5abc8");
    private static readonly Color Rule = Color.FromHtml("#2e2540");
    private static readonly Color Track = Color.FromHtml("#221b30");
    private static readonly Color StripFill = Color.FromHtml("#17111f");
    private static readonly Color Dim = Color.FromHtml("#5a4d72");
    private static readonly Color Warn = Color.FromHtml("#f5b544");
    private static readonly Color Error = Color.FromHtml("#ff8a70");

    private const float BarWidth = 72;

    private readonly PanelContainer _card;
    private readonly StyleBoxFlat _cardStyle;
    private readonly Label _champion;
    private readonly Label _status;
    private readonly (Button Button, PlayEnvironment Env)[] _switch;
    private readonly Control _toolbar;
    private readonly Button _auto, _step, _finish;
    private readonly Control _options;
    private readonly List<(Button Button, AutoSpeed Speed)> _speeds = [];
    private readonly Button _stopElites, _stopDrop;
    private readonly Label _speedLabel, _stopLabel;
    private readonly VBoxContainer _body;
    private readonly MapMark _map;
    private readonly CombatMark _combat;
    private readonly Action<Command>? _pressed;
    private readonly HashSet<Key> _down = [];
    private bool _hidden;
    private object? _built;
    private PlayEnvironment? _styled;
    private Drive? _drive;
    private (bool, AutoSpeed, bool, bool)? _toolStyled;

    /// <param name="switched">What the SL/NoSL switch does; null leaves it out (the mod is off).</param>
    /// <param name="pressed">What the toolbar and its keys do; null leaves the toolbar out (the mod is off).</param>
    public Panel(SceneTree tree, Action<PlayEnvironment>? switched, Action<Command>? pressed)
    {
        _pressed = pressed;
        var layer = new CanvasLayer { Layer = 100 };
        _cardStyle = Box(Colors.Black, 14, Rule);
        _cardStyle.SetContentMarginAll(16);
        _cardStyle.ContentMarginLeft = _cardStyle.ContentMarginRight = 18;
        _cardStyle.ShadowColor = new Color(0, 0, 0, 0.4f);
        _cardStyle.ShadowSize = 12;
        _card = Quiet(new PanelContainer { Position = new Vector2(24, 132), CustomMinimumSize = new Vector2(440, 0) });
        _card.AddThemeStyleboxOverride("panel", _cardStyle);
        var column = Column(14);
        _card.AddChild(column);

        var head = Row(10);
        head.AddChild(Badge("AI", 28, 8, Accent, null, 12));
        // A champion's name can be a whole line of options: it takes the head's free width and is cut there.
        var names = Column(0);
        names.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        names.AddChild(_champion = Clipped(Text("", 16, Fg)));
        names.AddChild(_status = Clipped(Text("", 13, Muted)));
        head.AddChild(names);
        if (switched != null)
        {
            var frame = Frame(Colors.Transparent, Color.FromHtml("#3a3050"), 3);
            var pair = Row(2);
            frame.AddChild(pair);
            _switch = [(Toggle("SL"), PlayEnvironment.SL), (Toggle("NoSL"), PlayEnvironment.NoSL)];
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

        // The toolbar, in the style of a debugger's: continue / pause, step, step out.
        var bar = Frame(Color.FromHtml("#0b0812"), Rule, 4);
        var buttons = Row(2);
        bar.AddChild(buttons);
        _auto = Tool(Command.Auto);
        _step = Tool(Command.Step);
        _finish = Tool(Command.Finish);
        buttons.AddChild(_auto);
        buttons.AddChild(_step);
        buttons.AddChild(_finish);
        _toolbar = bar;
        _toolbar.Visible = false;
        column.AddChild(_toolbar);

        // While auto-play runs: its speed and where it stops.
        var options = Column(6);
        var speed = Row(8);
        speed.AddChild(_speedLabel = Text("", 12, Muted));
        var speeds = Frame(Colors.Transparent, Color.FromHtml("#3a3050"), 3);
        var three = Row(2);
        speeds.AddChild(three);
        foreach (var value in new[] { AutoSpeed.Slow, AutoSpeed.Medium, AutoSpeed.Fast })
        {
            var button = Toggle("");
            button.Pressed += () => Settings.Set(Settings.Current with { Speed = value });
            three.AddChild(button);
            _speeds.Add((button, value));
        }
        speed.AddChild(speeds);
        options.AddChild(speed);
        var stops = Row(8);
        stops.AddChild(_stopLabel = Text("", 12, Muted));
        _stopElites = Toggle("");
        _stopElites.Pressed += () => Settings.Set(Settings.Current with { StopAtElites = !Settings.Current.StopAtElites });
        _stopDrop = Toggle("");
        _stopDrop.Pressed += () => Settings.Set(Settings.Current with { StopOnDrop = !Settings.Current.StopOnDrop });
        stops.AddChild(_stopElites);
        stops.AddChild(_stopDrop);
        options.AddChild(stops);
        _options = options;
        _options.Visible = false;
        column.AddChild(_options);

        _body = Column(10);
        column.AddChild(_body);
        layer.AddChild(_card);
        _map = new MapMark(layer);
        _combat = new CombatMark(layer);
        tree.Root.CallDeferred(Node.MethodName.AddChild, layer);
    }

    /// <summary>Reads the keys: F8 hides and shows, F5, F10 and F11 are the toolbar's; once per frame.</summary>
    public void Poll()
    {
        if (Pressed(Key.F8)) _hidden = !_hidden;
        if (_pressed == null || _drive == null) return;
        if (Pressed(Key.F5)) _pressed(Command.Auto);
        if (Pressed(Key.F10)) _pressed(Command.Step);
        if (Pressed(Key.F11)) _pressed(Command.Finish);
    }

    /// <summary>Whether <paramref name="key"/> went down since the last frame.</summary>
    private bool Pressed(Key key)
    {
        bool down = Input.IsKeyPressed(key);
        bool went = down && !_down.Contains(key);
        if (down) _down.Add(key);
        else _down.Remove(key);
        return went;
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
    /// <param name="drive">The toolbar's state; null leaves the toolbar out (no run to play).</param>
    public void Show(string champion, string status, PlayEnvironment? env, string message, string? sub = null, Tone tone = Tone.Plain, Drive? drive = null)
    {
        Head(champion, status, env, drive);
        Build((message, sub, tone, drive?.Key), () =>
        {
            StatusStrip(drive);
            Message(message, sub, tone);
            Recent(drive);
        });
        Marks(null, null);
    }

    /// <summary>The brain's answer, and the marks it draws on the map or in combat.</summary>
    /// <param name="sub">A line under the next action: how the fight or the run is expected to go after it.</param>
    /// <param name="foot">Which brain answered, and how long it took.</param>
    public void Show(string champion, string status, PlayEnvironment env, Shown answer, string? sub, string foot, MapRoute? route, CombatPlan? plan, Drive? drive)
    {
        Head(champion, status, env, drive);
        Build((answer, sub, foot, drive?.Key), () =>
        {
            StatusStrip(drive);
            Answer(answer, sub, foot);
            Recent(drive);
        });
        Marks(route, plan);
    }

    private void Head(string champion, string status, PlayEnvironment? env, Drive? drive)
    {
        var settings = Settings.Current;
        _card.Visible = !_hidden && settings.Panel;
        var fill = Color.FromHtml("#100c18") with { A = settings.Opacity };
        if (_cardStyle.BgColor != fill) _cardStyle.BgColor = fill;
        _champion.Text = champion;
        _status.Text = status;
        if (env != _styled)
        {
            _styled = env;
            foreach (var (button, which) in _switch) Restyle(button, which == env);
        }
        _drive = drive;
        _toolbar.Visible = drive != null && _pressed != null;
        _options.Visible = drive is { Auto: true };
        if (drive == null) return;
        Set(_auto, drive.Auto ? L10n.T("Pause") : L10n.T("Auto"), drive.Auto ? Icons.Pause : Icons.Play, true);
        // Every frame, so a change of the game's language shows.
        Label(_speedLabel, L10n.T("Speed"));
        Label(_stopLabel, L10n.T("Stop at"));
        foreach (var (button, speed) in _speeds) Label(button, L10n.T(speed.ToString()));
        Label(_stopElites, L10n.T("elites, bosses"));
        Label(_stopDrop, L10n.T("win chance −3"));
        Set(_step, drive.Step ?? L10n.T("Step"), Icons.Step, !drive.Auto && drive.Step != null);
        Set(_finish, drive.Finish ?? L10n.T("Finish room"), Icons.Finish, !drive.Auto && drive.Finish != null);
        // New style boxes only when what they show changed, not every frame.
        var styled = (drive.Auto, settings.Speed, settings.StopAtElites, settings.StopOnDrop);
        if (styled == _toolStyled) return;
        _toolStyled = styled;
        RestyleTool(_auto, drive.Auto);
        foreach (var (button, speed) in _speeds) Restyle(button, settings.Speed == speed);
        Restyle(_stopElites, settings.StopAtElites);
        Restyle(_stopDrop, settings.StopOnDrop);
    }

    private static void Label(Label label, string text)
    {
        if (label.Text != text) label.Text = text;
    }

    private static void Label(Button button, string text)
    {
        if (button.Text != text) button.Text = text;
    }

    private static void Set(Button button, string text, Texture2D icon, bool enabled)
    {
        if (button.Text != text) button.Text = text;
        if (button.Icon != icon) button.Icon = icon;
        if (button.Disabled == enabled) button.Disabled = !enabled;
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
        var key = (what, Settings.Current with { Env = default, Assist = default, Speed = default, StopAtElites = default, StopOnDrop = default },
            L10n.T("Next"));
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

    /// <summary>V's chance to win the run, and the AI's state.</summary>
    private void StatusStrip(Drive? drive)
    {
        if (drive == null) return;
        var strip = Frame(StripFill, null, 10);
        var row = Row(12);
        strip.AddChild(row);
        var left = Column(2);
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (drive.Win is { } win)
        {
            left.AddChild(Text(L10n.T("Win chance").ToUpperInvariant(), 12, Muted));
            var line = Row(8);
            line.AddChild(Text(Percent(win), 26, Fg));
            if (drive.Delta is { } delta && Math.Abs(delta) >= 0.0005)
            {
                var change = Text($"{(delta > 0 ? "▲" : "▼")} {Math.Abs(delta) * 100:0.0}", 13, delta > 0 ? Icons.Green : Error);
                change.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                line.AddChild(change);
            }
            left.AddChild(line);
        }
        row.AddChild(left);
        var pill = Frame(Track, null, 15);
        var inside = Row(7);
        pill.AddChild(inside);
        var dot = Quiet(new PanelContainer { CustomMinimumSize = new Vector2(8, 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        dot.AddThemeStyleboxOverride("panel", Box(drive.PillTone switch { Tone.Warn => Warn, Tone.Error => Error, _ => drive.Auto ? Icons.Green : Warn }, 4));
        inside.AddChild(dot);
        inside.AddChild(Text(drive.Pill, 13, Fg));
        pill.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(pill);
        _body.AddChild(strip);
    }

    private void Recent(Drive? drive)
    {
        if (drive == null || drive.Recent.Count == 0) return;
        _body.AddChild(Text(L10n.T("Recent").ToUpperInvariant(), 12, Muted));
        foreach (var line in drive.Recent) _body.AddChild(Clipped(Text(line, 14, Fg)));
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

    private void Answer(Shown answer, string? sub, string foot)
    {
        _body.AddChild(Text(L10n.T("Next").ToUpperInvariant(), 12, Muted));
        var next = Row(12);
        next.AddChild(answer.Route ? Ring(30) : Badge("1", 32, 16, Accent, null, 16));
        var lines = Column(2);
        lines.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        lines.AddChild(Wrapped(Text(answer.Next, 22, Fg)));
        if (sub != null) lines.AddChild(Wrapped(Text(sub, 13, Muted)));
        next.AddChild(lines);
        _body.AddChild(next);
        if (answer.Route && answer.Then.Count > 0)
        {
            _body.AddChild(Text(L10n.T("Route to the boss").ToUpperInvariant(), 12, Muted));
            _body.AddChild(Wrapped(Text(string.Join("  ·  ", answer.Then), 15, Fg)));
        }
        else
        {
            for (int i = 0; i < answer.Then.Count; i++)
            {
                var step = Row(12);
                step.AddChild(Indent(4));
                step.AddChild(Badge($"{i + 2}", 24, 12, null, Accent, 12));
                step.AddChild(Wrapped(Text(answer.Then[i], 16, Fg)));
                _body.AddChild(step);
            }
        }
        if (answer.Later != null)
        {
            var later = Row(0);
            later.AddChild(Indent(44));
            later.AddChild(Text(answer.Later, 13, Muted));
            _body.AddChild(later);
        }
        var mode = Settings.Current.Considered;
        if (mode != ConsideredMode.Off && answer.Options.Count > 0) Options(answer, mode == ConsideredMode.Bars);
        _body.AddChild(Text(foot, 12, Muted));
    }

    /// <summary>
    /// The options the brain scored, the chosen first: with their chance to win the fight and the HP left (the SL
    /// planner), their chance to win the run and its gap to the best (V; on the map with each option's route), or a
    /// search's score with its confidence interval (PIMC). The bars share one scale.
    /// </summary>
    private void Options(Shown answer, bool bars)
    {
        var options = answer.Options;
        _body.AddChild(Quiet(new ColorRect { Color = Rule, CustomMinimumSize = new Vector2(0, 1) }));
        var (third, fourth) = answer.Kind switch
        {
            ShownKind.Fight => (L10n.T("win"), L10n.T("HP left")),
            ShownKind.Run => (L10n.T("win"), L10n.T("diff")),
            _ => (L10n.T("value ±CI"), ""),
        };
        var grid = Quiet(new GridContainer { Columns = bars ? 4 : 3 });
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 7);
        Cell(grid, L10n.T("Considered").ToUpperInvariant(), 12, Muted, expand: true);
        if (bars) grid.AddChild(Quiet(new Control()));
        Cell(grid, third, 12, Muted, right: true);
        Cell(grid, fourth, 12, Muted, right: true);
        double Shown(Shown.Option o) => answer.Kind == ShownKind.Fight ? o.Win ?? 0 : o.Value;
        double best = options.Max(Shown);
        double lo = options.Min(o => Shown(o) - o.Ci), hi = options.Max(o => Shown(o) + o.Ci);
        double pad = Math.Max((hi - lo) * 0.15, answer.Kind == ShownKind.Score ? 1e-6 : 0.005);
        (lo, hi) = (lo - pad, answer.Kind == ShownKind.Fight ? Math.Max(hi, 1) : hi + pad);
        float At(double v) => (float)Math.Clamp((v - lo) / (hi - lo) * BarWidth, 0, BarWidth);
        for (int i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var color = i == 0 ? Lead : Fg;
            Cell(grid, o.Name, 15, color, expand: true);
            if (bars)
            {
                var bar = Quiet(new Control { CustomMinimumSize = new Vector2(BarWidth, 16) });
                bar.AddChild(Strip(0, BarWidth, 3, 10, Track));
                double v = Shown(o);
                bar.AddChild(Strip(At(v - o.Ci), Math.Max(At(v + o.Ci) - At(v - o.Ci), 4), 3, 10, i == 0 ? Accent : Dim));
                if (answer.Kind == ShownKind.Score)
                    bar.AddChild(Quiet(new ColorRect { Color = Fg, Position = new Vector2(At(v) - 1, 0), Size = new Vector2(2, 16) }));
                grid.AddChild(bar);
            }
            switch (answer.Kind)
            {
                case ShownKind.Fight:
                    Cell(grid, Percent(o.Win ?? 0, whole: true), 15, color, right: true);
                    Cell(grid, o.Win > 0 && o.Hp is { } hp ? $"{hp:0}" : "—", 15, color, right: true);
                    break;
                case ShownKind.Run:
                    Cell(grid, Percent(o.Value), 15, color, right: true);
                    Cell(grid, i == 0 || o.Value >= best ? "—" : (o.Value - best >= 0 ? "+" : "−") + $"{Math.Abs(o.Value - best) * 100:0.0}", 15, Muted, right: true);
                    break;
                default:
                    Cell(grid, $"{o.Value:0.000} ±{o.Ci:0.000}", 15, color, right: true);
                    Cell(grid, "", 15, color, right: true);
                    break;
            }
        }
        _body.AddChild(grid);
        // On the map: each option's route, as chips of its rooms.
        var routes = options.Where(o => o.Rooms is { Count: > 0 }).ToList();
        if (routes.Count == 0) return;
        foreach (var o in routes)
        {
            var line = Row(4);
            var name = Clipped(Text(o.Name, 12, Muted));
            name.CustomMinimumSize = new Vector2(96, 0);
            line.AddChild(name);
            foreach (var room in o.Rooms!) line.AddChild(Chip(room));
            _body.AddChild(line);
        }
        var legend = Quiet(new HFlowContainer());
        legend.AddThemeConstantOverride("h_separation", 12);
        legend.AddThemeConstantOverride("v_separation", 4);
        foreach (var type in routes.SelectMany(o => o.Rooms!).Distinct())
        {
            var item = Row(4);
            item.AddChild(Chip(type));
            item.AddChild(Text(Describe.Legend(type), 12, Muted));
            legend.AddChild(item);
        }
        _body.AddChild(legend);
    }

    /// <summary>A room of a route: a small square in its type's color, with its legend's first letter.</summary>
    private static Control Chip(string? type)
    {
        var color = type switch
        {
            "Elite" => Warn,
            "RestSite" => Icons.Green,
            "Shop" => Icons.Blue,
            "Treasure" => Color.FromHtml("#e8c56a"),
            "Boss" => Error,
            "Unknown" => Muted,
            _ => Dim,
        };
        string legend = Describe.Legend(type);
        string letter = type == "Unknown" ? "?" : StringInfo.GetNextTextElement(legend);
        var chip = Quiet(new PanelContainer { CustomMinimumSize = new Vector2(22, 22), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        chip.AddThemeStyleboxOverride("panel", Box(type == "Boss" ? Error : Colors.Transparent, 5, color, 2));
        var label = Text(letter.ToUpperInvariant(), 11, type == "Boss" ? Ink : type == "Monster" ? Fg : color);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        chip.AddChild(label);
        return chip;
    }

    private static string Percent(double p, bool whole = false) =>
        (p * 100).ToString(whole ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";

    private static void Cell(GridContainer grid, string text, int size, Color color, bool expand = false, bool right = false)
    {
        var label = expand ? Clipped(Text(text, size, color)) : Text(text, size, color);
        if (expand) label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (right)
        {
            label.HorizontalAlignment = HorizontalAlignment.Right;
            label.CustomMinimumSize = new Vector2(56, 0);
        }
        grid.AddChild(label);
    }

    private static Control Strip(float x, float width, float y, float height, Color color)
    {
        var strip = Quiet(new Godot.Panel { Position = new Vector2(x, y), Size = new Vector2(width, height) });
        strip.AddThemeStyleboxOverride("panel", Box(color, (int)(height / 2)));
        return strip;
    }

    /// <summary>A container with a filled, rounded box behind its content.</summary>
    private static PanelContainer Frame(Color fill, Color? border, int margin)
    {
        var frame = Quiet(new PanelContainer());
        var style = Box(fill, 10, border);
        style.SetContentMarginAll(margin);
        if (margin >= 10) style.ContentMarginLeft = style.ContentMarginRight = 12;
        frame.AddThemeStyleboxOverride("panel", style);
        return frame;
    }

    /// <summary>A toolbar button: an icon and a word, filling its share of the bar.</summary>
    private Button Tool(Command command)
    {
        var button = new Button { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 44), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        button.AddThemeFontSizeOverride("font_size", 14);
        button.AddThemeConstantOverride("h_separation", 7);
        button.Pressed += () => _pressed?.Invoke(command);
        RestyleTool(button, false);
        return button;
    }

    private static void RestyleTool(Button button, bool on)
    {
        var fill = Box(on ? Color.FromHtml("#2a2238") : Colors.Transparent, 7);
        fill.ContentMarginLeft = fill.ContentMarginRight = 8;
        var hover = Box(Color.FromHtml("#2a2238"), 7);
        hover.ContentMarginLeft = hover.ContentMarginRight = 8;
        foreach (var state in new[] { "normal", "pressed", "focus", "disabled" }) button.AddThemeStyleboxOverride(state, fill);
        button.AddThemeStyleboxOverride("hover", hover);
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" }) button.AddThemeColorOverride(state, Fg);
        button.AddThemeColorOverride("font_disabled_color", Dim);
        button.AddThemeColorOverride("icon_disabled_color", new Color(1, 1, 1, 0.35f));
    }

    private static Button Toggle(string text)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 32) };
        button.AddThemeFontSizeOverride("font_size", 13);
        Restyle(button, false);
        return button;
    }

    private static void Restyle(Button button, bool on)
    {
        var fill = Box(on ? Accent : Colors.Transparent, 7);
        fill.ContentMarginLeft = fill.ContentMarginRight = 12;
        var hover = Box(on ? Accent : Color.FromHtml("#2a2238"), 7);
        hover.ContentMarginLeft = hover.ContentMarginRight = 12;
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

    /// <summary>One line, cut with an ellipsis where its space ends, instead of widening the card.</summary>
    private static Label Clipped(Label label)
    {
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
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

    /// <summary>The panel takes no clicks but its buttons': the game under it gets them.</summary>
    private static T Quiet<T>(T control) where T : Control
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        return control;
    }
}
