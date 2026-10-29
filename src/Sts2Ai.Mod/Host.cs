using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>Starts the mod and runs its per-frame loop on the game's main thread (docs: mod §3).</summary>
internal static class Host
{
    /// <summary>The game version GameBridge is written against; any other turns the mod off (docs: mod §2).</summary>
    private const string PinnedVersion = "v0.111.0";

    public const string Tag = "[Sts2Ai]";

    private static Assist? _assist;
    private static Panel? _panel;
    private static string? _off;
    private static string? _lastError;

    public static void Start()
    {
        string version = NGame.GetGameVersion();
        if (version != PinnedVersion)
        {
            _off = $"off: game {version}, the mod is for {PinnedVersion}";
            Log.Warn($"{Tag} {_off}");
        }
        else
        {
            // First, before anything else compiles game code that could inline an entry (docs: mod §10).
            var harmony = new Harmony("sts2ai.mod");
            ChoiceContexts.Patch(harmony);
            NodeRecord.Patch(harmony);
            SettingsTab.Patch(harmony);
            _assist = new Assist(BrainLink.Start(BrainSocket.DefaultPath), version);
            Log.Info($"{Tag} assist mode, {Settings.Current.Env}, brain at {BrainSocket.DefaultPath}");
        }
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Tick;
    }

    private static void Tick()
    {
        try
        {
            _panel ??= new Panel((SceneTree)Engine.GetMainLoop(), _assist == null ? null : env => Settings.Set(Settings.Current with { Env = env }));
            _panel.Poll();
            if (_assist == null) _panel.Show("—", "off", null, _off!);
            else _assist.Tick(_panel);
            _lastError = null;
        }
        catch (Exception e)
        {
            // Once per distinct error, not once per frame.
            string error = e.ToString();
            if (error == _lastError) return;
            _lastError = error;
            Log.Error($"{Tag} {error}");
            _panel?.Show("—", "error", null, "Mod error", e.Message, Tone.Error);
        }
    }
}
