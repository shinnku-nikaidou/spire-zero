using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// The mod's "AI" tab in the game's settings screen (docs: output §2), after General, Graphics, Sound and Input. The
/// game builds its four tabs in <see cref="NSettingsTabManager"/>'s _Ready from nodes in its scene; a postfix there
/// copies the Input tab for the tab's button, and the Sound panel, emptied, for the tab's panel, so both look and switch
/// like the game's own. The rows are the mod's own controls in the game's font, each writing <see cref="Settings"/> at
/// once. Anything that fails here leaves the settings screen as the game built it, and is logged.
/// </summary>
internal static class SettingsTab
{
    private static readonly AccessTools.FieldRef<NSettingsTabManager, Dictionary<NSettingsTab, NSettingsPanel>> Tabs =
        AccessTools.FieldRefAccess<NSettingsTabManager, Dictionary<NSettingsTab, NSettingsPanel>>("_tabs");

    private static readonly MethodInfo SwitchTabTo = AccessTools.Method(typeof(NSettingsTabManager), "SwitchTabTo");

    /// <summary>Copies take the nodes and their scripts, not the game's signal connections to them.</summary>
    private const int Copy = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);

    private static readonly Color Muted = Color.FromHtml("#9aa6b6");

    public static void Patch(Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(NSettingsTabManager), nameof(NSettingsTabManager._Ready)),
        postfix: new HarmonyMethod(typeof(SettingsTab), nameof(Ready)));

    /// <summary>Adds the tab once the whole screen is ready: during _Ready the panels' parent may still be adding its children.</summary>
    private static void Ready(NSettingsTabManager __instance) => Callable.From(() =>
    {
        try
        {
            Add(__instance);
        }
        catch (Exception e)
        {
            Log.Error($"{Host.Tag} no AI settings tab: {e}");
        }
    }).CallDeferred();

    private static void Add(NSettingsTabManager manager)
    {
        var tabs = Tabs(manager);
        var input = manager.GetNode<NSettingsTab>("Input");
        var sound = manager.GetNode<NSettingsTab>("Sound");
        var model = tabs[sound];
        var font = model.FindChildren("*", "Label", owned: false).OfType<Label>().FirstOrDefault();

        // Everything is built before any of it joins the screen, so a failure leaves the screen as it was.
        var panel = (NSettingsPanel)model.Duplicate(Copy);
        panel.Name = "Sts2AiSettings";
        panel.UniqueNameInOwner = false;
        panel.Visible = false;
        var content = panel.GetNode<VBoxContainer>("VBoxContainer");
        foreach (var child in content.GetChildren())
        {
            content.RemoveChild(child);
            child.Free();
        }
        var refresh = new List<Action>();
        Fill(content, font, refresh);
        panel.VisibilityChanged += () => refresh.ForEach(show => show());
        var tab = (NSettingsTab)input.Duplicate(Copy);
        tab.Name = "Sts2Ai";

        model.AddSibling(panel);
        input.AddSibling(tab);
        tab.SetLabel("AI");
        // The tabs are placed by hand in the scene: the new one goes one step right of Input, and the row moves half a
        // step left to stay centred.
        var step = input.Position - sound.Position;
        if (step != Vector2.Zero)
        {
            foreach (var other in tabs.Keys) other.Position -= step / 2;
            manager.GetNode<Control>("LeftTabIcon").Position -= step / 2;
            manager.GetNode<Control>("RightTabIcon").Position += step / 2;
            tab.Position = input.Position + step;
        }
        tab.Connect(NClickableControl.SignalName.Released, Callable.From<NSettingsTab>(t => SwitchTabTo.Invoke(manager, [t])));
        tabs[tab] = panel;
    }

    private static void Fill(VBoxContainer content, Label? font, List<Action> refresh)
    {
        string[] onOff = [L10n.T("Off"), L10n.T("On")];
        bool[] bools = [false, true];
        content.AddChild(Header(L10n.T("Assist"), font));
        content.AddChild(Choice(L10n.T("AI assist"), L10n.T("off: the mod asks nothing and draws nothing"), font, onOff, bools, s => s.Assist, (s, v) => s with { Assist = v }, refresh));
        content.AddChild(Choice(L10n.T("Environment"), L10n.T("SL sees the true outcome of the room you are in; NoSL never peeks"), font,
            ["SL", "NoSL"], [PlayEnvironment.SL, PlayEnvironment.NoSL], s => s.Env, (s, v) => s with { Env = v }, refresh));
        content.AddChild(Choice(L10n.T("Panel"), L10n.T("F8 hides everything at once"), font, onOff, bools, s => s.Panel, (s, v) => s with { Panel = v }, refresh));
        content.AddChild(Choice(L10n.T("Considered options"), L10n.T("in the panel"), font, [L10n.T("Off"), L10n.T("Values"), L10n.T("Values + bars")],
            [ConsideredMode.Off, ConsideredMode.Values, ConsideredMode.Bars], s => s.Considered, (s, v) => s with { Considered = v }, refresh));
        content.AddChild(Choice(L10n.T("Order numbers on cards"), L10n.T("the cards this turn's plan plays"), font, onOff, bools, s => s.Numbers, (s, v) => s with { Numbers = v }, refresh));
        content.AddChild(Choice(L10n.T("Target and end-turn marks"), "", font, onOff, bools, s => s.Marks, (s, v) => s with { Marks = v }, refresh));
        content.AddChild(Choice(L10n.T("Map route"), "", font, [L10n.T("Off"), L10n.T("Next point"), L10n.T("Whole route")],
            [RouteMode.Off, RouteMode.Next, RouteMode.Whole], s => s.Route, (s, v) => s with { Route = v }, refresh));
        content.AddChild(Opacity(font, refresh));
        content.AddChild(Choice(L10n.T("Search effort"), L10n.T("how hard the SL planner searches each fight; a change starts the run anew for the brain"), font,
            [.. Enum.GetValues<SearchEffort>().Select(e => L10n.T(e.ToString()))], Enum.GetValues<SearchEffort>(), s => s.Effort, (s, v) => s with { Effort = v }, refresh));
        content.AddChild(Header(L10n.T("Auto-play"), font));
        content.AddChild(Choice(L10n.T("Speed of auto-play"), L10n.T("the pause before each of the AI's actions"), font,
            [L10n.T("Slow"), L10n.T("Medium"), L10n.T("Fast")], [AutoSpeed.Slow, AutoSpeed.Medium, AutoSpeed.Fast], s => s.Speed, (s, v) => s with { Speed = v }, refresh));
        content.AddChild(Choice(L10n.T("Stop before elites and bosses"), "", font, onOff, bools, s => s.StopAtElites, (s, v) => s with { StopAtElites = v }, refresh));
        content.AddChild(Choice(L10n.T("Stop when the win chance falls 3 points"), "", font, onOff, bools, s => s.StopOnDrop, (s, v) => s with { StopOnDrop = v }, refresh));
        var reset = new Button { Text = L10n.T("Reset AI settings"), FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        Style(reset, font, 1);
        reset.Pressed += () =>
        {
            Settings.Set(new Settings());
            refresh.ForEach(show => show());
        };
        content.AddChild(reset);
    }

    /// <summary>A row that steps through <paramref name="values"/> with the two arrows, as the game's own paginators do.</summary>
    private static HBoxContainer Choice<T>(string label, string sub, Label? font, string[] names, T[] values, Func<Settings, T> get,
        Func<Settings, T, Settings> set, List<Action> refresh)
    {
        var value = Text("", font, 1);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        value.CustomMinimumSize = new Vector2(220, 0);
        void Show() => value.Text = names[Math.Max(Array.IndexOf(values, get(Settings.Current)), 0)];
        void Move(int by)
        {
            int at = Array.IndexOf(values, get(Settings.Current));
            Settings.Set(set(Settings.Current, values[(at + by + values.Length) % values.Length]));
            Show();
        }
        var prev = Arrow("<", font, () => Move(-1));
        var next = Arrow(">", font, () => Move(1));
        refresh.Add(Show);
        Show();
        return Row(label, sub, font, prev, value, next);
    }

    private static HBoxContainer Opacity(Label? font, List<Action> refresh)
    {
        var slider = new HSlider { MinValue = 0.6, MaxValue = 1, Step = 0.02, CustomMinimumSize = new Vector2(220, 28), FocusMode = Control.FocusModeEnum.None,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var value = Text("", font, 1);
        value.CustomMinimumSize = new Vector2(70, 0);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        void Show()
        {
            slider.SetValueNoSignal(Settings.Current.Opacity);
            value.Text = $"{Settings.Current.Opacity * 100:0}%";
        }
        slider.ValueChanged += v =>
        {
            Settings.Set(Settings.Current with { Opacity = (float)v });
            Show();
        };
        refresh.Add(Show);
        Show();
        return Row(L10n.T("Panel opacity"), "", font, slider, value);
    }

    private static HBoxContainer Row(string label, string sub, Label? font, params Control[] controls)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 64) };
        row.AddThemeConstantOverride("separation", 16);
        var name = Text(label, font, 1);
        name.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(name);
        if (sub != "")
        {
            var note = Text(sub, font, 0.7f, Muted);
            note.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            note.CustomMinimumSize = new Vector2(200, 0);
            row.AddChild(note);
        }
        else row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        foreach (var control in controls)
        {
            control.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(control);
        }
        return row;
    }

    private static Label Header(string text, Label? font)
    {
        var header = Text(text.ToUpperInvariant(), font, 0.75f, Muted);
        header.CustomMinimumSize = new Vector2(0, 48);
        header.VerticalAlignment = VerticalAlignment.Bottom;
        return header;
    }

    private static Button Arrow(string text, Label? font, Action pressed)
    {
        var button = new Button { Text = text, Flat = true, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(48, 48) };
        Style(button, font, 1);
        button.Pressed += pressed;
        return button;
    }

    /// <summary>A label in the font of the game's own settings rows, at <paramref name="scale"/> of its size.</summary>
    private static Label Text(string text, Label? font, float scale, Color? color = null)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        if (font != null)
        {
            label.AddThemeFontOverride("font", font.GetThemeFont("font"));
            label.AddThemeFontSizeOverride("font_size", (int)(font.GetThemeFontSize("font_size") * scale));
        }
        label.AddThemeColorOverride("font_color", color ?? font?.GetThemeColor("font_color") ?? Colors.White);
        return label;
    }

    private static void Style(Button button, Label? font, float scale)
    {
        if (font == null) return;
        button.AddThemeFontOverride("font", font.GetThemeFont("font"));
        button.AddThemeFontSizeOverride("font_size", (int)(font.GetThemeFontSize("font_size") * scale));
    }
}
