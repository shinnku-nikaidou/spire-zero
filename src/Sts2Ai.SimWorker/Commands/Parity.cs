using System.CommandLine;
using System.Diagnostics;
using MegaCrit.Sts2.Core.Models;
using Microsoft.Extensions.Logging;
using Sts2Ai.Engine;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// parity: the lockstep check (<see cref="Lockstep"/>) over a sweep of combats (<see cref="Sweep"/>): a second world
/// held against the real game at every decision. One JSON line per combat (--out), and a summary: the differences by
/// the first path that differs and by what the action acts with, and for every id of the game how often it was met and
/// how often it differed (--coverage has the whole table). Exit code 0: every combat checked, no difference.
/// </summary>
internal static class ParityCommand
{
    private static readonly Option<string> World = Opt.Of("--world", "replay",
        "the second world: engine, the Rust engine's, made from the combat written out along its schema; replay, the SL world that replays the combat; " +
        "determinized, the same with its hidden state drawn again, which must differ after actions")
        .AcceptOnlyFromAmong("engine", "replay", "determinized");
    private static readonly Option<string?> Exports = Opt.Maybe<string?>("--exports",
        "with --world engine: a folder that gets every export written, as COMBAT-DECISION.json");
    private static readonly Option<string> Character = Opt.Of("--character", "all", "the characters, such as SILENT,FOOL, or all");
    private static readonly Option<string> Asc = Opt.Of("--asc", "0,10", "the ascension levels");
    private static readonly Option<string> Encounter = Opt.Of("--encounter", "all", "the encounters, such as A,B, or all: every encounter of the game, the ones list shows and the events' besides");
    private static readonly Option<int> Decks = Opt.Of("--decks", 2,
        "loadouts per character, ascension and encounter: the first is the starting one, the others are drawn (deck, upgrades, enchantments, relics, potions, HP)");
    private static readonly Option<string?> Deck = Opt.Maybe<string?>("--deck", "this deck in every combat instead, such as A*5,B+,C+2@SHARP:2 (as combat's)");
    private static readonly Option<string?> Relics = Opt.Maybe<string?>("--relics", "these relics in every combat instead");
    private static readonly Option<string?> Potions = Opt.Maybe<string?>("--potions", "these potions in every combat instead");
    private static readonly Option<int?> Hp = Opt.Maybe<int?>("--hp", "this HP in every combat instead");
    private static readonly Option<int?> MaxHp = Opt.Maybe<int?>("--max-hp", "this max HP in every combat instead");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base: of the game's seeds, the drawn loadouts and the player");
    private static readonly Option<int> Start = Opt.Of("--start", 0, "the first combat's index in the sweep");
    private static readonly Option<int?> N = Opt.Maybe<int?>("--n", "combats (default: to the sweep's end)");
    private static readonly Option<int?> Continue = Opt.Maybe<int?>("--continue",
        "how many decisions of a combat the continue check starts from, drawn evenly (default: every one, which costs the square of the combat's length)");
    private static readonly Option<int> Workers = Opt.Of("--workers", 1, "processes that share the sweep");
    private static readonly Option<string?> Part = Opt.Maybe<string?>("--part", "K/N: only the combats whose index is K modulo N, one of N parts of a sweep");
    private static readonly Option<string?> Out = Opt.Maybe<string?>("--out", "one JSON line per combat goes here; -: to stdout, in place of the summary");
    private static readonly Option<string?> Coverage = Opt.Maybe<string?>("--coverage", "the coverage table goes here, one id a line (tab-separated)");
    private static readonly Option<bool> Full = Opt.Of("--full", false, "the lines keep each difference's values whole, however long");
    private static readonly Option<string[]> Merge = new("--merge")
    {
        Description = "plays nothing: the summary of these files of lines, the parts of one sweep",
        AllowMultipleArgumentsPerToken = true,
    };

    /// <summary>The options a part of the sweep takes from the whole: all but where the output goes and who shares it.</summary>
    private static readonly Option[] Shared = [World, Exports, Character, Asc, Encounter, Decks, Deck, Relics, Potions, Hp, MaxHp, Seed, Start, N, Continue, Full];

    public static Command Command()
    {
        var command = Opt.Command("parity", "the lockstep check: a second world held against the real game at every decision of a sweep of combats",
            [.. Shared, Workers, Part, Out, Coverage, Merge]);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        var summary = new ParitySummary();
        var universe = GameIds.All();
        if (r.GetValue(Merge) is { Length: > 0 } files)
        {
            foreach (var line in files.SelectMany(File.ReadLines)) summary.Add(ProtocolJson.Deserialize<ParityLine>(line));
            return Finish(r, summary, universe, $"parity: the lines of {files.Length} files, game {HeadlessGame.GameVersion}");
        }

        var characters = r.GetValue(Character) is "all" ? ModelDb.AllCharacters.ToList()
            : [.. r.GetValue(Character)!.Split(',').Select(id => ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliException($"no character {id} (see list)"))];
        var ascensions = r.GetValue(Asc)!.Split(',').Select(a => int.TryParse(a, out int level) ? level : throw new CliException($"--asc {a} is no ascension level")).ToList();
        var encounters = Sweep.AllEncounters();
        if (r.GetValue(Encounter) is not "all")
            encounters = [.. r.GetValue(Encounter)!.Split(',').Select(id => encounters.Where(e => e.Encounter.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase))
                .DefaultIfEmpty().First() is { Encounter: not null } found ? found : throw new CliException($"no encounter {id} (see list)"))];
        var given = Loadout.Parse(r.GetValue(Deck), r.GetValue(Relics), r.GetValue(Potions), r.GetValue(Hp), r.GetValue(MaxHp));
        int decks = r.GetValue(Decks);
        if (decks < 1) throw new CliException("--decks is at least 1");
        var sweep = new Sweep(characters, ascensions, encounters, decks, r.GetValue(Seed), given == new Loadout() ? null : given);

