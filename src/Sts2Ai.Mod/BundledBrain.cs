using System.Diagnostics;
using System.Runtime.InteropServices;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// The brain that ships in the mod's folder, so a player needs nothing else: brain/RID/ holds a
/// self-contained brain for each platform, brain/ the champion files and their nets: champion.json NoSL's,
/// sl-champion.json SL's (each environment has its own nets). It runs as a child process of the game,
/// talking on its stdin and stdout, with the running game's own assemblies (STS2_BIN_DIR, the folder the game loaded
/// sts2.dll from) and version (STS2_GAME_VERSION, which the live combats it writes out carry for the engine) and its
/// working directory brain/. It writes into the game session's folder of the mod's log (<see cref="ModLog"/>): its
/// stderr to brain.log, every message it gets to wire.jsonl (what <c>clones</c> replays the SL records from) and each
/// decision as it answers it to decisions.jsonl. A brain started again in the same game session adds to all three.
/// </summary>
internal sealed class BundledBrain
{
    private readonly string _dir;
    private readonly string _gameBin;
    private readonly string _gameVersion;

    private readonly string _logs;

    private BundledBrain(string dir, string launcher, string gameBin, string gameVersion, string logs) =>
        (_dir, Launcher, _gameBin, _gameVersion, _logs) = (dir, launcher, gameBin, gameVersion, logs);

    public string Launcher { get; }

    /// <summary>The engine's schema beside this platform's brain, which the live combat is written out along for it.</summary>
    public string SchemaPath => Path.Combine(Path.GetDirectoryName(Launcher)!, "sts2ai_engine.schema.json");

    private string LogPath => Path.Combine(_logs, "brain.log");

    /// <summary>The brain beside the mod for this platform, or null when the mod ships none (an install for development).</summary>
    /// <param name="logs">The game session's folder of the mod's log.</param>
    public static BundledBrain? Find(string modDir, string gameBin, string gameVersion, string logs)
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string rid = $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        var dir = Path.Combine(modDir, "brain");
        var launcher = Path.Combine(dir, rid, OperatingSystem.IsWindows() ? "Sts2Ai.SimWorker.exe" : "Sts2Ai.SimWorker");
        return File.Exists(launcher) ? new BundledBrain(dir, launcher, gameBin, gameVersion, logs) : null;
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
        var start = new ProcessStartInfo(Launcher, ["brain", "--champion", "champion.json", "--sl-champion", "sl-champion.json", "--workers", Workers.ToString(),
            "--wire", Path.Combine(_logs, "wire.jsonl"), "--log", Path.Combine(_logs, "decisions.jsonl")])
        {
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["STS2_BIN_DIR"] = _gameBin;
        start.Environment["STS2_GAME_VERSION"] = _gameVersion;
        // As every launcher of the simulator sets it.
        start.Environment["DOTNET_ReadyToRun"] = "0";
        // The game's Sentry extension sets this in the game's process. Inherited, it makes sts2.dll's module
        // initializer in each search worker load that extension outside Godot and call into it: SIGSEGV at boot.
        start.Environment.Remove("SENTRY_GODOT_LIB_PATH");

        using var log = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        using var process = Process.Start(start)!;
        _process = process;
        ModLog.Write("brain", $"started the brain: pid {process.Id}, {start.FileName} {string.Join(" ", start.ArgumentList)}");
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
        ModLog.Write("brain", $"the brain ended: pid {process.Id}, exit code {process.ExitCode}, last line: {last}");
        return $"the brain ended (exit code {process.ExitCode}): {last} (log: {LogPath})";
    }

    private volatile Process? _process;

    /// <summary>Ends the brain process running now, if one is (the console's restart); true if there was one.</summary>
    public bool Kill()
    {
        if (_process is not { } process) return false;
        try
        {
            if (process.HasExited) return false;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the shipped simulator for a job of the console's (a race, a rollout, a scout) with <paramref name="args"/>,
    /// as the brain starts: the game's own assemblies and version, in brain/, its stdout read by the caller, its stderr
    /// to <paramref name="errors"/> in the game session's folder of the mod's log.
    /// </summary>
    public Process Job(IReadOnlyList<string> args, string errors)
    {
        var start = new ProcessStartInfo(Launcher, args)
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["STS2_BIN_DIR"] = _gameBin;
        start.Environment["STS2_GAME_VERSION"] = _gameVersion;
        start.Environment["DOTNET_ReadyToRun"] = "0";
        start.Environment.Remove("SENTRY_GODOT_LIB_PATH");
        // A home of its own: the headless game it boots loads and cleans up runs, and must never reach the player's saves.
        string home = Path.Combine(_logs, "job-home");
        Directory.CreateDirectory(home);
        foreach (var name in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_DATA_HOME", "XDG_CONFIG_HOME" }) start.Environment[name] = home;
        var process = Process.Start(start)!;
        var log = new StreamWriter(Path.Combine(_logs, errors), append: true) { AutoFlush = true };
        new Thread(() =>
        {
            using (log)
                while (process.StandardError.ReadLine() is { } line)
                    log.WriteLine(line);
        }) { IsBackground = true, Name = "sts2ai job stderr" }.Start();
        ModLog.Write("ai", $"started a job: pid {process.Id}, {start.FileName} {string.Join(" ", start.ArgumentList)}");
        return process;
    }

    /// <summary>The game session's folder of the mod's log, where jobs keep their files.</summary>
    public string Logs => _logs;

    /// <summary>The search's worker processes: half the logical processors, so the game keeps its share, and no more than the planner's 8 worlds.</summary>
    public static int Workers => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
}
