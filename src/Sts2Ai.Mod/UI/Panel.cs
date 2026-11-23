using System.Globalization;
using Godot;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod.UI;

/// <summary>How a panel message reads: plain, something the player can fix or wait out, or an error.</summary>
internal enum Tone { Plain, Warn, Error }

/// <summary>What the player asks the AI to do, from the toolbar or its keys.</summary>
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
/// The mod's panel: a card with the champion, the mod's status and an SL/NoSL switch,
/// the toolbar that lets the AI play (auto, one step, to the end of the turn or the room) and the SL planner's effort,
/// then either the brain's
/// answer (the next action, the plan after it, the options it scored with their chances to win) or a message saying
/// why there is none. It also owns the marks the answer draws on the game's own nodes (<see cref="MapMark"/>,
/// <see cref="CombatMark"/>). F8 hides and shows all of it over the game; the settings (<see cref="Settings"/>) pick
/// which parts show.
/// The panel is over the game, or in its own desktop window (<see cref="DesktopWindow"/>, <see cref="Settings.InGame"/>),
/// in two columns there, the toolbar's on the left, the answer and the fight's details on the right; then the game
/// keeps only the marks and the orb, or nothing.
/// Over the game a small orb opens and closes the card, which opens under it, and the marks with it; the orb or the
/// card's head drags both (kept where they were let go), and a button in the head steps the card's opacity down.
/// The colours are the run's character's (<see cref="Palette"/>): the content is built again when they change, or the
/// panel moves; the body only when what it shows changes, not every frame.
/// </summary>
internal sealed class Panel
{
    private static Palette P => Palette.Current;
    public static Color Accent => P.Accent;
    public static Color Ink => P.Ink;
    internal static Color Fg => P.Fg;
    internal static Color Lead => P.Strong;
    internal static Color Muted => P.Muted;
    internal static Color Rule => P.Rule;
    internal static Color Track => P.Hover;
    internal static Color Dim => P.Dim;
    internal static readonly Color Warn = Color.FromHtml("#f5b544");
    internal static readonly Color Error = Color.FromHtml("#ff8a70");

    private const float BarWidth = 56;

    /// <summary>The orb's width, and the gap between it and the card.</summary>
    private const int OrbSize = 40, Gap = 6;

    /// <summary>The toolbar's column in the desktop window.</summary>
    private const int ControlsWidth = 340;

    /// <summary>The opacities the head's button steps down through, then back to the first.</summary>
    private static readonly float[] Opacities = [1f, 0.75f, 0.5f, 0.3f];

    private readonly SceneTree _tree;
    private readonly PanelContainer _card;
    private readonly StyleBoxFlat _cardStyle;
    private readonly MapMark _map;
    private readonly CombatMark _combat;
    private readonly Details _details;
    private readonly Action<Command>? _pressed;
    private readonly PanelContainer _orb;
    private readonly Label _orbLabel;
    private readonly HashSet<Key> _down = [];
    private DesktopWindow? _window;

    // The content, built again for new colours or in its other place (Compose); in the card or in the window.
    private Control? _content;
    private Label _champion = null!;
    private Label _status = null!;
    private (Button Button, PlayEnvironment Env)[] _switch = [];
    private (Button Button, InGame Mode)[] _places = [];
    private Control _toolbar = null!;
    private Button _auto = null!, _step = null!, _finish = null!;
    private Control _options = null!;
    private readonly List<(Button Button, AutoSpeed Speed)> _speeds = [];
    private Button _stopElites = null!, _stopAncients = null!, _stopRewards = null!;
    private Label _speedLabel = null!, _stopLabel = null!, _effortLabel = null!;
    private Label? _placeLabel;
    private Control _effortRow = null!;
    /// <summary>The window's left column, which holds the toolbar and the effort row; null over the game.</summary>
    private Control? _controls;
    private readonly List<(Button Button, SearchEffort Effort)> _efforts = [];
    private VBoxContainer _body = null!;
    private Button? _opacity, _open;

    private bool _hidden;
    private bool? _orbStyled;
    /// <summary>While the orb or the head is held: where the mouse is from the orb's corner.</summary>
    private Vector2? _grab;
    /// <summary>Where the mouse went down, and whether it has moved far enough since to be a drag, not a click.</summary>
    private Vector2 _downAt;
    private bool _dragged;
    private object? _built;
    private Drive? _drive;

    /// <summary>The run on screen has more than one player: the AI only advises (no buttons that play). Set by the assist
    /// every frame.</summary>
    public bool Coop { get; set; }

    /// <summary>The run on screen is NoSL's whatever the setting says (a run of more than one player without the hidden
    /// <c>ai mp sl</c>), and the SL/NoSL switch is held there. Set by the assist every frame.</summary>
    public bool HeldAtNoSl { get; set; }

