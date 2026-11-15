using System.Globalization;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Ai.Mod;

/// <summary>
/// The mod's log: a folder for each game session under <see cref="Settings.Dir"/>/logs/, named by its start in UTC,
/// holding mod.log (what the mod saw, asked, got and did) beside the brain's files (<see cref="BundledBrain"/>). Each
/// line is flushed as it is written, so a crash or a kill keeps everything up to it. Only the newest
/// <see cref="Kept"/> folders stay. <see cref="Info"/>, <see cref="Warn"/> and <see cref="Error"/> also go to the game's
/// own log; <see cref="Write"/> only here. The game's main thread and the thread reading the brain's stderr both write.
/// Everything stays on the player's computer.
/// </summary>
internal static class ModLog
{
    /// <summary>How many game sessions' folders are kept.</summary>
    public const int Kept = 5;

    private static readonly object Gate = new();
    private static StreamWriter? _file;

    /// <summary>This game session's folder; null before <see cref="Start"/>.</summary>
    public static string? Folder { get; private set; }

    /// <summary>Makes this game session's folder and deletes the oldest past <see cref="Kept"/>.</summary>
    public static void Start()
    {
        string root = Path.Combine(Settings.Dir, "logs");
        string folder = Path.Combine(root, DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        // The names sort as their times do.
        foreach (var old in Directory.GetDirectories(root).Order(StringComparer.Ordinal).SkipLast(Kept)) Directory.Delete(old, recursive: true);
        lock (Gate)
        {
            _file = new StreamWriter(Path.Combine(folder, "mod.log"), append: true) { AutoFlush = true };
            Folder = folder;
        }
    }

    /// <summary>A line in mod.log only.</summary>
    public static void Write(string category, string text) => Line("info", category, text);

    public static void Info(string category, string text)
    {
        Log.Info($"{Host.Tag} {text}");
        Line("info", category, text);
    }

    public static void Warn(string category, string text)
    {
        Log.Warn($"{Host.Tag} {text}");
        Line("warn", category, text);
    }

    public static void Error(string category, string text)
    {
        Log.Error($"{Host.Tag} {text}");
        Line("error", category, text);
    }

    private static void Line(string level, string category, string text)
    {
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        lock (Gate)
        {
            try
            {
                _file?.WriteLine($"{stamp}  {level,-5}  {category,-8}  {text}");
            }
            catch (IOException)
            {
                // A full disk loses the log, never the game.
            }
        }
    }
}
