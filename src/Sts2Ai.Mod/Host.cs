using System.Runtime.InteropServices;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>Starts the mod and runs its per-frame loop on the game's main thread.</summary>
internal static class Host
{
    /// <summary>The game version GameBridge is written against; any other turns the mod off.</summary>
    private const string PinnedVersion = "v0.113.1";

    public const string Tag = "[Sts2Ai]";

    private static Assist? _assist;
    private static UI.Panel? _panel;
    private static string? _off;
    private static string? _lastError;

    public static void Start()
    {
        ModLog.Start();
        string version = NGame.GetGameVersion();
        string modDir = Path.GetDirectoryName(typeof(Host).Assembly.Location)!;
        ModLog.Write("start", $"mod {ModVersion(modDir)} from {modDir}");
        ModLog.Write("start", $"game {version} ({ReleaseInfoManager.Instance.ReleaseInfo?.Branch ?? "no branch"}), " +
                              $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}; the game's log: {OS.GetUserDataDir()}/logs/godot.log");
        ModLog.Write("start", $"settings: {Settings.Current}");
        if (version != PinnedVersion)
        {
            _off = $"off: game {version}, the mod is for {PinnedVersion}";
            ModLog.Warn("start", _off);
        }
        else
        {
            // First, before anything else compiles game code that could inline an entry.
            var harmony = new Harmony("sts2ai.mod");
            ChoiceContexts.Patch(harmony);
            NodeRecord.Patch(harmony);
            Executor.Patch(harmony);
            SettingsTab.Patch(harmony);
            // The brain shipped beside the mod plays with the running game's own assemblies, and logs into this game
            // session's folder.
            var bundled = BundledBrain.Find(modDir, Path.GetDirectoryName(typeof(NGame).Assembly.Location)!, version, ModLog.Folder!);
            var link = BrainLink.Start(bundled);
            _assist = new Assist(link, version, bundled?.SchemaPath);
            ModLog.Info("start", $"assist mode, {Settings.Current.Env}, brain: {link.Where}");
        }
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Tick;
    }

    /// <summary>The version in the mod's manifest beside it.</summary>
    private static string ModVersion(string modDir)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(modDir, "Sts2Ai.json")));
        return manifest.RootElement.GetProperty("version").GetString()!;
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
            ModLog.Error("mod", error);
            _panel?.Show("—", L10n.T("error"), null, L10n.T("Mod error"), e.Message, Tone.Error);
        }
    }
}
