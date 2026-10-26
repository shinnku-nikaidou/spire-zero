using System.Text.Json;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2Ai.Headless;

/// <summary>Process-wide boot of the game's model layer, and the error channel the game reports into.</summary>
internal static class HeadlessGame
{
    public static bool Verbose;
    public static string GameVersion { get; private set; } = "";

    /// <summary>Game Log.Error lines and unobserved task faults since the last <see cref="TakeErrors"/>.</summary>
    private static readonly List<string> Errors = [];

    private static bool _booted;

    public static void Boot(bool verbose = false)
    {
        if (_booted) return;
        _booted = true;
        Verbose = verbose;
        GameVersion = ReadGameVersion();
        HeadlessPatches.GameVersion = GameVersion;

        GodotNativeStub.Install();
        HeadlessPatches.Apply();
        Probe.MaybeApply(); // TEMPORARY
        TestMode.TurnOnInternal();

        // Mods are never loaded headless; the setter is private.
        typeof(ModManager).GetProperty(nameof(ModManager.State))!.SetValue(null, ModManagerState.Skipped);
        AssemblyInfo.Init();
        ModelDb.Init();
        ModelIdSerializationCache.Init();
        ModelDb.InitIds();
        LocalContext.NetId = 1UL;

        var saves = SaveManager.Instance;
        saves.InitSettingsDataForTest();
        saves.InitPrefsDataForTest();
        saves.PrefsSave.FastMode = FastModeType.Instant;

        Log.LogCallback += (level, text, _) =>
        {
            if (level >= LogLevel.Error) lock (Errors) Errors.Add(text);
        };
        TaskHelper.UnobservedFault += e => { lock (Errors) Errors.Add("unobserved task fault: " + e); };
    }

    public static List<string> TakeErrors()
    {
        lock (Errors)
        {
            var copy = Errors.ToList();
            Errors.Clear();
            return copy;
        }
    }

    private static string ReadGameVersion()
    {
        var path = Path.Combine(GameAssemblies.BinDir, "release_info.json");
        if (!File.Exists(path)) return "unknown";
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("version").GetString() ?? "unknown";
    }
}
