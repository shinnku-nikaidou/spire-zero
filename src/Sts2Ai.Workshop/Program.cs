using System.Runtime.InteropServices;
using System.Text;
using Steamworks;

namespace Sts2Ai.Workshop;

/// <summary>
/// The mod's Steam Workshop item, created and uploaded as the game (app 2868840) through the Steam client that is running
/// and logged in on this machine, so no password is typed anywhere. Run from the repository's root:
/// <code>
///   workshop check                 what Steam says: logged on, the game's branch, the item and how it stands
///   workshop create                a new item, its id written to distro/workshop/item.txt (once)
///   workshop upload --content DIR [--preview FILE] [--visibility private|friends|unlisted|public]
///                   [--branches MIN:MAX] [--note TEXT]
/// </code>
/// The texts are distro/workshop/LANGUAGE.bbcode, LANGUAGE Steam's API language code: the title on the first line, the
/// description after the blank line below it. Steam takes one language's title and description an update, so an upload
/// is one update with the content, the preview, the settings and the English texts, then one update for each other
/// language.
/// </summary>
internal static class Program
{
    private static readonly AppId_t Game = new(2868840);
    private const string Dir = "distro/workshop";
    private static string ItemFile => Path.Combine(Dir, "item.txt");

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("check" or "create" or "upload"))
        {
            Console.Error.WriteLine("usage: workshop check | create | upload --content DIR [--preview FILE] [--visibility private|friends|unlisted|public] [--branches MIN:MAX] [--note TEXT]");
            return 2;
        }
        // The Steam API takes the app from the process environment when Steam did not start the process. On Unix .NET
        // keeps its own copy of the environment, so the variable is set in the C library's.
        if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("SteamAppId", Game.ToString());
        else if (setenv("SteamAppId", Game.ToString(), 1) != 0) throw new InvalidOperationException("setenv failed");
        if (SteamAPI.InitEx(out string why) != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
        {
            Console.Error.WriteLine($"workshop: Steam refused: {why} (Steam must be running and logged in, with the game in the library)");
            return 1;
        }
        try
        {
            return args[0] switch
            {
                "check" => Check(),
                "create" => Create(),
                _ => Upload(Options(args[1..])),
            };
        }
        catch (WorkshopException e)
        {
            Console.Error.WriteLine($"workshop: {e.Message}");
            return 1;
        }
        finally
        {
            SteamAPI.Shutdown();
        }
    }

    private static int Check()
    {
        string branch = SteamApps.GetCurrentBetaName(out var beta, 128) ? beta : "public";
        Console.WriteLine($"Steam: logged on {SteamUser.BLoggedOn()}; the game's branch {branch}, build {SteamApps.GetAppBuildId()}");
        if (Item() is not { } id)
        {
            Console.WriteLine($"no item yet ({ItemFile} is missing): workshop create");
            return 0;
        }
        var query = SteamUGC.CreateQueryUGCDetailsRequest([id], 1);
        var done = Wait<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(query));
        try
        {
            if (done.m_eResult != EResult.k_EResultOK) throw new WorkshopException($"the query of item {id} came back {done.m_eResult}");
            if (!SteamUGC.GetQueryUGCResult(query, 0, out var details)) throw new WorkshopException($"no details for item {id}");
            Console.WriteLine($"item {id} ({details.m_eResult}): \"{details.m_rgchTitle}\", {details.m_eVisibility}, "
                              + $"{details.m_nFileSize / 1e6:F1} MB, updated {DateTimeOffset.FromUnixTimeSeconds(details.m_rtimeUpdated):u}");
            Console.WriteLine(Page(id));
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(query);
        }
        return 0;
    }

    private static int Create()
    {
        if (Item() is { } existing) throw new WorkshopException($"the item exists already: {existing} ({ItemFile})");
        var created = Wait<CreateItemResult_t>(SteamUGC.CreateItem(Game, EWorkshopFileType.k_EWorkshopFileTypeCommunity));
        if (created.m_eResult != EResult.k_EResultOK) throw new WorkshopException($"Steam did not create the item: {created.m_eResult}");
        var id = created.m_nPublishedFileId;
        File.WriteAllText(ItemFile, $"{id}\n");
        Console.WriteLine($"created item {id}, written to {ItemFile}: {Page(id)}");
        Agreement(created.m_bUserNeedsToAcceptWorkshopLegalAgreement);
        return 0;
    }

    private static int Upload(Dictionary<string, string> options)
    {
        var id = Item() ?? throw new WorkshopException($"no item yet ({ItemFile} is missing): workshop create");
        string content = Path.GetFullPath(options.GetValueOrDefault("content") ?? throw new WorkshopException("upload needs --content DIR"));
        if (!Directory.Exists(content)) throw new WorkshopException($"no folder {content}");
        var texts = Texts();
        bool agreement = false;
        foreach (var (language, title, description) in texts)
        {
            bool first = language == texts[0].Language;
            var update = SteamUGC.StartItemUpdate(Game, id);
            Must(SteamUGC.SetItemUpdateLanguage(update, language), $"the language {language}");
            Must(SteamUGC.SetItemTitle(update, title), $"the {language} title");
            Must(SteamUGC.SetItemDescription(update, description), $"the {language} description ({Encoding.UTF8.GetByteCount(description)} bytes)");
            if (first)
            {
                Must(SteamUGC.SetItemContent(update, content), $"the content {content}");
                if (options.GetValueOrDefault("preview") is { } preview) Must(SteamUGC.SetItemPreview(update, Path.GetFullPath(preview)), $"the preview {preview}");
                if (options.GetValueOrDefault("visibility") is { } visibility) Must(SteamUGC.SetItemVisibility(update, Visibility(visibility)), $"the visibility {visibility}");
                if (options.GetValueOrDefault("branches") is { } branches)
                {
                    var range = branches.Split(':');
                    if (range.Length != 2) throw new WorkshopException($"--branches is MIN:MAX, not {branches}");
                    Must(SteamUGC.SetRequiredGameVersions(update, range[0], range[1]), $"the branches {branches}");
                }
            }
            var submitted = Wait<SubmitItemUpdateResult_t>(SteamUGC.SubmitItemUpdate(update, first ? options.GetValueOrDefault("note") : null),
                () => first ? Progress(update) : null);
            if (submitted.m_eResult != EResult.k_EResultOK) throw new WorkshopException($"Steam refused the {language} update: {submitted.m_eResult}");
            agreement |= submitted.m_bUserNeedsToAcceptWorkshopLegalAgreement;
            Console.WriteLine(first ? $"uploaded {content} with the {language} texts" : $"the {language} texts");
        }
        Console.WriteLine($"item {id}: {Page(id)}");
        Agreement(agreement);
        return 0;
    }

    /// <summary>The texts, English first: Steam shows it where the player's language has none.</summary>
    private static List<(string Language, string Title, string Description)> Texts()
    {
        var texts = new List<(string, string, string)>();
        foreach (var file in Directory.GetFiles(Dir, "*.bbcode").OrderBy(f => Path.GetFileNameWithoutExtension(f) != "english").ThenBy(f => f, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file).ReplaceLineEndings("\n");
            int end = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (end <= 0) throw new WorkshopException($"{file}: the title, a blank line, then the description");
            texts.Add((Path.GetFileNameWithoutExtension(file), text[..end].Trim(), text[(end + 2)..].Trim()));
        }
        if (texts.Count == 0 || texts[0].Item1 != "english") throw new WorkshopException($"{Dir}/english.bbcode is missing");
        return texts;
    }

    private static string? Progress(UGCUpdateHandle_t update)
    {
        var status = SteamUGC.GetItemUpdateProgress(update, out ulong done, out ulong total);
        return total > 0 ? $"{status}: {done / 1e6:F0} of {total / 1e6:F0} MB" : $"{status}";
    }

    /// <summary>Runs Steam's callbacks until the call's result comes, saying what <paramref name="state"/> reads now and then.</summary>
    private static T Wait<T>(SteamAPICall_t call, Func<string?>? state = null) where T : struct
    {
        T? got = null;
        bool failed = false;
        using var result = CallResult<T>.Create((r, io) => (got, failed) = (r, io));
        result.Set(call);
        string? said = null;
        for (int tick = 0; got == null; tick++)
        {
            SteamAPI.RunCallbacks();
            if (tick % 20 == 0 && state?.Invoke() is { } now && now != said) Console.WriteLine(said = now);
            Thread.Sleep(50);
        }
        if (failed) throw new WorkshopException($"Steam's call for {typeof(T).Name} failed on its way");
        return got.Value;
    }

    private static void Agreement(bool needed)
    {
        if (needed) Console.WriteLine("The item stays hidden until the Steam Workshop agreement is accepted: https://steamcommunity.com/sharedfiles/workshoplegalagreement");
    }

    private static PublishedFileId_t? Item() =>
        File.Exists(ItemFile) ? new PublishedFileId_t(ulong.Parse(File.ReadAllText(ItemFile).Trim())) : null;

    private static string Page(PublishedFileId_t id) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";

    private static ERemoteStoragePublishedFileVisibility Visibility(string name) => name switch
    {
        "public" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic,
        "friends" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
        "private" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate,
        "unlisted" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted,
        _ => throw new WorkshopException($"--visibility is private, friends, unlisted or public, not {name}"),
    };

    private static void Must(bool ok, string what)
    {
        if (!ok) throw new WorkshopException($"Steam did not take {what}");
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new WorkshopException($"options are --NAME VALUE: {string.Join(' ', args)}");
            options[args[i][2..]] = args[i + 1];
        }
        var unknown = options.Keys.Except(["content", "preview", "visibility", "branches", "note"]).ToList();
        if (unknown.Count > 0) throw new WorkshopException($"unknown option --{unknown[0]}");
        return options;
    }

    [DllImport("libc")]
    private static extern int setenv(string name, string value, int overwrite);
}

internal sealed class WorkshopException(string message) : Exception(message);
