using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Logging;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod.UI;

/// <summary>How much of the brain's scoring the panel shows.</summary>
internal enum ConsideredMode { Off, Values, Bars }

/// <summary>How much of a map answer's route the map shows.</summary>
internal enum RouteMode { Off, Next, Whole }

/// <summary>How long auto-play waits before each of the AI's actions, so the player can follow it.</summary>
internal enum AutoSpeed { Slow, Medium, Fast }

/// <summary>How hard the SL planner searches each fight: the brain's own effort, or one of its presets.</summary>
internal enum SearchEffort { Brain, Low, Medium, High, Max }

/// <summary>
/// The player's choices for the mod (docs: output §2), set in the game's settings screen (<see cref="SettingsTab"/>) and
/// the panel's SL/NoSL switch. They are kept in one file in the mod's own folder (<see cref="Dir"/>), outside the game's
/// profile, and read once at start; every change is written back at once.
/// </summary>
/// <param name="Assist">Off: the mod asks the brain nothing and draws nothing.</param>
/// <param name="Numbers">Order numbers on the cards the plan plays this turn.</param>
/// <param name="Marks">The plan's target and end-turn marks.</param>
/// <param name="StopAtElites">Auto-play stops before it travels to an elite or a boss.</param>
/// <param name="StopOnDrop">Auto-play stops when V's chance to win falls 3 points below the best it showed since auto-play started.</param>
/// <param name="Effort">The SL planner's effort the mod asks the brain for; a change starts the run anew for the brain.</param>
internal sealed record Settings(
    bool Assist = true,
    PlayEnvironment Env = PlayEnvironment.SL,
    bool Panel = true,
    ConsideredMode Considered = ConsideredMode.Bars,
    bool Numbers = true,
    bool Marks = true,
    RouteMode Route = RouteMode.Whole,
    float Opacity = 0.92f,
    AutoSpeed Speed = AutoSpeed.Medium,
    bool StopAtElites = true,
    bool StopOnDrop = true,
    SearchEffort Effort = SearchEffort.Brain)
{
    /// <summary>The effort as hello names it; null for the brain's own.</summary>
    public string? EffortName => Effort == SearchEffort.Brain ? null : Effort.ToString().ToLowerInvariant();

    /// <summary>The wait before each auto-play action at <see cref="Speed"/>, in milliseconds.</summary>
    public int Delay => Speed switch { AutoSpeed.Slow => 1500, AutoSpeed.Medium => 800, _ => 200 };

    /// <summary>The mod's own folder, outside the game's profile: the settings and the brain's log. $XDG_STATE_HOME/sts2ai, by default ~/.local/state/sts2ai.</summary>
    public static readonly string Dir = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state
            ? state
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
        "sts2ai");

    private static readonly string Kept = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Settings Current { get; private set; } = Read();

    public static void Set(Settings settings)
    {
        if (settings == Current) return;
        Current = settings;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Kept)!);
            File.WriteAllText(Kept, JsonSerializer.Serialize(settings, Json));
        }
        catch (IOException e)
        {
            Log.Warn($"{Host.Tag} could not keep the settings in {Kept}: {e.Message}");
        }
    }

    /// <summary>The kept settings; the defaults until the player changes one.</summary>
    private static Settings Read()
    {
        try
        {
            return File.Exists(Kept) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Kept), Json) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Log.Warn($"{Host.Tag} could not read {Kept}, using the defaults: {e.Message}");
            return new();
        }
    }
}
