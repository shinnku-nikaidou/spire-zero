using MegaCrit.Sts2.Core.Models;

namespace Sts2Ai.SimWorker.Runtime;

/// <summary>Canonical game models by id entry, the form observations and the command line use ("STRIKE_SILENT").</summary>
internal static class Models
{
    private static readonly Dictionary<(Type, string), AbstractModel> Cache = [];

    public static T Find<T>(string id) where T : AbstractModel
    {
        if (Cache.TryGetValue((typeof(T), id), out var hit)) return (T)hit;
        var model = ModelDb.All.OfType<T>().FirstOrDefault(m => m.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no {typeof(T).Name.Replace("Model", "").ToLowerInvariant()} {id}");
        Cache[(typeof(T), id)] = model;
        return model;
    }
}