    /// <summary>The run's character (its id), whose colours the panel takes; null out of a run. Set by the assist every frame.</summary>
    public string? Character { get; set; }
    private PlayEnvironment? _envStyled;
    private InGame? _placeStyled;
    private (bool, AutoSpeed, bool, bool, bool, SearchEffort)? _toolStyled;

    /// <param name="pressed">What the toolbar and its keys do; null leaves the toolbar out (the mod is off).</param>
    public Panel(SceneTree tree, Action<Command>? pressed)
    {
        (_tree, _pressed) = (tree, pressed);
        var layer = new CanvasLayer { Layer = 100 };
        _cardStyle = Box(P.Bg with { A = 0.92f }, 14, Rule);
        _cardStyle.SetContentMarginAll(12);
        _cardStyle.ContentMarginLeft = _cardStyle.ContentMarginRight = 14;
        _cardStyle.ShadowColor = new Color(0, 0, 0, 0.4f);
        _cardStyle.ShadowSize = 12;
        // Wide enough for the champion's name in the head ("v-sl-td4-12-1-d2") beside the switch.
        _card = Quiet(new PanelContainer { Position = new Vector2(Settings.Current.PanelX, Settings.Current.PanelY + OrbSize + Gap), CustomMinimumSize = new Vector2(380, 0) });
        _card.AddThemeStyleboxOverride("panel", _cardStyle);

        // The orb: a click opens or closes the card, a drag moves both. It takes the mouse, unlike the card.
        _orb = new PanelContainer { Position = new Vector2(Settings.Current.PanelX, Settings.Current.PanelY), CustomMinimumSize = new Vector2(OrbSize, OrbSize),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        _orbLabel = Text("AI", 14, Ink);
        _orbLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _orbLabel.VerticalAlignment = VerticalAlignment.Center;
        _orb.AddChild(_orbLabel);
        _orb.GuiInput += e => Drag(e, toggle: true);

        layer.AddChild(_card);
        layer.AddChild(_orb);
        _map = new MapMark(layer);
        _combat = new CombatMark(layer);
        _details = new Details(layer);
        tree.Root.CallDeferred(Node.MethodName.AddChild, layer);
    }

    /// <summary>
    /// Builds the content anew, in the colours in use: over the game, in the card in one column; in the desktop window,
    /// in two, with the fight's details under the answer. Whatever it showed before is shown again from scratch.
    /// </summary>
    private void Compose()
    {
        bool window = _window != null;
        _details.Dock(null);
        if (_content != null && GodotObject.IsInstanceValid(_content))
        {
            _content.GetParent()?.RemoveChild(_content);
            _content.QueueFree();
        }
        (_orbStyled, _envStyled, _placeStyled, _toolStyled, _built) = (null, null, null, null, null);
        _speeds.Clear();
        _efforts.Clear();
        _cardStyle.BgColor = P.Bg with { A = 0.92f };
        _cardStyle.BorderColor = Rule;

        // Over the game the head is the card's handle too: it takes the mouse there, unlike the rest of the card.
        var head = Row(8);
        if (!window)
        {
            head.MouseFilter = Control.MouseFilterEnum.Stop;
            head.MouseDefaultCursorShape = Control.CursorShape.Move;
            head.GuiInput += e => Drag(e, toggle: false);
        }
        // The champion's name (its nets and search size) takes the head's free width and is cut there.
        var names = Column(0);
        names.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        names.AddChild(_champion = Clipped(Text("", window ? 15 : 13, Fg)));
        names.AddChild(_status = Clipped(Text("", window ? 12 : 11, Muted)));
        head.AddChild(names);
        // SL or NoSL: the same setting as Environment in Settings → AI; a change starts the run anew for the brain.
        if (_pressed != null)
        {
            var frame = Frame(Colors.Transparent, P.Border, 3);
            var pair = Row(2);
            frame.AddChild(pair);
            _switch = [(Toggle("SL"), PlayEnvironment.SL), (Toggle("NoSL"), PlayEnvironment.NoSL)];
            foreach (var (button, env) in _switch)
            {
                button.Pressed += () => Settings.Set(Settings.Current with { Env = env });
                pair.AddChild(button);
            }
            head.AddChild(frame);
        }
        else _switch = [];
        if (window)
        {
            // What the game keeps: the panel back over it (this window closes), the marks only, or nothing.
            head.AddChild(_placeLabel = Text("", 11, Muted));
            _placeLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            var frame = Frame(Colors.Transparent, P.Border, 3);
            var three = Row(2);
            frame.AddChild(three);
            _places = [(Toggle(""), InGame.Panel), (Toggle(""), InGame.Marks), (Toggle(""), InGame.Nothing)];
            foreach (var (button, mode) in _places)
            {
                button.Pressed += () => Settings.Set(Settings.Current with { InGame = mode });
                three.AddChild(button);
            }
            head.AddChild(frame);
            (_opacity, _open) = (null, null);
        }
        else
        {
            (_places, _placeLabel) = ([], null);
            head.AddChild(_open = HeadButton(Icons.Window, () => Settings.Set(Settings.Current with { InGame = InGame.Marks })));
            head.AddChild(_opacity = HeadButton(Icons.Opacity, () =>
            {
                float now = Settings.Current.Opacity;
                float next = Opacities.FirstOrDefault(o => o < now - 0.01f, Opacities[0]);
                Settings.Set(Settings.Current with { Opacity = next });
            }));
        }

        // The toolbar, in the style of a debugger's: continue / pause, step, step out.
        var bar = Frame(P.Deep, Rule, 3);
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

        // How hard the SL planner searches: one of its presets, switched in the run.
        var effort = Row(8);
        effort.AddChild(_effortLabel = Text("", 11, Muted));
        var efforts = Frame(Colors.Transparent, P.Border, 3);
        var presets = Row(2);
        efforts.AddChild(presets);
        foreach (var value in Enum.GetValues<SearchEffort>())
        {
            var button = Toggle("");
            button.Pressed += () => Settings.Set(Settings.Current with { Effort = value });
            presets.AddChild(button);
            _efforts.Add((button, value));
        }
        effort.AddChild(efforts);
        _effortRow = effort;
        _effortRow.Visible = false;

        // While auto-play runs: its speed and where it stops.
        var options = Column(5);
        var speed = Row(8);
        speed.AddChild(_speedLabel = Text("", 11, Muted));
        var speeds = Frame(Colors.Transparent, P.Border, 3);
        var fast = Row(2);
        speeds.AddChild(fast);
        foreach (var value in new[] { AutoSpeed.Slow, AutoSpeed.Medium, AutoSpeed.Fast })
        {
            var button = Toggle("");
            button.Pressed += () => Settings.Set(Settings.Current with { Speed = value });
            fast.AddChild(button);
            _speeds.Add((button, value));
        }
        speed.AddChild(speeds);
        options.AddChild(speed);
        var stops = Row(8);
        stops.AddChild(_stopLabel = Text("", 11, Muted));
        _stopElites = Toggle("");
        _stopElites.Pressed += () => Settings.Set(Settings.Current with { StopAtElites = !Settings.Current.StopAtElites });
        stops.AddChild(_stopElites);
        _stopAncients = Toggle("");
        _stopAncients.Pressed += () => Settings.Set(Settings.Current with { StopAtAncients = !Settings.Current.StopAtAncients });
        stops.AddChild(_stopAncients);
        _stopRewards = Toggle("");
        _stopRewards.Pressed += () => Settings.Set(Settings.Current with { StopAtRewards = !Settings.Current.StopAtRewards });
        stops.AddChild(_stopRewards);
        options.AddChild(stops);
        _options = options;
        _options.Visible = false;

        _body = Column(8);
        if (window)
        {
            var page = Column(14);
            page.AddChild(head);
            page.AddChild(Quiet(new ColorRect { Color = Rule, CustomMinimumSize = new Vector2(0, 1) }));
            var columns = Row(18);
            columns.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            var controls = Column(12);
            controls.CustomMinimumSize = new Vector2(ControlsWidth, 0);
            controls.AddChild(_toolbar);
            controls.AddChild(_effortRow);
            controls.AddChild(_options);
            columns.AddChild(_controls = controls);
            // The answer and the details scroll when they are taller than the window.
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            var answer = Column(14);
            answer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            answer.AddChild(_body);
            scroll.AddChild(answer);
            columns.AddChild(scroll);
            page.AddChild(columns);
            _window!.Content.AddChild(page);
            _details.Dock(answer);
            _content = page;
        }
        else
        {
            var column = Column(10);
            column.AddChild(head);
            column.AddChild(_toolbar);
            column.AddChild(_effortRow);
            column.AddChild(_options);
            column.AddChild(_body);
            _card.AddChild(column);
            (_content, _controls) = (column, null);
        }
    }

    /// <summary>
    /// Opens or closes the desktop window as the settings say (closed while the panel hides, <paramref name="shown"/>
    /// false), and builds the content in its place and colours when either changed.
    /// </summary>
    private void Dock(bool shown)
    {
        bool window = shown && Settings.Current.InGame != InGame.Panel;
        bool recolor = Palette.Use(Character);
        if (window != (_window != null))
        {
            if (window) _window = new DesktopWindow(_tree);
            else
            {
                // The window frees what it shows: the details' card is taken out first, the rest goes with it.
                _details.Dock(null);
                _window!.Close();
                (_window, _content) = (null, null);
            }
            recolor = true;
        }
        if (recolor || _content == null) Compose();
        _window?.Tick();
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

    /// <summary>Nothing over the game and no window: the player turned assist off.</summary>
    public void Hide()
    {
        Dock(false);
        _card.Visible = _orb.Visible = false;
        _map.Show(null, false);
        _combat.Show(null, false);
        _details.Show(null, false, default, 1);
    }

    /// <summary>A message instead of an answer.</summary>
    /// <param name="sub">A second, quieter line: the reason, or what happens next.</param>
    /// <param name="drive">The toolbar's state; null leaves the toolbar out (no run to play).</param>
    public void Show(string champion, string status, string message, string? sub = null, Tone tone = Tone.Plain, Drive? drive = null)
    {
        Head(champion, status, drive);
        Build((message, sub, tone, drive?.Key), () =>
        {
            StatusStrip(drive);
            Message(message, sub, tone);
            Recent(drive);
        });
        Marks(null, null);
        _details.Show(null, false, default, 1);
    }

    /// <summary>The brain's answer, and the marks it draws on the map or in combat.</summary>
    /// <param name="sub">A line under the next action: how the fight or the run is expected to go after it.</param>
    /// <param name="foot">Which brain answered, and how long it took.</param>
    /// <param name="table">In a fight, the answer's line turn by turn, for the details card (<see cref="Details"/>),
    /// which a button under the answer opens and closes; null for none.</param>
    public void Show(string champion, string status, Shown answer, string? sub, string foot, MapRoute? route, CombatPlan? plan, Drive? drive,
        FightTable? table = null)
    {
        Head(champion, status, drive);
        Build((answer, sub, foot, drive?.Key, table != null), () =>
        {
            StatusStrip(drive);
            Answer(answer, sub, foot, table != null);
            Recent(drive);
        });
        Marks(route, plan);
        var settings = Settings.Current;
        // In the window, the details are under the answer, whatever the game shows.
        if (_window != null) _details.Show(table, settings.Details, default, 1, Coop);
        else _details.Show(table, !_hidden && settings.Panel && settings.Details, new Rect2(_card.Position, _card.Size), settings.Opacity, Coop);
    }

    private void Head(string champion, string status, Drive? drive)
    {
        Dock(true);
        var settings = Settings.Current;
        bool window = _window != null;
        _orb.Visible = !_hidden && settings.InGame != InGame.Nothing;
        _card.Visible = !_hidden && settings.Panel && !window;
        if (_card.Theme != Fonts.Theme) _card.Theme = _orb.Theme = Fonts.Theme;
        if (_card.Modulate.A != settings.Opacity) _card.Modulate = Colors.White with { A = settings.Opacity };
        if (_orbStyled != settings.Panel)
        {
            // Open: filled; closed: a ring.
            _orbStyled = settings.Panel;
            var style = Box(settings.Panel ? Accent : P.Bg, OrbSize / 2, Accent, 2);
            style.ShadowColor = new Color(0, 0, 0, 0.4f);
            style.ShadowSize = 8;
            _orb.AddThemeStyleboxOverride("panel", style);
            _orbLabel.AddThemeColorOverride("font_color", settings.Panel ? Ink : Accent);
        }
        // Where it was let go, kept inside the window as the card grows and shrinks.
        if (_grab == null && _card.IsInsideTree()) Place(new Vector2(settings.PanelX, settings.PanelY));
        Tooltip(_orb, window ? L10n.T("Click to show or hide the marks, drag to move it") : L10n.T("Click to open or close the panel, drag to move it"));
        if (_opacity != null) Tooltip(_opacity, $"{L10n.T("Panel opacity")} {settings.Opacity * 100:0}%");
        if (_open != null) Tooltip(_open, L10n.T("Move the panel into its own window, outside the game"));
        if (_placeLabel != null) Label(_placeLabel, L10n.T("In game"));
        foreach (var (button, mode) in _places)
            Label(button, L10n.T(mode switch { InGame.Panel => "Panel + marks", InGame.Marks => "Marks only", _ => "Nothing" }));
        if (_placeStyled != settings.InGame)
        {
            _placeStyled = settings.InGame;
            foreach (var (button, mode) in _places) Restyle(button, mode == settings.InGame);
        }
        _champion.Text = champion;
        _status.Text = status;
        var env = HeldAtNoSl ? PlayEnvironment.NoSL : settings.Env;
        if (env != _envStyled)
        {
            _envStyled = env;
            foreach (var (button, which) in _switch) Restyle(button, which == env);
        }
        foreach (var (button, _) in _switch) button.Disabled = HeldAtNoSl;
        _drive = drive;
        _toolbar.Visible = drive != null && !Coop && _pressed != null;
        _effortRow.Visible = drive != null && _pressed != null;
        _options.Visible = drive is { Auto: true };
        if (_controls != null) _controls.Visible = _toolbar.Visible || _effortRow.Visible;
        if (drive == null) return;
        Set(_auto, drive.Auto ? L10n.T("Pause") : L10n.T("Auto"), drive.Auto ? Icons.Pause : Icons.Play, true);
        // Every frame, so a change of the game's language shows.
        Label(_speedLabel, L10n.T("Speed"));
        Label(_stopLabel, L10n.T("Stop at"));
        foreach (var (button, speed) in _speeds) Label(button, L10n.T(speed.ToString()));
        Label(_stopElites, L10n.T("elites, bosses"));
        Label(_stopAncients, L10n.T("ancients"));
        Label(_stopRewards, L10n.T("rewards"));
        Label(_effortLabel, L10n.T("Effort"));
        foreach (var (button, effort) in _efforts) Label(button, L10n.T(effort.ToString()));
        Set(_step, drive.Step ?? L10n.T("Step"), Icons.Step, !drive.Auto && drive.Step != null);
        Set(_finish, drive.Finish ?? L10n.T("Finish room"), Icons.Finish, !drive.Auto && drive.Finish != null);
        // New style boxes only when what they show changed, not every frame.
        var styled = (drive.Auto, settings.Speed, settings.StopAtElites, settings.StopAtAncients, settings.StopAtRewards, settings.Effort);
        if (styled == _toolStyled) return;
        _toolStyled = styled;
        RestyleTool(_auto, drive.Auto);
        foreach (var (button, speed) in _speeds) Restyle(button, settings.Speed == speed);
        Restyle(_stopElites, settings.StopAtElites);
        Restyle(_stopAncients, settings.StopAtAncients);
        Restyle(_stopRewards, settings.StopAtRewards);
        foreach (var (button, effort) in _efforts) Restyle(button, settings.Effort == effort);
    }

    private static void Label(Label label, string text)
    {
        if (label.Text != text) label.Text = text;
    }

    private static void Label(Button button, string text)
    {
        if (button.Text != text) button.Text = text;
    }

    private static void Tooltip(Control control, string text)
    {
        if (control.TooltipText != text) control.TooltipText = text;
    }

    /// <summary>
    /// Drags the orb and the card together, by the orb or the card's head; where they are let go is kept in the settings.
    /// A press and release on the orb that moved under 4 pixels is a click: it opens or closes the card.
    /// </summary>
    private void Drag(InputEvent e, bool toggle)
    {
        var mouse = _orb.GetGlobalMousePosition();
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                _grab = mouse - _orb.Position;
                _downAt = mouse;
                _dragged = false;
            }
            else if (_grab != null)
            {
                _grab = null;
                if (_dragged) Settings.Set(Settings.Current with { PanelX = _orb.Position.X, PanelY = _orb.Position.Y });
                else if (toggle) Settings.Set(Settings.Current with { Panel = !Settings.Current.Panel });
            }
        }
        else if (e is InputEventMouseMotion && _grab is { } grab && (_dragged || mouse.DistanceTo(_downAt) >= 4))
        {
            _dragged = true;
            Place(mouse - grab);
        }
    }

