using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// The panel's own desktop window, outside the game's (<see cref="Settings.InGame"/>): a native window that stays above
/// the game's, its borderless fullscreen too (the game's transient child), and that the player moves anywhere, another
/// screen too, and resizes; where it was left and how big are kept in the settings. Its content is scaled as the game
/// scales its 1080p canvas on a screen of that height, so its text reads as large as the game's. It opens on another
/// screen than the game's if there is one. Closing it puts the panel back over the game.
/// </summary>
internal sealed class DesktopWindow
{
    /// <summary>Its size the first time, and the least it is resized to, in the game's 1080p pixels.</summary>
    private static readonly Vector2 First = new(940, 760), Least = new(640, 420);

    private const string Title = "Spire Zero";

    /// <summary>How long after it opens the window is put in its place again, once the desktop has framed it.</summary>
    private const ulong Framed = 300;

    private readonly Window _window;
    private readonly StyleBoxFlat _frame;
    private readonly WindowRect _rect;
    private int _screen = -1;
    private ulong _keptAt;
    /// <summary>When it was first in the tree; whether it was put in its place again since.</summary>
    private ulong? _opened;
    private bool _placed;

    /// <summary>What the window shows: the panel puts its content here.</summary>
    public Container Content { get; }

    public DesktopWindow(SceneTree tree)
    {
        var rect = _rect = Settings.Current.Window is { } kept && Shown(kept) ? kept : Default();
        // Native only while not shown: hidden first, which a window outside the tree only notes.
        _window = new Window
        {
            Visible = false, ForceNative = true, Title = Title, Transient = true, WrapControls = false,
            InitialPosition = Window.WindowInitialPosition.Absolute,
            Position = new Vector2I(rect.X, rect.Y), Size = new Vector2I(rect.W, rect.H),
        };
        _window.Visible = true;
        _window.CloseRequested += () => Settings.Set(Settings.Current with { InGame = InGame.Panel });
        Rescale(ScreenOf(rect));
        _frame = Panel.Box(Palette.Current.Bg, 0, Palette.Current.Accent, 2);
        var fill = new Godot.Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        fill.AddThemeStyleboxOverride("panel", _frame);
        fill.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _window.AddChild(fill);
        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 16);
        _window.AddChild(margin);
        Content = margin;
        tree.Root.CallDeferred(Node.MethodName.AddChild, _window);
    }

    /// <summary>Once per frame: the game's font and the colours in use, the scale of the screen it is on, and where it is
    /// kept, at most once a second.</summary>
    public void Tick()
    {
        if (!_window.IsInsideTree()) return;
        ulong now = Time.GetTicksMsec();
        if (_opened == null)
        {
            // It takes the game's V-Sync: on a screen of a slower refresh, it would hold the game's frames to that screen's.
            _opened = now;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled, _window.GetWindowId());
        }
        if (!_placed && now >= _opened + Framed)
        {
            // A window opens with its frame where its content was asked to be (X11), and the kept place is its
            // content's: put there again once framed, it stays put across games. The new window took the focus too:
            // the game's goes back to it, for its keys.
            _placed = true;
            _window.Position = new Vector2I(_rect.X, _rect.Y);
            _window.GetTree().Root.GrabFocus();
        }
        if (_window.Theme != Fonts.Theme) _window.Theme = Fonts.Theme;
        var p = Palette.Current;
        if (_frame.BgColor != p.Bg) _frame.BgColor = p.Bg;
        if (_frame.BorderColor != p.Accent) _frame.BorderColor = p.Accent;
        if (_window.CurrentScreen != _screen) Rescale(_window.CurrentScreen);
        if (!_placed || now < _keptAt + 1000) return;
        _keptAt = now;
        var rect = new WindowRect(_window.Position.X, _window.Position.Y, _window.Size.X, _window.Size.Y);
        if (Settings.Current.Window != rect) Settings.Set(Settings.Current with { Window = rect });
    }

    private void Rescale(int screen)
    {
        _screen = screen;
        float scale = Scale(screen);
        _window.ContentScaleFactor = scale;
        _window.MinSize = (Vector2I)(Least * scale);
    }

    /// <summary>Closes the window, and frees what it shows.</summary>
    public void Close() => _window.QueueFree();

    /// <summary>The game's scale on a screen: its height over the game's 1080p canvas, never under 1.</summary>
    private static float Scale(int screen) => Math.Max(1f, DisplayServer.ScreenGetSize(screen).Y / 1080f);

    /// <summary>Whether a kept place is still on one of the screens, which may have changed since.</summary>
    private static bool Shown(WindowRect rect) => Enumerable.Range(0, DisplayServer.GetScreenCount())
        .Any(s => DisplayServer.ScreenGetUsableRect(s).Intersects(new Rect2I(rect.X, rect.Y, rect.W, rect.H)));

    /// <summary>The screen that holds a place's middle; the game's if none does.</summary>
    private static int ScreenOf(WindowRect rect)
    {
        var middle = new Vector2I(rect.X + rect.W / 2, rect.Y + rect.H / 2);
        return Enumerable.Range(0, DisplayServer.GetScreenCount()).Where(s => new Rect2I(DisplayServer.ScreenGetPosition(s), DisplayServer.ScreenGetSize(s)).HasPoint(middle))
            .DefaultIfEmpty(DisplayServer.WindowGetCurrentScreen()).First();
    }

    /// <summary>The first place: the middle of another screen than the game's, or the game's top right corner.</summary>
    private static WindowRect Default()
    {
        int game = DisplayServer.WindowGetCurrentScreen();
        int screen = Enumerable.Range(0, DisplayServer.GetScreenCount()).Where(s => s != game).DefaultIfEmpty(game).First();
        var area = DisplayServer.ScreenGetUsableRect(screen);
        var want = (Vector2I)(First * Scale(screen));
        var size = new Vector2I(Math.Min(want.X, area.Size.X), Math.Min(want.Y, area.Size.Y));
        var at = screen == game ? new Vector2I(area.End.X - size.X, area.Position.Y) : area.Position + (area.Size - size) / 2;
        return new(at.X, at.Y, size.X, size.Y);
    }

    /// <summary>
    /// The game mutes itself and lowers its frame rate when its window loses the focus (its settings' mute in background
    /// and limit FPS in background, both on by default), so a click in this window would. The two do that when the
    /// game's windows all lose the focus instead, which Godot sends as the application's focus.
    /// </summary>
    public static void Patch(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(DesktopWindow), nameof(AppFocus));
        foreach (var type in new[] { typeof(NMuteInBackgroundHandler), typeof(NBackgroundModeHandler) })
            harmony.Patch(AccessTools.Method(type, nameof(Node._Notification)), prefix);
    }

    private static bool AppFocus(ref int __0)
    {
        switch ((long)__0)
        {
            case Node.NotificationWMWindowFocusIn or Node.NotificationWMWindowFocusOut:
                return false;
            case Node.NotificationApplicationFocusIn:
                __0 = (int)Node.NotificationWMWindowFocusIn;
                break;
            case Node.NotificationApplicationFocusOut:
                __0 = (int)Node.NotificationWMWindowFocusOut;
                break;
        }
        return true;
    }
}
