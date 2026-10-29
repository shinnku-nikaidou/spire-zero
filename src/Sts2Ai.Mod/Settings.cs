using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Logging;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>How much of the brain's scoring the panel shows.</summary>
internal enum ConsideredMode { Off, Values, Bars }

/// <summary>How much of a map answer's route the map shows.</summary>
internal enum RouteMode { Off, Next, Whole }

/// <summary>
/// The player's choices for the mod (docs: output §2), set in the game's settings screen (<see cref="SettingsTab"/>) and
/// the panel's SL/NoSL switch. They are kept in one file beside the brain's socket, outside the game's profile, and
/// read once at start; every change is written back at once.
/// </summary>
/// <param name="Assist">Off: the mod asks the brain nothing and draws nothing.</param>
/// <param name="Numbers">Order numbers on the cards the plan plays this turn.</param>
/// <param name="Marks">The plan's target and end-turn marks.</param>
internal sealed record Settings(
    bool Assist = true,
    PlayEnvironment Env = PlayEnvironment.SL,
    bool Panel = true,
    ConsideredMode Considered = ConsideredMode.Bars,
    bool Numbers = true,
    bool Marks = true,
    RouteMode Route = RouteMode.Whole,
    float Opacity = 0.92f)
{
    private static readonly string Kept = Path.Combine(Path.GetDirectoryName(BrainSocket.DefaultPath)!, "settings.json");

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