    /// <summary>
    /// The orb at <paramref name="at"/> and the card under it, or over it when there is no room under; both moved as
    /// little as it takes to be inside the window.
    /// </summary>
    private void Place(Vector2 at)
    {
        var window = _orb.GetViewportRect().Size;
        var orb = _orb.Position = Inside(at, _orb.Size, window);
        float under = orb.Y + OrbSize + Gap;
        float y = under + _card.Size.Y <= window.Y ? under : orb.Y - Gap - _card.Size.Y;
        _card.Position = Inside(new Vector2(orb.X, y), _card.Size, window);
    }

    private static Vector2 Inside(Vector2 at, Vector2 size, Vector2 window)
    {
        var room = window - size;
        return new Vector2(Math.Clamp(at.X, 0, Math.Max(room.X, 0)), Math.Clamp(at.Y, 0, Math.Max(room.Y, 0)));
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
        // The orb closes the marks with the card.
        bool visible = !_hidden && settings.Panel && settings.InGame != InGame.Nothing;
        _map.Show(settings.Route == RouteMode.Off ? null : route, visible, settings.Route == RouteMode.Whole);
        _combat.Show(plan, visible);
    }

    /// <summary>Builds the body again when what it shows, or the settings it is drawn with, changed.</summary>
    private void Build(object what, Action build)
    {
        var key = (what, Settings.Current with
            {
                Env = default, Assist = default, Panel = default, Opacity = default, PanelX = default, PanelY = default, Speed = default, StopAtElites = default,
                StopAtAncients = default, StopAtRewards = default,
                Effort = default, TurnDecay = default, InGame = default, Window = default,
            },
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
        var strip = Frame(P.Raised, null, 8);
        var row = Row(10);
        strip.AddChild(row);
        var left = Column(2);
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (drive.Win is { } win)
        {
            left.AddChild(Text(L10n.T("Win chance").ToUpperInvariant(), 11, Muted));
            var line = Row(6);
            line.AddChild(Text(Percent(win), 20, Fg));
            if (drive.Delta is { } delta && Math.Abs(delta) >= 0.0005)
            {
                var change = Text($"{(delta > 0 ? "+" : "-")}{Math.Abs(delta) * 100:0.0}", 12, delta > 0 ? Icons.Green : Error);
                change.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                line.AddChild(change);
            }
            left.AddChild(line);
        }
        row.AddChild(left);
        var pill = Frame(Track, null, 6);
        var inside = Row(6);
        pill.AddChild(inside);
        var dot = Quiet(new PanelContainer { CustomMinimumSize = new Vector2(8, 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        dot.AddThemeStyleboxOverride("panel", Box(drive.PillTone switch { Tone.Warn => Warn, Tone.Error => Error, _ => drive.Auto ? Icons.Green : Warn }, 4));
        inside.AddChild(dot);
        inside.AddChild(Text(drive.Pill, 12, Fg));
        pill.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(pill);
        _body.AddChild(strip);
    }

    private void Recent(Drive? drive)
    {
        if (drive == null || drive.Recent.Count == 0) return;
        _body.AddChild(Text(L10n.T("Recent").ToUpperInvariant(), 11, Muted));
        foreach (var line in drive.Recent) _body.AddChild(Clipped(Text(line, 12, Fg)));
    }

    private void Message(string message, string? sub, Tone tone)
    {
        var color = tone switch { Tone.Warn => Warn, Tone.Error => Error, _ => Fg };
        var row = Row(10);
        var dot = Quiet(new PanelContainer { CustomMinimumSize = new Vector2(10, 10), SizeFlagsVertical = Control.SizeFlags.ShrinkBegin });
        dot.AddThemeStyleboxOverride("panel", Box(tone == Tone.Plain ? Accent : color, 5));
        var margin = Quiet(new MarginContainer());
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddChild(dot);
        row.AddChild(margin);
        var lines = Column(4);
        lines.AddChild(Wrapped(Text(message, 15, color)));
        if (sub != null) lines.AddChild(Wrapped(Text(sub, 12, Muted)));
        row.AddChild(lines);
        _body.AddChild(row);
    }

    private void Answer(Shown answer, string? sub, string foot, bool details)
    {
        _body.AddChild(Text(L10n.T("Next").ToUpperInvariant(), 11, Muted));
        var next = Row(10);
        next.AddChild(answer.Route ? Ring(22) : Badge("1", 24, 12, Accent, null, 13));
        var lines = Column(2);
        lines.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        lines.AddChild(Wrapped(Text(answer.Next, 17, Fg)));
        if (sub != null) lines.AddChild(Wrapped(Text(sub, 12, Muted)));
        next.AddChild(lines);
        _body.AddChild(next);
        var mode = Settings.Current.Considered;
        bool considered = mode != ConsideredMode.Off && answer.Options.Count > 0;
        // On the map the options show their routes as rooms; the chosen one's is then not written out again.
        if (answer.Route)
        {
            if (answer.Then.Count > 0 && !(considered && answer.Options.Any(o => o.Rooms != null)))
            {
                _body.AddChild(Text(L10n.T("Route to the boss").ToUpperInvariant(), 11, Muted));
                _body.AddChild(Wrapped(Text(string.Join(" · ", answer.Then), 13, Fg)));
            }
        }
        else
        {
            for (int i = 0; i < answer.Then.Count; i++)
            {
                var step = Row(10);
                step.AddChild(Indent(2));
                step.AddChild(Badge($"{i + 2}", 20, 10, null, Accent, 11));
                step.AddChild(Wrapped(Text(answer.Then[i], 14, Fg)));
                _body.AddChild(step);
            }
        }
        if (answer.Later != null)
        {
            var later = Row(0);
            later.AddChild(Indent(32));
            later.AddChild(Text(answer.Later, 12, Muted));
            _body.AddChild(later);
        }
        if (considered) Options(answer, mode == ConsideredMode.Bars);
        if (details) _body.AddChild(DetailsButton());
        _body.AddChild(Text(foot, 11, Muted));
    }

    /// <summary>Opens and closes the fight's details card beside the panel's (<see cref="Settings.Details"/>).</summary>
    private static Button DetailsButton()
    {
        bool open = Settings.Current.Details;
        var button = new Button { Text = $"{L10n.T("Fight details")} · {L10n.T(open ? "Hide" : "Show")}", FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 32), Alignment = HorizontalAlignment.Left };
        button.AddThemeFontSizeOverride("font_size", 13);
        RestyleTool(button, open);
        button.Pressed += () => Settings.Set(Settings.Current with { Details = !Settings.Current.Details });
        return button;
    }

    /// <summary>
    /// The options the brain scored, the chosen first: with their chance to win the fight and the HP left (the SL
    /// planner), their chance to win the run and its gap to the best (V), or a search's score with its confidence
    /// interval (PIMC). The bars share one scale. On the map each option is its route, drawn as its rooms, with a legend.
    /// </summary>
    private void Options(Shown answer, bool bars)
    {
        var options = answer.Options;
        bool routes = options.All(o => o.Rooms is { Count: > 0 });
        bars &= !routes;
        _body.AddChild(Quiet(new ColorRect { Color = Rule, CustomMinimumSize = new Vector2(0, 1) }));
        var (third, fourth) = answer.Kind switch
        {
            ShownKind.Fight => (L10n.T("win"), L10n.T("HP left")),
            ShownKind.Run => (L10n.T("win"), L10n.T("diff")),
            _ => (L10n.T("value ±CI"), ""),
        };
        var grid = Quiet(new GridContainer { Columns = bars ? 4 : 3 });
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 5);
        Cell(grid, L10n.T(routes ? "Route to the boss" : "Considered").ToUpperInvariant(), 11, Muted, expand: true);
        if (bars) grid.AddChild(Quiet(new Control()));
        Cell(grid, third, 11, Muted, right: true);
        Cell(grid, fourth, 11, Muted, right: true);
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
            if (routes)
            {
                var rooms = Row(2);
                rooms.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                foreach (var room in o.Rooms!) rooms.AddChild(Chip(room, first: i == 0));
                grid.AddChild(rooms);
            }
            else Cell(grid, o.Name, 13, color, expand: true);
            if (bars)
            {
                var bar = Quiet(new Control { CustomMinimumSize = new Vector2(BarWidth, 12), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
                bar.AddChild(Strip(0, BarWidth, 2, 8, Track));
                double v = Shown(o);
                bar.AddChild(Strip(At(v - o.Ci), Math.Max(At(v + o.Ci) - At(v - o.Ci), 3), 2, 8, i == 0 ? Accent : Dim));
                if (answer.Kind == ShownKind.Score)
                    bar.AddChild(Quiet(new ColorRect { Color = Fg, Position = new Vector2(At(v) - 1, 0), Size = new Vector2(2, 12) }));
                grid.AddChild(bar);
            }
            switch (answer.Kind)
            {
                case ShownKind.Fight:
                    Cell(grid, Percent(o.Win ?? 0, whole: true), 13, color, right: true);
                    Cell(grid, o.Win > 0 && o.Hp is { } hp ? $"{hp:0}" : "—", 13, color, right: true);
                    break;
                case ShownKind.Run:
                    Cell(grid, Percent(o.Value), 13, color, right: true);
                    Cell(grid, i == 0 || o.Value >= best ? "—" : "-" + $"{(best - o.Value) * 100:0.0}", 13, Muted, right: true);
                    break;
                default:
                    Cell(grid, $"{o.Value:0.000} ±{o.Ci:0.000}", 13, color, right: true);
                    Cell(grid, "", 13, color, right: true);
                    break;
            }
        }
        _body.AddChild(grid);
        if (!routes) return;
        var legend = Quiet(new HFlowContainer());
        legend.AddThemeConstantOverride("h_separation", 10);
        legend.AddThemeConstantOverride("v_separation", 3);
        foreach (var type in options.SelectMany(o => o.Rooms!).Distinct())
        {
            var item = Row(3);
            item.AddChild(Chip(type, first: false));
            item.AddChild(Text(Describe.Legend(type), 11, Muted));
            legend.AddChild(item);
        }
        _body.AddChild(legend);
    }

    /// <summary>A room of a route: a small square in its type's color, with its legend's first letter; the chosen route's are brighter.</summary>
    private static PanelContainer Chip(string? type, bool first)
    {
        var color = type switch
        {
            "Elite" => Warn,
            "RestSite" => Icons.Green,
            "Shop" => Icons.Blue,
            "Treasure" => Color.FromHtml("#e8c56a"),
            "Boss" => Error,
            "Unknown" => Muted,
            _ => first ? Muted : Dim,
        };
        string legend = Describe.Legend(type);
        string letter = type == "Unknown" ? "?" : StringInfo.GetNextTextElement(legend);
        var chip = Quiet(new PanelContainer { CustomMinimumSize = new Vector2(16, 16), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        chip.AddThemeStyleboxOverride("panel", Box(type == "Boss" ? Error : Colors.Transparent, 3, color, 1));
        var label = Text(letter.ToUpperInvariant(), 9, type == "Boss" ? Ink : type == "Monster" ? Fg : color);
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
            label.CustomMinimumSize = new Vector2(42, 0);
        }
        grid.AddChild(label);
    }

    private static Godot.Panel Strip(float x, float width, float y, float height, Color color)
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
        var button = new Button { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 32), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        button.AddThemeFontSizeOverride("font_size", 13);
        button.AddThemeConstantOverride("h_separation", 5);
        button.AddThemeConstantOverride("icon_max_width", Icons.Shown);
        button.Pressed += () => _pressed?.Invoke(command);
        RestyleTool(button, false);
        return button;
    }

    internal static void RestyleTool(Button button, bool on)
    {
        var fill = Box(on ? P.Hover : Colors.Transparent, 7);
        fill.ContentMarginLeft = fill.ContentMarginRight = 8;
        var hover = Box(P.Hover, 7);
        hover.ContentMarginLeft = hover.ContentMarginRight = 8;
        foreach (var state in new[] { "normal", "pressed", "focus", "disabled" }) button.AddThemeStyleboxOverride(state, fill);
        button.AddThemeStyleboxOverride("hover", hover);
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" }) button.AddThemeColorOverride(state, Fg);
        button.AddThemeColorOverride("font_disabled_color", Dim);
        button.AddThemeColorOverride("icon_disabled_color", new Color(1, 1, 1, 0.35f));
    }

    /// <summary>A small icon button in the head's corner: grey, white under the mouse.</summary>
    private static Button HeadButton(Texture2D icon, Action pressed)
    {
        var button = new Button { Icon = icon, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(24, 24),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, IconAlignment = HorizontalAlignment.Center };
        button.AddThemeConstantOverride("icon_max_width", 12);
        var fill = Box(Colors.Transparent, 6);
        fill.SetContentMarginAll(4);
        var hover = Box(P.Hover, 6);
        hover.SetContentMarginAll(4);
        foreach (var state in new[] { "normal", "pressed", "focus" }) button.AddThemeStyleboxOverride(state, fill);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeColorOverride("icon_normal_color", Muted);
        foreach (var state in new[] { "icon_hover_color", "icon_pressed_color", "icon_hover_pressed_color", "icon_focus_color" }) button.AddThemeColorOverride(state, Fg);
        button.Pressed += pressed;
        return button;
    }

    private static Button Toggle(string text)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 24) };
        button.AddThemeFontSizeOverride("font_size", 12);
        Restyle(button, false);
        return button;
    }

