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

    /// <summary>What the game's launch options need, where SELinux would undo the mod's patches (<see cref="PatchesUndone"/>).</summary>
    private const string LaunchOption = "DOTNET_EnableWriteXorExecute=0 %command%";

    private static Assist? _assist;
    private static UI.Panel? _panel;
    /// <summary>Why the mod is off, in English (shown in the game's language), or null.</summary>
    private static string? _off;

    /// <summary>Whether the mod is off for <see cref="PatchesUndone"/>, which the launch option fixes.</summary>
    private static bool _offForSeLinux;

    /// <summary>The notice that tells the player the launch option, once the game's language is known.</summary>
    private static UI.Notice? _notice;
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
        else if (PatchesUndone())
        {
            // The game's language loads after the mods: the panel translates it each frame.
            (_off, _offForSeLinux) = ("SELinux would undo the mod's hooks", true);
            ModLog.Warn("start", "off: SELinux enforces and denies execheap, and .NET's W^X is on, so a Harmony patch is undone " +
                                 $"once .NET recompiles its method (after about 30 calls); the game's launch options need {LaunchOption}");
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

    /// <summary>
    /// Whether this machine would undo the mod's Harmony patches. .NET recompiles a method after about 30 calls, and
    /// Harmony's MonoMod writes the patch into the new code; with .NET's W^X on (its default), SELinux in enforcing mode
    /// denies that write (execheap, EACCES), unless its boolean allows execheap. MonoMod logs it only to its own debug
    /// log, and the patch is gone for the rest of the game: in the user's two real games of 2026-11-16 the save's and
    /// the turn end's prefixes stopped at their 31st call, each at an execheap denial in the audit log. W^X off (an
    /// environment variable the runtime reads at its start, so only a launch option can set it) leaves no denial.
    /// </summary>
    private static bool PatchesUndone()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if ((System.Environment.GetEnvironmentVariable("DOTNET_EnableWriteXorExecute") ?? System.Environment.GetEnvironmentVariable("COMPlus_EnableWriteXorExecute")) == "0")
            return false;
        static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        return Read("/sys/fs/selinux/enforce") == "1" && Read("/sys/fs/selinux/booleans/selinuxuser_execheap")?.StartsWith('1') != true;
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
            // Once, in the game's language, which loads after the mods.
            if (_offForSeLinux && _notice == null && MegaCrit.Sts2.Core.Localization.LocManager.Instance?.Language != null)
                _notice = new UI.Notice((SceneTree)Engine.GetMainLoop(), L10n.T("Spire Zero is off"),
                    L10n.T("SELinux on this computer silently removes the mod's hooks partway through a game, and the AI then stops. " +
                           "Add this line to the game's launch options in Steam (Properties, Launch Options), then restart the game."),
                    LaunchOption);
            if (assist == null)
                _panel.Show("—", L10n.T("off"), null, L10n.T(_off!), _offForSeLinux ? L10n.T("Steam launch options: {0}", LaunchOption) : null,
                    _offForSeLinux ? Tone.Warn : Tone.Plain);
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
