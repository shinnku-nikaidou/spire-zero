using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// The fusion panel (docs: combat-brain §8, open question 2), which only measures: how much PIMC's strategy fusion
/// costs. It takes NoSL combat decisions from decision logs, with their observations and seeds; for each, K worlds are
/// built from the observation as pimc builds them, and each world's turn is enumerated in full. Each first action then
/// gets two values: PIMC's, the mean over the worlds of each world's best line; and the merged one, where the worlds
/// that still show the same observations must make the same choice (at each point, the best action for them all,
/// averaged over them). The merged value is what a player who sees only the observations can get, so PIMC's pick
/// loses the gap between the two picks' merged values.
///
/// The worlds show the same observations when they agree on everything but uids: a drawn card's uid is its place in
/// that world's shuffle, so it differs between worlds that draw the same card. Actions are compared the same way, by
/// the card's place in the hand and the target's among the creatures. A decision with a world over --max-nodes states
/// is skipped and counted.
/// </summary>
internal static class Fusion
{
    private const double Tie = 1e-9;

    private static readonly Option<string> Log = Opt.Required<string>("--log", "decision logs, comma-separated");
    private static readonly Option<int> N = Opt.Of("--n", 400, "decisions");
    private static readonly Option<int> Start = Opt.Of("--start", 0, "decisions skipped first");
    private static readonly Option<int> Every = Opt.Of("--every", 1, "takes every so many decisions");
    private static readonly Option<int> K = Opt.Of("--k", 32, "worlds per decision");
    private static readonly Option<int> MaxNodes = Opt.Of("--max-nodes", 5000, "a decision with a world over this many states is skipped");
    private static readonly Option<string?> Out = Opt.Maybe<string?>("--out", "one JSON line per decision");