    private static void Restyle(Button button, bool on)
    {
        var fill = Box(on ? Accent : Colors.Transparent, 6);
        fill.ContentMarginLeft = fill.ContentMarginRight = 8;
        var hover = Box(on ? Accent : P.Hover, 6);
        hover.ContentMarginLeft = hover.ContentMarginRight = 8;
        foreach (var state in new[] { "normal", "pressed", "focus" }) button.AddThemeStyleboxOverride(state, fill);
        button.AddThemeStyleboxOverride("hover", hover);
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            button.AddThemeColorOverride(state, on ? Ink : Muted);
    }

    /// <summary>A round badge with a number or a word: filled with <paramref name="fill"/>, or drawn as a ring of <paramref name="ring"/>.</summary>
    private static PanelContainer Badge(string text, int size, int radius, Color? fill, Color? ring, int fontSize)
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
    private static PanelContainer Ring(int size)
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

    internal static Label Wrapped(Label label)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(200, 0);
        return label;
    }

    /// <summary>One line, cut with an ellipsis where its space ends, instead of widening the card.</summary>
    internal static Label Clipped(Label label)
    {
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return label;
    }

    private static Control Indent(int width) => Quiet(new Control { CustomMinimumSize = new Vector2(width, 0) });

    internal static VBoxContainer Column(int gap)
    {
        var box = Quiet(new VBoxContainer());
        box.AddThemeConstantOverride("separation", gap);
        return box;
    }

    internal static HBoxContainer Row(int gap)
    {
        var box = Quiet(new HBoxContainer());
        box.AddThemeConstantOverride("separation", gap);
        return box;
    }

    /// <summary>The panel takes no clicks but its buttons': the game under it gets them.</summary>
    internal static T Quiet<T>(T control) where T : Control
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        return control;
    }
}
