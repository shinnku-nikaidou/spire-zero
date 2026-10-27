using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>
/// A rest site: "rest:ID" picks an enabled option (HEAL, SMITH, ...); "leave" once proceeding is allowed, i.e. after
/// an option succeeded or when none is enabled.
/// </summary>
public static class RestActions
{
    public const string Leave = "leave";

    public static RestScreen Screen(IReadOnlyList<RestSiteOption> options, Player player) => new()
    {
        Options = options.Select(o => new RestOptionView(o.OptionId, o.IsEnabled,
            o is HealRestSiteOption ? (int)HealRestSiteOption.GetHealAmount(player) : null)).ToList(),
    };

    public static List<string> Legal(IReadOnlyList<RestSiteOption> options, bool anySucceeded)
    {
        var actions = options.Where(o => o.IsEnabled).Select(o => $"rest:{o.OptionId}").ToList();
        if (anySucceeded || actions.Count == 0) actions.Add(Leave);
        return actions;
    }

    /// <summary>
    /// Picks the option as its button does; the task ends when it is done (true if it succeeded, e.g. not cancelled).
    /// "leave" changes nothing in the game: the caller opens the map.
    /// </summary>
    public static Task<bool> Start(IReadOnlyList<RestSiteOption> options, bool anySucceeded, string action)
    {
        if (!Legal(options, anySucceeded).Contains(action)) throw ActionText.Illegal(action, Surfaces.Rest);
        if (action == Leave) return Task.FromResult(true);
        int i = options.ToList().FindIndex(o => $"rest:{o.OptionId}" == action);
        return RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(i);
    }
}
