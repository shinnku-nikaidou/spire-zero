using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Sts2Ai.SimWorker.Runtime;

/// <summary>
/// The sim's logs (Microsoft.Extensions.Logging), one line each on stderr, so that stdout carries only results, and for
/// serve the wire. Information and up by default; --verbose adds Debug: the game's own log, and each failure's stack.
/// </summary>
internal static class Logs
{
    public static ILoggerFactory Factory { get; private set; } = NullLoggerFactory.Instance;

    /// <summary>The sim's own messages.</summary>
    public static ILogger Sim { get; private set; } = NullLogger.Instance;

    /// <summary>The game's log (<see cref="HeadlessPatches"/>).</summary>
    public static ILogger Game { get; private set; } = NullLogger.Instance;

    /// <summary>Starts logging; disposing the result writes out what is still queued.</summary>
    public static IDisposable Start(bool verbose)
    {
        var factory = LoggerFactory.Create(logging => logging
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
            .AddConsole(console =>
            {
                console.FormatterName = Line.Id;
                console.LogToStandardErrorThreshold = LogLevel.Trace;
            })
            .AddConsoleFormatter<Line, ConsoleFormatterOptions>());
        (Factory, Sim, Game) = (factory, factory.CreateLogger("sim"), factory.CreateLogger("game"));
        return factory;
    }

    /// <summary>A message's first line, at most 200 characters: what a one-line report of an error shows.</summary>
    public static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    /// <summary>"12:34:56 INFO    sim: message", as the Python scripts' logs; an exception follows on its own lines.</summary>
    private sealed class Line() : ConsoleFormatter(Id)
    {
        public const string Id = "line";

        public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter writer)
        {
            var level = entry.LogLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARNING",
                LogLevel.Error => "ERROR",
                _ => "CRITICAL",
            };
            writer.Write($"{DateTime.Now:HH:mm:ss} {level,-7} {entry.Category}: {entry.Formatter(entry.State, entry.Exception)}\n");
            if (entry.Exception != null) writer.Write(entry.Exception + "\n");
        }
    }
}