        int start = r.GetValue(Start);
        int end = Math.Min(sweep.Count, start + (r.GetValue(N) ?? sweep.Count));
        var (part, parts) = (0, 1);
        if (r.GetValue(Part) is { } text)
            (part, parts) = text.Split('/') is [var k, var n] && int.TryParse(k, out int kk) && int.TryParse(n, out int nn) && kk >= 0 && kk < nn ? (kk, nn)
                : throw new CliException($"--part {text} is not K/N with K from 0 to N - 1");
        var indexes = Enumerable.Range(start, Math.Max(0, end - start)).Where(i => i % parts == part).ToList();
        int workers = r.GetValue(Workers);
        if (workers < 1) throw new CliException("--workers is at least 1");
        if (workers > 1 && parts > 1) throw new CliException("--workers shares a whole sweep: not with --part");

        bool toStdout = r.GetValue(Out) == "-";
        using var file = !toStdout && r.GetValue(Out) is { } path ? new StreamWriter(path) : null;
        var output = toStdout ? Console.Out : file;
        var clock = Stopwatch.StartNew();
        long reported = 0;
        void Take(ParityLine line)
        {
            lock (summary)
            {
                summary.Add(line);
                output?.WriteLine(ProtocolJson.Serialize(line));
                if (toStdout || clock.ElapsedMilliseconds - reported < 30_000) return;
                reported = clock.ElapsedMilliseconds;
                Logs.Sim.LogInformation("parity: {Done} of {All} combats, {Seconds:F0} s", summary.Combats, indexes.Count, clock.Elapsed.TotalSeconds);
            }
        }

        string world = r.GetValue(World)!;
        string? exports = r.GetValue(Exports);
        if (exports != null && world != "engine") throw new CliException("--exports is the engine's: it needs --world engine");
        if (workers == 1)
        {
            // The engine's schema is read once; each combat has its own maker, which counts what it wrote.
            var export = world == "engine" ? new EngineExport(EngineSchema.Load(EngineLibrary.SchemaPath)) : null;
            if (exports != null) Directory.CreateDirectory(exports);
            ISecondWorlds replay = new ReplayWorlds(pump, determinized: world == "determinized");
            foreach (int i in indexes)
                Take(Play(pump, sweep[i], export == null ? replay : new EngineWorlds(export, exports == null ? null : Path.Combine(exports, $"{i}")),
                    r.GetValue(Continue), r.GetValue(Full)));
        }
        else
        {
            // Each worker plays a part of the sweep and writes its lines to its stdout; its logs go to this stderr.
            var common = Shared.Where(o => r.GetResult(o) is { Implicit: false }).SelectMany(o => r.GetResult(o)!.Tokens.Select(t => t.Value).Prepend(o.Name)).ToList();
            var self = Program.SelfCommand();
            var running = Enumerable.Range(0, workers).Select(k =>
            {
                var process = Process.Start(new ProcessStartInfo(self[0], [.. self.Skip(1), "parity", .. common, "--part", $"{k}/{workers}", "--out", "-"])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                }) ?? throw new InvalidOperationException("the worker did not start");
                var reader = Task.Run(() =>
                {
                    while (process.StandardOutput.ReadLine() is { } line)
                        if (line.StartsWith('{')) Take(ProtocolJson.Deserialize<ParityLine>(line));
                });
                return (Process: process, Reader: reader, Part: k);
            }).ToList();
            foreach (var worker in running)
            {
                worker.Reader.Wait();
                worker.Process.WaitForExit();
                if (worker.Process.ExitCode is not (0 or 1)) throw new InvalidOperationException($"the worker of part {worker.Part} ended with exit code {worker.Process.ExitCode}");
            }
            if (summary.Combats != indexes.Count) throw new InvalidOperationException($"the workers played {summary.Combats} of {indexes.Count} combats");
        }
        clock.Stop();
        if (toStdout) return summary.Clean ? 0 : 1;

        string continues = r.GetValue(Continue) is { } few ? $"continued from {few} decisions a combat" : "continued from every decision";
        return Finish(r, summary, universe,
            $"parity: world {world}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}, {indexes.Count} of the sweep's {sweep.Count} combats "
            + $"({characters.Count} characters x {ascensions.Count} ascensions x {encounters.Count} encounters x {decks} loadouts), seed {r.GetValue(Seed)}, "
            + $"{continues}, {clock.Elapsed.TotalSeconds:F0} s with {workers} {(workers == 1 ? "process" : "processes")}");
    }

    private static int Finish(ParseResult r, ParitySummary summary, Dictionary<string, List<string>> universe, string header)
    {
        Console.WriteLine(header);
        summary.Print(Console.Out, universe);
        if (r.GetValue(Coverage) is { } path) summary.WriteCoverage(path, universe);
        return summary.Clean ? 0 : 1;
    }

    private static ParityLine Play(Pump pump, SweepCombat combat, ISecondWorlds worlds, int? continues, bool full)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var result = Lockstep.Check(pump, combat.Setup, combat.PolicySeed, worlds, continues);
            var line = ParityLine.Of(combat, result, clock.Elapsed.TotalMilliseconds, full);
            return worlds is not EngineWorlds { Written: var written } engine ? line
                : line with { Exports = written.Count, ExportBytes = written.Bytes, ExportMs = Math.Round(written.Ms, 1), Outside = engine.Outside };
        }
        catch (Exception e)
        {
            Logs.Sim.LogError(e, "combat #{Index} failed", combat.Index);
            return ParityLine.Failed(combat, e, clock.Elapsed.TotalMilliseconds);
        }
    }
}