    public static Command Command()
    {
        var command = Opt.Command("fusion", "the fusion panel: PIMC's values against merged ones over the logs' NoSL combat decisions (docs: combat-brain §8)",
            [Log, N, Start, Every, K, MaxNodes, Out]);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        var logs = r.GetValue(Log)!;
        int n = r.GetValue(N);
        int start = r.GetValue(Start);
        int every = r.GetValue(Every);
        int k = r.GetValue(K);
        int maxNodes = r.GetValue(MaxNodes);
        // Combat decisions with a choice, taken in NoSL by a searching brain, every --every-th of them.
        var picked = logs.Split(',').SelectMany(File.ReadLines).Select(line => JsonNode.Parse(line)!)
            .Where(e => (string?)e["surface"] == Surfaces.Combat && (string?)e["env"] == nameof(PlayEnvironment.NoSL) && (int)e["legal"]! > 1 && e["obs"] != null)
            .Where((_, j) => j % every == 0).Skip(start).Take(n).ToList();

        using var output = r.GetValue(Out) is { } outPath ? new StreamWriter(outPath) { AutoFlush = true } : null;
        int measured = 0, capped = 0, revealing = 0, disagree = 0, disagreeRevealing = 0;
        double loss = 0;
        Console.WriteLine($"fusion: {picked.Count} decisions from #{start} (every {every}), K {k}, max {maxNodes} states per world, " +
                          $"game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        foreach (var entry in picked)
        {
            var obs = ProtocolJson.Deserialize<CombatObservation>(entry["obs"]!.ToJsonString());
            ulong seed = (ulong)entry["seed"]!;
            string id = $"{entry["run"]}:{entry["decision_id"]}";
            var started = clock.Elapsed;
            Result? result;
            // As pimc's NoSL route: the build seed is drawn from the decision's seed.
            using (var worlds = SimWorlds.NoSL(pump, obs, seed.ToString("X16")))
                result = Measure(obs, worlds.Worlds(k, seed), maxNodes);
            double ms = (clock.Elapsed - started).TotalMilliseconds;
            if (result == null) capped++;
            else
            {
                measured++;
                bool worse = result.Loss > Tie;
                if (result.Reveals) revealing++;
                if (worse) disagree++;
                if (worse && result.Reveals) disagreeRevealing++;
                loss += result.Loss;
                Console.WriteLine($"{id} {obs.Encounter} round {obs.Round}: pimc {result.Pimc} merged {result.Merged} loss {result.Loss:F4} " +
                                  $"reveals {result.Reveals} states {result.States.Max()} {ms:F0} ms");
            }
            output?.WriteLine(JsonSerializer.Serialize(new
            {
                id, encounter = obs.Encounter, round = obs.Round, legal = (int)entry["legal"]!, k, capped = result == null,
                pimc = result?.Pimc, merged = result?.Merged, loss = result?.Loss, reveals = result?.Reveals,
                mismatches = result?.Mismatches, states = result?.States, values = result?.Values, ms,
            }));
        }
        clock.Stop();

        Console.WriteLine($"decisions measured {measured}, skipped over the state cap {capped}, with a reveal inside the turn {revealing}");
        Console.WriteLine($"pimc's pick worse by the merged value: {disagree} ({disagree / (double)Math.Max(1, measured):P1}; " +
                          $"{disagreeRevealing} of the {revealing} with a reveal), mean loss {loss / Math.Max(1, measured):F5} per decision; " +
                          $"{clock.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine($"game Log.Error lines: {HeadlessGame.TakeErrors().Count}");
        return 0;
    }

    /// <param name="Pimc">PIMC's pick, in the form that compares across worlds.</param>
    /// <param name="Merged">The pick by merged values.</param>
    /// <param name="Loss">The merged value of the merged pick less that of PIMC's.</param>
    /// <param name="Reveals">Whether the worlds came to show different observations before the turn ended.</param>
    /// <param name="Mismatches">Points where worlds showing the same observation had different legal actions.</param>
    /// <param name="States">The states enumerated in each world.</param>
    /// <param name="Values">Each first action's PIMC and merged value.</param>
    private sealed record Result(string Pimc, string Merged, double Loss, bool Reveals, int Mismatches, int[] States,
        Dictionary<string, double[]> Values);

    /// <summary>Thrown when a world has more states than the cap.</summary>
    private sealed class CappedException : Exception;

    /// <summary>Null when a world is over the cap.</summary>
    private static Result? Measure(CombatObservation obs, IReadOnlyList<ICombatWorld> worlds, int maxNodes)
    {
        var trees = new List<Tree>();
        try
        {
            foreach (var world in worlds) trees.Add(Tree.Enumerate(world, obs.Round, maxNodes));
        }
        catch (CappedException)
        {
            return null;
        }
        var merge = new Merge(trees);
        var root = trees.Select((_, w) => (w, 0)).ToList();
        var first = trees[0].Nodes[0].Edges.Keys.ToList();
        var values = new Dictionary<string, double[]>();
        foreach (var action in first)
        {
            double pimc = trees.Average(t => t.Value(t.Nodes[0].Edges[action]));
            values[action] = [Math.Round(pimc, 6), Math.Round(merge.Of(root, action), 6)];
        }
        // Ties go to the first action in the order the search tries them, as in PimcBrain.
        string byPimc = first.MaxBy(a => values[a][0])!, byMerged = first.MaxBy(a => values[a][1])!;
        return new(byPimc, byMerged, values[byMerged][1] - values[byPimc][1], merge.Reveals, merge.Mismatches,
            trees.Select(t => t.Nodes.Count).ToArray(), values);
    }

    /// <summary>An edge to a state still in the turn (<paramref name="Child"/>), or to the end of the turn or combat,
    /// with its leaf value (<paramref name="Child"/> is -1).</summary>
    private readonly record struct Edge(int Child, double Leaf);

    /// <param name="Key">The observation without uids, which worlds compare.</param>
    /// <param name="Edges">By the action as worlds compare it, in the order the search tries them.</param>
    private sealed record Node(ulong Key, Dictionary<string, Edge> Edges);

    /// <summary>One world's turn, every line of it: states merged by their observation, as PIMC's search merges them.</summary>
    private sealed class Tree
    {
        public List<Node> Nodes { get; } = [];
        private readonly Dictionary<ulong, int> _index = [];
        private readonly Dictionary<int, double> _best = [];

        public static Tree Enumerate(ICombatWorld world, int round, int maxNodes)
        {
            var tree = new Tree();
            tree.Visit(world, world.Observe(), round, maxNodes);
            return tree;
        }

        /// <summary>PIMC's value of an edge in this world: the best line after it.</summary>
        public double Value(Edge edge)
        {
            if (edge.Child < 0) return edge.Leaf;
            if (_best.TryGetValue(edge.Child, out var known)) return known;
            return _best[edge.Child] = Nodes[edge.Child].Edges.Values.Max(Value);
        }

        private int Visit(ICombatWorld world, CombatObservation obs, int round, int maxNodes)
        {
            ulong full = ProtocolJson.Hash(obs);
            if (_index.TryGetValue(full, out var known)) return known;
            if (Nodes.Count >= maxNodes) throw new CappedException();
            int i = Nodes.Count;
            _index[full] = i;
            var node = new Node(Key(obs), []);
            Nodes.Add(node);
            var here = world.Snapshot();
            bool moved = false;
            foreach (var action in PimcBrain.Ordered(world.Legal()))
            {
                if (moved) world.Restore(here);
                moved = true;
                world.Execute(action);
                var status = world.Status;
                var after = world.Observe();
                node.Edges[Canonical(action, obs)] = status != CombatStatus.InProgress || action.Kind == CombatActionKind.EndTurn || after.Round != round
                    ? new(-1, LeafValue.Of(status, after))
                    : new(Visit(world, after, round, maxNodes), 0);
            }
            return i;
        }
    }

    /// <summary>
    /// Merged values: a group of worlds, each at its own state, that show the same observation and so must take the
    /// same action. After the action the group splits by what each world then shows.
    /// </summary>
    private sealed class Merge(List<Tree> trees)
    {
        private readonly Dictionary<string, double> _memo = [];

        public bool Reveals { get; private set; }
        public int Mismatches { get; private set; }

        /// <summary>The best merged value of the group.</summary>
        private double Best(List<(int World, int Node)> group)
        {
            string key = string.Join(",", group.Select(g => $"{g.World}:{g.Node}"));
            if (_memo.TryGetValue(key, out var known)) return known;
            var actions = group.Skip(1).Aggregate(trees[group[0].World].Nodes[group[0].Node].Edges.Keys.ToList(),
                (common, g) => common.Intersect(trees[g.World].Nodes[g.Node].Edges.Keys).ToList());
            if (group.Any(g => trees[g.World].Nodes[g.Node].Edges.Count != actions.Count)) Mismatches++;
            return _memo[key] = actions.Max(a => Of(group, a));
        }

        /// <summary>The merged value of <paramref name="action"/> for the group.</summary>
        public double Of(List<(int World, int Node)> group, string action)
        {
            double total = 0;
            var next = new Dictionary<ulong, List<(int World, int Node)>>();
            foreach (var (w, node) in group)
            {
                var edge = trees[w].Nodes[node].Edges[action];
                if (edge.Child < 0) total += edge.Leaf;
                else
                {
                    ulong key = trees[w].Nodes[edge.Child].Key;
                    if (!next.TryGetValue(key, out var same)) next[key] = same = [];
                    same.Add((w, edge.Child));
                }
            }
            if (next.Count > 1) Reveals = true;
            foreach (var same in next.Values) total += same.Count * Best(same);
            return total / group.Count;
        }
    }

    /// <summary>The observation without uids: what worlds that drew the same cards in another order of the pile share.</summary>
    private static ulong Key(CombatObservation obs) => ProtocolJson.Hash(obs with
    {
        Player = obs.Player with { Uid = null },
        Hand = obs.Hand.Select(c => c with { Uid = null }).ToList(),
        Allies = obs.Allies.Select(c => c with { Uid = null }).ToList(),
        Enemies = obs.Enemies.Select(c => c with { Uid = null }).ToList(),
    });

    /// <summary>The action by the card's place in the hand and the target's among the creatures (player, allies, enemies).</summary>
    private static string Canonical(CombatAction action, CombatObservation obs)
    {
        var creatures = new List<uint?> { obs.Player.Uid };
        creatures.AddRange(obs.Allies.Select(c => c.Uid));
        creatures.AddRange(obs.Enemies.Select(c => c.Uid));
        string target = action.Target is { } t ? $":{creatures.IndexOf(t)}" : "";
        return action.Kind switch
        {
            CombatActionKind.PlayCard => $"play:{obs.Hand.ToList().FindIndex(c => c.Uid == action.Card)}{target}",
            CombatActionKind.UsePotion => $"potion:{action.PotionSlot}{target}",
            _ => "end_turn",
        };
    }
}
