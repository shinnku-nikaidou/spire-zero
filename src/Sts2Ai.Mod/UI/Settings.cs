using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod.UI;

/// <summary>How much of the brain's scoring the panel shows.</summary>
internal enum ConsideredMode { Off, Values, Bars }

/// <summary>How much of a map answer's route the map shows.</summary>
internal enum RouteMode { Off, Next, Whole }

/// <summary>How long auto-play waits before each of the AI's actions, so the player can follow it.</summary>
internal enum AutoSpeed { Slow, Medium, Fast }

/// <summary>How hard the SL planner searches each fight: one of its presets.</summary>
internal enum SearchEffort { Low, Medium, High, Max }

/// <summary>
/// The player's choices for the mod, set in the game's settings screen (<see cref="SettingsTab"/>) and some in the
/// panel. They are kept in one file in the mod's own folder (<see cref="Dir"/>), outside the game's
/// profile, and read once at start; every change is written back at once.
/// </summary>
/// <param name="Assist">Off: the mod asks the brain nothing and draws nothing.</param>
/// <param name="Env">SL or NoSL, set in the settings screen, its last setting, and by the panel's switch; a change starts the run anew for the brain.</param>
/// <param name="Numbers">Order numbers on the cards the plan plays this turn.</param>
/// <param name="Marks">The plan's target and end-turn marks.</param>
/// <param name="Panel">The panel's card and its marks (combat, map) show; its orb opens and closes them.</param>
/// <param name="Opacity">The card's opacity, all of it: its box, text and buttons.</param>
/// <param name="PanelX">Where the panel's orb is, its top-left corner in the game's 1080p canvas; the card opens under it. The player drags it.</param>
/// <param name="StopAtElites">Auto-play stops before it travels to an elite or a boss.</param>
/// <param name="StopAtAncients">Auto-play stops once at each act's ancient, for the player's own pick.</param>
/// <param name="StopAtRewards">Auto-play stops once at each fight's rewards, for the player's own; resumed after the player took
/// one, it leaves the rest.</param>
/// <param name="Effort">The SL planner's effort the mod asks the brain for; a change starts the run anew for the brain.</param>
/// <param name="TurnDecay">The mod asks the SL planner to take a little off a line for each turn it ended
/// (<see cref="Hello.TurnDecay"/>), so a fight is not drawn out for nothing; a change starts the run anew for the brain.</param>
internal sealed record Settings(
    bool Assist = true,
    PlayEnvironment Env = PlayEnvironment.SL,
    bool Panel = true,
    ConsideredMode Considered = ConsideredMode.Bars,
    bool Numbers = true,
    bool Marks = true,
    RouteMode Route = RouteMode.Whole,
    float Opacity = 1f,
    float PanelX = 24f,
    float PanelY = 132f,
    AutoSpeed Speed = AutoSpeed.Medium,
    bool StopAtElites = true,
    bool StopAtAncients = false,
    bool StopAtRewards = false,
    SearchEffort Effort = SearchEffort.Medium,
    bool TurnDecay = true)
{
    /// <summary>The effort as hello names it.</summary>
    public string EffortName => Effort.ToString().ToLowerInvariant();

    /// <summary>The wait before each auto-play action at <see cref="Speed"/>, in milliseconds.</summary>
    public int Delay => Speed switch { AutoSpeed.Slow => 1500, AutoSpeed.Medium => 800, _ => 200 };

    /// <summary>The mod's own folder, outside the game's profile: the settings and the logs (<see cref="ModLog"/>). $XDG_STATE_HOME/sts2ai, by default ~/.local/state/sts2ai.</summary>
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
            ModLog.Warn("settings", $"could not keep the settings in {Kept}: {e.Message}");
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
            ModLog.Warn("settings", $"could not read {Kept}, using the defaults: {e.Message}");
            return new();
        }
    }
}
