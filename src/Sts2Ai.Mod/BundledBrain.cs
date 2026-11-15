using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// The brain that ships in the mod's folder, so a player needs nothing else: brain/RID/ holds a
/// self-contained brain for each platform, brain/ its champion file and nets. It runs as a child process of the game,
/// talking on its stdin and stdout, with the running game's own assemblies (STS2_BIN_DIR, the folder the game loaded
/// sts2.dll from) and its working directory brain/; its stderr goes to <see cref="LogPath"/>. With
/// <see cref="Settings.Record"/> it also writes what it gets and decides beside that log (<see cref="Captures"/>).
/// </summary>
internal sealed class BundledBrain
{
    private readonly string _dir;
    private readonly string _gameBin;
    private bool _started;

    private BundledBrain(string dir, string launcher, string gameBin, string logPath) =>
        (_dir, Launcher, _gameBin, LogPath) = (dir, launcher, gameBin, logPath);

    public string Launcher { get; }

    /// <summary>The engine's schema beside this platform's brain, which the live combat is written out along for it.</summary>
    public string SchemaPath => Path.Combine(Path.GetDirectoryName(Launcher)!, "sts2ai_engine.schema.json");

    public string LogPath { get; }

    /// <summary>The brain beside the mod for this platform, or null when the mod ships none (an install for development).</summary>
    public static BundledBrain? Find(string modDir, string gameBin, string logPath)
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string rid = $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        var dir = Path.Combine(modDir, "brain");
        var launcher = Path.Combine(dir, rid, OperatingSystem.IsWindows() ? "Sts2Ai.SimWorker.exe" : "Sts2Ai.SimWorker");
        return File.Exists(launcher) ? new BundledBrain(dir, launcher, gameBin, logPath) : null;
    }

    /// <summary>Starts the brain, runs <paramref name="session"/> on its stdin and stdout, and ends it; why it ended.</summary>
    public string Run(Func<WireStream, string> session)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Steam's download is not relied on to keep the executable bit.
            const UnixFileMode exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            var mode = File.GetUnixFileMode(Launcher);
            if ((mode & exec) != exec) File.SetUnixFileMode(Launcher, mode | exec);
        }
        var start = new ProcessStartInfo(Launcher, ["brain", "--champion", "champion.json", "--workers", Workers.ToString(), .. Captures()])
        {
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["STS2_BIN_DIR"] = _gameBin;
        // As every launcher of the simulator sets it.
        start.Environment["DOTNET_ReadyToRun"] = "0";
        // The game's Sentry extension sets this in the game's process. Inherited, it makes sts2.dll's module
        // initializer in each search worker load that extension outside Godot and call into it: SIGSEGV at boot.
        start.Environment.Remove("SENTRY_GODOT_LIB_PATH");

        // The game session's first brain starts the log; a brain started again after it ended adds to it.
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        using var log = new StreamWriter(LogPath, append: _started) { AutoFlush = true };
        _started = true;
        using var process = Process.Start(start)!;
        string last = "";
        var errors = new Thread(() =>
        {
            while (process.StandardError.ReadLine() is { } line)
            {
                log.WriteLine(line);
                if (line.Trim().Length > 0) last = line.Trim();
            }
        }) { IsBackground = true, Name = "sts2ai brain stderr" };
        errors.Start();
        try
        {
            session(new WireStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream));
        }
        finally
        {
            // Its search workers too: they are its children.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            errors.Join();
        }
        return $"the brain ended (exit code {process.ExitCode}): {last} (log: {LogPath})";
    }

    /// <summary>With <see cref="Settings.Record"/>, the brain's --wire and --log: a pair of files named by its start, in UTC.</summary>
    private string[] Captures()
    {
        if (!Settings.Current.Record) return [];
        var dir = Path.Combine(Path.GetDirectoryName(LogPath)!, "captures");
        Directory.CreateDirectory(dir);
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        return ["--wire", Path.Combine(dir, $"{stamp}-wire.jsonl"), "--log", Path.Combine(dir, $"{stamp}-decisions.jsonl")];
    }

    /// <summary>The search's worker processes: half the logical processors, so the game keeps its share, and no more than the planner's 8 worlds.</summary>
    private static int Workers => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
}
