using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace Sts2Ai.Mod;

/// <summary>
/// What the console's ai command writes. The game's console shows its output as BBCode, so text is escaped; a table goes
/// in a code block, in the console's monospace font. An answer that comes later than the command (a query, a race) is
/// added to the console's output when it comes, and to the mod's log.
/// </summary>
internal static class Out
{
    private static readonly AccessTools.FieldRef<NDevConsole, RichTextLabel> Buffer = AccessTools.FieldRefAccess<NDevConsole, RichTextLabel>("_outputBuffer");

    /// <summary>Text as the console shows it as is: BBCode's opening bracket escaped.</summary>
    public static string Esc(string text) => text.Replace("[", "[lb]");

    /// <summary>Lines in the console's monospace font, escaped.</summary>
    public static string Code(string text) => "[code]" + Esc(text.TrimEnd('\n')) + "[/code]";

    /// <summary>Lines in the console's monospace font, escaped.</summary>
    public static string Code(IEnumerable<string> lines) => Code(string.Join("\n", lines));

    /// <summary>Adds an answer that came after its command to the console, and to the log.</summary>
    /// <param name="bbcode">Already escaped (<see cref="Esc"/>, <see cref="Code(string)"/>).</param>
    public static void Later(string bbcode)
    {
        ModLog.Write("ai", bbcode);
        if (NDevConsole.InstanceOrNull is { } console) Buffer(console).Text += "[color=#88ccff]ai ›[/color] " + bbcode + "\n";
    }

    /// <summary>An error that came after its command.</summary>
    public static void LaterError(string text) => Later($"[color=#ff5555]{Esc(text)}[/color]");

    /// <summary>A fraction as percent.</summary>
    public static string Pct(double p, string format = "0.0") => (p * 100).ToString(format, System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>A value difference in points of a value (×100).</summary>
    public static string Pts(double d) => (d * 100).ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Text padded to a width, cut with an ellipsis when longer.</summary>
    public static string Pad(string text, int width) => text.Length > width ? text[..(width - 1)] + "…" : text.PadRight(width);
}
