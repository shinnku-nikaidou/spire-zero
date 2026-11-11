using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>Starts the mod and runs its per-frame loop on the game's main thread.</summary>
internal static class Host
{
    /// <summary>The game version GameBridge is written against; any other turns the mod off.</summary>
    private const string PinnedVersion = "v0.111.0";

    public const string Tag = "[Sts2Ai]";

    private static Assist? _assist;
    private static UI.Panel? _panel;
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
            // First, before anything else compiles game code that could inline an entry.
            var harmony = new Harmony("sts2ai.mod");
            ChoiceContexts.Patch(harmony);
            NodeRecord.Patch(harmony);
            SettingsTab.Patch(harmony);
            // The brain shipped beside the mod plays with the running game's own assemblies, and logs beside the settings.
            var bundled = BundledBrain.Find(Path.GetDirectoryName(typeof(Host).Assembly.Location)!, Path.GetDirectoryName(typeof(NGame).Assembly.Location)!,
                Path.Combine(Settings.Dir, "brain.log"));
            var link = BrainLink.Start(bundled);
            _assist = new Assist(link, version);
            Log.Info($"{Tag} assist mode, {Settings.Current.Env}, brain: {link.Where}");
        }
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Tick;
    }

    private static void Tick()
    {
        try
        {
            var assist = _assist;
            _panel ??= new UI.Panel((SceneTree)Engine.GetMainLoop(), assist == null ? null : env => Settings.Set(Settings.Current with { Env = env }),
                assist == null ? null : assist.Press);
            _panel.Poll();
            if (assist == null) _panel.Show("—", L10n.T("off"), null, _off!);
            else assist.Tick(_panel);
            _lastError = null;
        }
        catch (Exception e)
        {
            // Once per distinct error, not once per frame.
            string error = e.ToString();
            if (error == _lastError) return;
            _lastError = error;
            Log.Error($"{Tag} {error}");
            _panel?.Show("—", L10n.T("error"), null, L10n.T("Mod error"), e.Message, Tone.Error);
        }
    }
}
