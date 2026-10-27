using System.Text.Json;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>V (docs: learning §1): what a run is worth, on one scale for every option of every surface.</summary>
public interface IRunValue
{
    /// <summary>Which V this is, for the champion's name.</summary>
    string Name { get; }

    double Value(RunView run);
}

/// <summary>
/// V v0 (docs: learning §1): hand-written features of the run view times linear weights. The value is a logit of
/// P(win); only its order matters to the macro brain, which compares the afterstates of one decision. The initial
/// weights are hypotheses, in units of one HP, for fitting and tuning to replace (<see cref="ValueWeights"/>).
/// </summary>
public sealed class RunValue(IReadOnlyDictionary<string, double> weights, string name = "v0") : IRunValue
{
    /// <summary>Which weights these are, for the champion's name.</summary>
    public string Name { get; } = name;

    public static readonly IReadOnlyDictionary<string, double> InitialWeights = new Dictionary<string, double>
    {
        ["hp"] = 1,
        ["max_hp"] = 0.5,
        ["gold"] = 0.1,
        ["potions"] = 8,
        ["relics"] = 8,
        ["cards"] = -0.3,
        ["unplayable"] = -15,
        ["upgrades"] = 3,
        ["damage"] = 3,
        ["block"] = 2,
        ["draw"] = 4,
        ["elites_ahead"] = -5,
        ["rests_ahead"] = 4,
        ["shops_ahead"] = 1,
        ["treasures_ahead"] = 6,
        ["unknowns_ahead"] = 1,
    };

    public static RunValue Initial { get; } = new(InitialWeights);

    public static RunValue From(ValueWeights file) => new(file.Weights, file.Name);

    /// <summary>The V a value file holds: a net (<see cref="NetFile"/>, the file with an ONNX graph) or V v0's weights.</summary>
    public static IRunValue Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("onnx", out _)
            ? RunNet.Load(ProtocolJson.Deserialize<NetFile>(json))
            : From(ProtocolJson.Deserialize<ValueWeights>(json));
    }

    public double Value(RunView run) => Features(run).Sum(f => weights.GetValueOrDefault(f.Key) * f.Value);

    /// <summary>
    /// The features. Card numbers are means over the playable cards (damage counts poison too). Map counts are the
    /// expected number of points of each type from the current point (included) to the boss, if each step took a
    /// random path. The floor has no initial weight: it is the same for every option of a decision, and a fit needs it
    /// so that progress is not credited to what comes with it (relics, gold).
    /// </summary>
    public static Dictionary<string, double> Features(RunView run)
    {
        var playable = run.Deck.Where(c => c.Keywords?.Contains("Unplayable") != true).ToList();
        double Mean(Func<CardView, decimal> f) => playable.Count == 0 ? 0 : (double)playable.Sum(f) / playable.Count;
        var features = new Dictionary<string, double>
        {
            ["floor"] = run.Floor,
            ["hp"] = run.Hp,
            ["max_hp"] = run.MaxHp,
            ["gold"] = run.Gold,
            ["potions"] = run.Potions.Count(p => p != null),
            ["relics"] = run.Relics.Count,
            ["cards"] = run.Deck.Count,
            ["unplayable"] = run.Deck.Count - playable.Count,
            ["upgrades"] = run.Deck.Count(c => c.Up > 0),
            ["damage"] = Mean(c => Var(c, "Damage") + Var(c, "PoisonPower")),
            ["block"] = Mean(c => Var(c, "Block")),
            ["draw"] = Mean(c => Var(c, "Cards")),
        };
        foreach (var (type, name) in MapTypes) features[name] = 0;
        if (run.Map.Visited.Count > 0)
            foreach (var (type, count) in Ahead(run.Map, run.Map.Visited[^1]))
                if (MapTypes.TryGetValue(type, out var name)) features[name] = count;
        return features;
    }

    private static readonly Dictionary<string, string> MapTypes = new()
    {
        ["Elite"] = "elites_ahead", ["RestSite"] = "rests_ahead", ["Shop"] = "shops_ahead", ["Treasure"] = "treasures_ahead",
        ["Unknown"] = "unknowns_ahead",
    };

    private static decimal Var(CardView card, string name) => card.Vars?.GetValueOrDefault(name) ?? 0;

    /// <summary>The expected count of each point type from <paramref name="from"/> on, each next point equally likely.</summary>
    private static Dictionary<string, double> Ahead(MapView map, Coord from)
    {
        var points = map.Points.ToDictionary(p => new Coord(p.Col, p.Row));
        var memo = new Dictionary<Coord, Dictionary<string, double>>();
        Dictionary<string, double> Of(Coord at)
        {
            if (memo.TryGetValue(at, out var known)) return known;
            var counts = new Dictionary<string, double>();
            if (points.TryGetValue(at, out var point))
            {
                var next = point.Next.Select(Of).ToList();
                foreach (var type in next.SelectMany(n => n.Keys).Distinct())
                    counts[type] = next.Average(n => n.GetValueOrDefault(type));
                counts[point.Type] = counts.GetValueOrDefault(point.Type) + 1;
            }
            return memo[at] = counts;
        }
        return Of(from);
    }
}

/// <summary>V's weights as a file holds them (docs: learning §2): a name for reports, and a weight per feature. A
/// feature it leaves out weighs nothing.</summary>
public sealed record ValueWeights(string Name, IReadOnlyDictionary<string, double> Weights);
