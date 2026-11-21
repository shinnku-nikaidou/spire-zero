using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// The console's jobs: the shipped simulator started beside the game (<see cref="BundledBrain.Job"/>) for a race (the
/// AI plays the run's seed from floor 1), a rollout (the rest of the run from the room's clone, N times, chance drawn
/// anew) or a scout (what the rooms ahead hold). Their output is read on threads of their own and handed to the game's
/// main thread (<see cref="Drain"/>), which writes it to the console. Each job runs with half the brain's search threads,
/// at the effort the player names; the game closing ends them.
/// </summary>
internal static class Jobs
{
    private sealed class Job(string kind, Process process)
    {
        public string Kind { get; } = kind;
        public Process Process { get; } = process;
        public long Started { get; } = Environment.TickCount64;
        public string Status = "starting";
    }

    private static readonly List<Job> Running = [];
    private static readonly ConcurrentQueue<Action> Main = new();

    static Jobs() => AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();

    /// <summary>On the game's main thread, each frame: what the jobs' threads handed over.</summary>
    public static void Drain()
    {
        while (Main.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ModLog.Warn("ai", $"job output: {e}");
            }
        }
    }

    private static BundledBrain Brain => BrainLink.Current?.Bundled ?? throw new AiError("no brain ships with this copy of the mod");

    private static int Threads => Math.Max(1, BundledBrain.Workers / 2);

    private static string Effort(string effort) => effort is "low" or "medium" or "high" ? effort : throw new AiError("effort low|medium|high");

    /// <summary>The run on screen: the game's state, the local player, and one player only.</summary>
    private static (RunState Run, MegaCrit.Sts2.Core.Entities.Players.Player Me) Run()
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me) throw new AiError("no run in progress");
        if (run.Players.Count > 1) throw new AiError("the simulator plays runs of one player");
        return (run, me);
    }

    private static List<string> Common(RunState run, MegaCrit.Sts2.Core.Entities.Players.Player me, string effort) =>
    [
        "--character", me.Character.Id.Entry, "--asc", run.AscensionLevel.ToString(CultureInfo.InvariantCulture), "--env", "SL",
        "--champion", "sl-champion.json", "--effort", Effort(effort), "--workers", Threads.ToString(CultureInfo.InvariantCulture),
    ];

    private static Job Start(string kind, List<string> args, Action<Job, string> line, Action<Job> ended)
    {
        if (Running.Any(j => j.Kind == kind)) throw new AiError($"a {kind} is running: ai jobs, ai stop");
        var job = new Job(kind, Brain.Job(args, $"{kind}.log"));
        Running.Add(job);
        new Thread(() =>
        {
            while (job.Process.StandardOutput.ReadLine() is { } text) Main.Enqueue(() => line(job, text));
            job.Process.WaitForExit();
            Main.Enqueue(() =>
            {
                Running.Remove(job);
                ended(job);
            });
        }) { IsBackground = true, Name = $"sts2ai {kind}" }.Start();
        return job;
    }

    /// <summary>The floor and HP of a progress line (progress FLOOR HP MAXHP), if it is one.</summary>
    private static bool Progress(string text, Job job)
    {
        var p = text.Split(' ');
        if (p.Length != 4 || p[0] != "progress") return false;
        job.Status = $"floor {p[1]}, HP {p[2]}/{p[3]}";
        return true;
    }

    // ---- race ----

    /// <summary><c>ai race</c>: the AI plays this run's seed from floor 1 (SL), beside the game; again, how it stands.</summary>
    public static string Race(string effort)
    {
        if (Running.FirstOrDefault(j => j.Kind == "race") is { } racing) return Out.Esc($"race: the AI is at {racing.Status}; you are on floor {Run().Run.TotalFloor}");
        var (run, me) = Run();
        string seeds = Path.Combine(Brain.Logs, "race-seed.txt");
        File.WriteAllText(seeds, run.Rng.StringSeed + "\n");
        var args = new List<string> { "run", "--seeds", seeds, "--progress" };
        args.AddRange(Common(run, me, effort));
        if (run.GameMode == GameMode.Dread) args.AddRange(["--mode", "dread"]);
        Start("race", args, (job, text) =>
        {
            if (Progress(text, job)) return;
            // The run's own line: #0 SEED OUTCOME summary ...
            if (text.StartsWith("#0 ", StringComparison.Ordinal))
            {
                var words = text.Split(' ');
                job.Status = $"{words.ElementAtOrDefault(2)} ({string.Join(' ', words.Skip(3).Take(4))})";
                int floor = RunManager.Instance.DebugOnlyGetState()?.TotalFloor ?? 0;
                Out.Later(Out.Esc($"race over: the AI {job.Status} on this seed; you are on floor {floor}"));
            }
        }, job => { if (job.Process.ExitCode != 0) Out.LaterError($"race ended (exit code {job.Process.ExitCode}; log: race.log)"); });
        return Out.Esc($"race started: the AI plays seed {run.Rng.StringSeed} from floor 1 at {effort}, {Threads} threads; ai race to see where it is");
    }

    // ---- rollout ----

    /// <summary><c>ai rollout</c>: the rest of this run, N times from the room's clone, by the AI (SL), chance drawn anew
    /// from the next map point on.</summary>
    public static string Rollout(int n, string effort)
    {
        if (n < 1 || n > 200) throw new AiError("rollout 1..200");
        var (run, me) = Run();
        if (NodeRecord.Checkpoint is not { } checkpoint || NodeRecord.Broken != null)
            throw new AiError($"no clone of this room: {NodeRecord.Broken ?? "no checkpoint yet"}");
        string node = Path.Combine(Brain.Logs, "rollout-node.json");
        File.WriteAllText(node, ProtocolJson.Serialize(new NodeClone(checkpoint, NodeRecord.Steps)));
        var args = new List<string> { "rollout", "--node", node, "--n", n.ToString(CultureInfo.InvariantCulture),
            "--seed", Environment.TickCount.ToString(CultureInfo.InvariantCulture) };
        args.AddRange(Common(run, me, effort));
        var ends = new List<(bool Won, int Floor)>();
        Start("rollout", args, (job, text) =>
        {
            if (Progress(text, job))
            {
                job.Status = $"{ends.Count + 1}/{n}: {job.Status}";
                return;
            }
            var w = text.Split(' ');
            if (w.Length < 4 || w[0] != "rollout" || !int.TryParse(w[3], out int floor)) return;
            ends.Add((w[2] == "won", floor));
            Out.Later(Out.Esc($"rollout {ends.Count}/{n}: {w[2]} at floor {floor}"));
            if (ends.Count < n) return;
            var floors = ends.Select(e => e.Floor).Order().ToList();
            Out.Later(Out.Esc($"rollouts: {ends.Count(e => e.Won)} won of {n} ({Out.Pct((double)ends.Count(e => e.Won) / n, "0")}); " +
                              $"floors {floors[0]}–{floors[^1]}, median {floors[floors.Count / 2]}"));
        }, job => { if (job.Process.ExitCode != 0) Out.LaterError($"rollout ended (exit code {job.Process.ExitCode}; log: rollout.log)"); });
        return Out.Esc($"rollout started: {n} times from this room at {effort}, {Threads} threads; each one's end comes here");
    }

    // ---- scout ----

    /// <summary>The spoiler (<see cref="ScoutConsoleCmd"/>): the game's save of this room walked on along
    /// <paramref name="route"/> by the game's own code, each line named in the game's language as it comes.</summary>
    public static string Scout(IReadOnlyList<string> route)
    {
        Run();
        if (NodeRecord.Checkpoint is not { } checkpoint) throw new AiError("no save of this room yet");
        string dir = Brain.Logs;
        string save = Path.Combine(dir, "scout.save");
        File.WriteAllText(save, Checkpoint.Open(checkpoint).Save);
        // Each act's steps; "/" goes on to the next act.
        var acts = new List<List<string>> { new() };
        foreach (var step in route)
            if (step == "/") acts.Add([]);
            else acts[^1].Add(step);
        string spec = Path.Combine(dir, "scout.json");
        File.WriteAllText(spec, System.Text.Json.JsonSerializer.Serialize(new { save, routes = acts }));
        Start("scout", ["scout", "--spec", spec], (_, text) => Out.Later(Out.Code(Describe.Names(text))),
            job => { if (job.Process.ExitCode != 0) Out.LaterError($"scout failed (exit code {job.Process.ExitCode}; log: scout.log)"); });
        return "scouting from this room's save…";
    }

    // ---- all of them ----

    public static string List()
    {
        var lines = new List<string>();
        if (Assist.Current is { } assist)
            lines.AddRange(assist.Questions.Select(q => $"question {q.Id}: {q.What}"));
        lines.AddRange(Running.Select(j => $"{j.Kind}: {j.Status} · {(Environment.TickCount64 - j.Started) / 1000} s · pid {j.Process.Id}"));
        return lines.Count == 0 ? "nothing open" : Out.Code(lines);
    }

    public static int StopAll()
    {
        int n = 0;
        foreach (var job in Running.ToList())
        {
            try
            {
                if (!job.Process.HasExited)
                {
                    job.Process.Kill(entireProcessTree: true);
                    n++;
                }
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }
        }
        return n;
    }
}
