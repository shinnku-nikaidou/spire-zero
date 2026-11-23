using Godot;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// The mod's colours for the run's character: the accent from the character's own colour in the game (its name's colour;
/// the Necrobinder's from its targeting line, pushed to pink away from the old purple; the Silent's darkened), the
/// neutrals tinted with the same hue, in OKLCH; the picked one's on a screen that starts a run; elsewhere out of a run
/// (the main menu), the mod's old purple. The state colours
/// (a win chance up or down, warnings, the rooms' types) stay the same for every character (<see cref="Icons"/>,
/// <see cref="Panel.Warn"/>, <see cref="Panel.Error"/>).
/// </summary>
/// <param name="Accent">The frames, the orb, the chosen option's bar, the marks on the game's nodes.</param>
/// <param name="Strong">The chosen option's text.</param>
/// <param name="Ink">Text on the accent.</param>
/// <param name="Bg">The card's fill.</param>
/// <param name="Deep">The toolbar's fill.</param>
/// <param name="Raised">The status strip's fill.</param>
/// <param name="Hover">A button under the mouse or on, and the tracks of bars and pills.</param>
/// <param name="Rule">The card's edge, lines between its parts, the chart's axes.</param>
/// <param name="Border">The frames around switches.</param>
/// <param name="Dim">Disabled text, the other options' bars.</param>
/// <param name="Muted">Labels and second lines.</param>
/// <param name="Fg">Text.</param>
internal sealed record Palette(Color Accent, Color Strong, Color Ink, Color Bg, Color Deep, Color Raised, Color Hover, Color Rule, Color Border, Color Dim,
    Color Muted, Color Fg)
{
    private static readonly Palette None = Of("#bf59ff", "#c689ff", "#130c19", "#100d13", "#08050a", "#1c1720", "#2a232f", "#322a38", "#42394a", "#695e73", "#b6adbf", "#f5f2f8");

    private static readonly Dictionary<string, Palette> Characters = new()
    {
        ["IRONCLAD"] = Of("#fc6661", "#ff928a", "#1b0a09", "#140c0b", "#0a0505", "#211615", "#312221", "#3a2927", "#4d3735", "#765c59", "#c2aba8", "#f9f1f1"),
        ["SILENT"] = Of("#69d85f", "#9cec93", "#091206", "#0c100a", "#050704", "#161b13", "#21291e", "#283124", "#364130", "#5a6854", "#a9b5a4", "#f1f5ef"),
        ["DEFECT"] = Of("#6cc3ef", "#9cdbfc", "#011219", "#081013", "#03070a", "#101b20", "#19292f", "#1f3138", "#2b414a", "#4e6873", "#9fb5bf", "#eef5f8"),
        ["REGENT"] = Of("#fd9f23", "#ffc075", "#180d02", "#120d08", "#090603", "#1f1810", "#2e241a", "#362c20", "#483a2b", "#71604e", "#bdae9e", "#f7f3ee"),
        ["NECROBINDER"] = Of("#f784c8", "#ffaadc", "#160b16", "#120c11", "#090509", "#1e171e", "#2d222d", "#352935", "#463746", "#6e5c6e", "#bbabba", "#f6f2f6"),
        ["FOOL"] = Of("#f1d93f", "#fbea85", "#111002", "#0f0f08", "#070703", "#1a1a10", "#28271a", "#2f2f20", "#3f3e2b", "#65654e", "#b3b39e", "#f4f4ee"),
    };

    /// <summary>The colours in use: the run's character's, or the purple out of a run.</summary>
    public static Palette Current { get; private set; } = None;

    /// <summary>Takes the colours of <paramref name="character"/> (its id; null out of a run, or a character with none of
    /// its own); whether they changed.</summary>
    public static bool Use(string? character)
    {
        var next = character != null ? Characters.GetValueOrDefault(character, None) : None;
        if (next == Current) return false;
        Current = next;
        return true;
    }

    private static Palette Of(params string[] html)
    {
        var c = html.Select(h => Color.FromHtml(h)).ToArray();
        return new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11]);
    }
}
