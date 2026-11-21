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
/// With --runs the combats are those of whole runs (<see cref="RunLockstep"/>), each with the run around it.
/// </summary>
internal static class ParityCommand
{
    private static readonly Option<string> World = Opt.Of("--world", "replay",
        "the second world: engine, the Rust engine's, made from the combat written out along its schema; replay, the SL world that replays the combat; " +
        "determinized, the same with its hidden state drawn again, which must differ after actions; none: the combats are only played, for --dump")
        .AcceptOnlyFromAmong("engine", "replay", "determinized", "none");
    private static readonly Option<string?> Dump = Opt.Maybe<string?>("--dump",
        "a folder that gets every combat as files, for the engine's own tests: at each decision the combat as the engine imports it, what the game shows " +
        "and the action it was given (the folder's README.md has the layout)");
    private static readonly Option<string?> Delegates = Opt.Maybe<string?>("--delegates",
        "a file that gets a census of the delegates the combats' state holds (DelegateCensus): one row for each field and method with its count, and, with " +
        "--world replay, each monster's machine and each card's vars made again by the game and held against the live ones at a combat's last decision");
    private static readonly Option<int?> Runs = Opt.Maybe<int?>("--runs",
        "whole runs in place of the sweep's combats: this many for every character and ascension, played by the same random player, every combat of a run " +
        "checked as a combat of the sweep is, and the run around it compared too. The player dies early at a character's own HP: --hp and --max-hp let it go on");
    private static readonly Option<string?> Mode = Opt.Maybe<string?>("--mode",
        "the game mode of --runs: dread, which the game plays at ascension 10 only (--asc 10); the standard game if not given");
    private static readonly Option<string> Character = Opt.Of("--character", "all", "the characters, such as SILENT,FOOL, or all");
    private static readonly Option<string> Asc = Opt.Of("--asc", "0,10", "the ascension levels");
    private static readonly Option<string> Encounter = Opt.Of("--encounter", "all", "the encounters, such as A,B, or all: every encounter of the game, the ones list shows and the events' besides");
    private static readonly Option<int> Decks = Opt.Of("--decks", 2,
        "loadouts per character, ascension and encounter: the first is the starting one, the others are drawn (deck, upgrades, enchantments, relics, potions, HP)");
    private static readonly Option<string?> Cards = Opt.Maybe<string?>("--cards",
        "the only cards a drawn deck holds, such as A,B,C, of those its character can draw (its own pool's and the shared pools'): for decks full of one kind of card");
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
    private static readonly Option[] Shared = [World, Dump, Delegates, Runs, Mode, Character, Asc, Encounter, Decks, Cards, Deck, Relics, Potions, Hp, MaxHp, Seed, Start, N, Continue, Full];

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
        var loadout = given == new Loadout() ? null : given;
        var cards = r.GetValue(Cards)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(id => Models.Find<CardModel>(id).Id.Entry).ToHashSet();
        if (cards != null && loadout?.Deck != null) throw new CliException("--cards is for drawn decks: not with --deck");
        var sweep = new Sweep(characters, ascensions, encounters, decks, r.GetValue(Seed), loadout, cards);
        RunSweep? runs = null;
        if (r.GetValue(Mode) != null && r.GetValue(Runs) == null) throw new CliException("--mode is for whole runs: with --runs");
        if (r.GetValue(Runs) is { } each)
        {
            if (each < 1) throw new CliException("--runs is at least 1");
            if (r.GetValue(Encounter) is not "all" || r.GetResult(Decks) is { Implicit: false }) throw new CliException("--runs plays whole runs: not with --encounter or --decks");
            if (cards != null) throw new CliException("--runs plays whole runs: not with --cards");
            var mode = ascensions.Select(a => RunCommand.GameModeOf(r.GetValue(Mode), a)).Distinct().Single();
            runs = new RunSweep(characters, ascensions, each, r.GetValue(Seed), loadout, mode);
        }
        int count = runs?.Count ?? sweep.Count;
        string unit = runs == null ? "combats" : "runs";

        int start = r.GetValue(Start);
        int end = Math.Min(count, start + (r.GetValue(N) ?? count));
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
        var done = new HashSet<int>();
        void Take(ParityLine line)
        {
            lock (summary)
            {
                summary.Add(line);
                done.Add(line.I);
                output?.WriteLine(ProtocolJson.Serialize(line));
                if (toStdout || clock.ElapsedMilliseconds - reported < 30_000) return;
                reported = clock.ElapsedMilliseconds;
                Logs.Sim.LogInformation("parity: {Done} of {All} {Unit}, {Seconds:F0} s", done.Count, indexes.Count, unit, clock.Elapsed.TotalSeconds);
            }
        }

        string world = r.GetValue(World)!;
        string? dumped = r.GetValue(Dump);
        if (dumped != null && parts == 1)
        {
            Directory.CreateDirectory(dumped);
            File.WriteAllText(Path.Combine(dumped, "README.md"), CombatDump.Readme(string.Join(' ', Environment.GetCommandLineArgs().SkipWhile(a => a != "parity"))));
        }
        string? delegates = r.GetValue(Delegates);
        // A part's census goes beside the whole's, which adds the parts up.
        string? censusFile = delegates == null ? null : parts == 1 ? delegates : $"{delegates}.{part}";
        if (workers == 1)
        {
            // The engine's schema is read once; each combat has its own maker of engine worlds, which counts what it wrote.
            var schema = world == "engine" || dumped != null || delegates != null ? EngineSchema.Load(EngineLibrary.SchemaPath) : null;
            var export = schema == null ? null : new EngineExport(schema);
            var dump = dumped == null ? null : new CombatDump(export!, dumped);
            Census = delegates == null ? null : (new DelegateCensus(schema!), export!);
            if (runs != null)
            {
                IRunWorlds same = world switch
                {
                    "none" => new NoWorlds(),
                    "determinized" => throw new CliException("--world determinized is a world of single combats: not with --runs"),
                    _ => new NodeWorlds(pump),
                };
                foreach (int i in indexes)
                    foreach (var line in PlayRun(pump, runs[i], world == "engine" ? new EngineWorlds(export!) : same, dump, r.GetValue(Continue), r.GetValue(Full)))
                        Take(line);
            }
            else
            {
                ISecondWorlds same = world == "none" ? new NoWorlds() : new ReplayWorlds(pump, determinized: world == "determinized");
                foreach (int i in indexes)
                    Take(Play(pump, sweep[i], world == "engine" ? new EngineWorlds(export!) : same, dump, r.GetValue(Continue), r.GetValue(Full)));
            }
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
            if (done.Count != indexes.Count) throw new InvalidOperationException($"the workers played {done.Count} of {indexes.Count} {unit}");
            if (delegates != null)
            {
                var whole = new DelegateCensus(EngineSchema.Load(EngineLibrary.SchemaPath));
                foreach (int k in Enumerable.Range(0, workers))
                {
                    whole.Add($"{delegates}.{k}");
                    File.Delete($"{delegates}.{k}");
                }
                whole.Write(delegates);
            }
        }
        if (workers == 1) Census?.Census.Write(censusFile!);
        clock.Stop();
        if (toStdout) return summary.Clean ? 0 : 1;

        string continues = r.GetValue(Continue) is { } few ? $"continued from {few} decisions a combat" : "continued from every decision";
        return Finish(r, summary, universe,
            $"parity: world {world}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}, {indexes.Count} of the sweep's {count} {unit} "
            + (runs == null ? $"({characters.Count} characters x {ascensions.Count} ascensions x {encounters.Count} encounters x {decks} loadouts)"
                : $"({characters.Count} characters x {ascensions.Count} ascensions x {r.GetValue(Runs)} runs)")
            + $", seed {r.GetValue(Seed)}, "
            + $"{continues}, {clock.Elapsed.TotalSeconds:F0} s with {workers} {(workers == 1 ? "process" : "processes")}");
    }

    private static int Finish(ParseResult r, ParitySummary summary, Dictionary<string, List<string>> universe, string header)
    {
        Console.WriteLine(header);
        summary.Print(Console.Out, universe);
        if (r.GetValue(Coverage) is { } path) summary.WriteCoverage(path, universe);
        return summary.Clean ? 0 : 1;
    }

    private static ParityLine Play(Pump pump, SweepCombat combat, ISecondWorlds worlds, CombatDump? dump, int? continues, bool full)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var result = Lockstep.Check(pump, combat.Setup, combat.PolicySeed, worlds, continues, Kept(dump), dump == null ? null : dump.Opening);
            dump?.Write(combat.Info, result);
            MadeAgain(result.Steps);
            var line = ParityLine.Of(combat.Info, result, clock.Elapsed.TotalMilliseconds, full);
            return worlds is EngineWorlds { Written: var written } engine ? line.With(written.Count, written.Bytes, written.Ms, engine.Tally)
                : dump != null ? line.With(result.Steps) : line;
        }
        catch (Exception e)
        {
            Logs.Sim.LogError(e, "combat #{Index} failed", combat.Index);
            return ParityLine.Failed(combat.Info, e, clock.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>The census of delegates, when one is asked for, with the export whose walk it rides on.</summary>
    private static (DelegateCensus Census, EngineExport Export)? Census;

    /// <summary>What is kept of each decision: the dump's files, and with a census the delegates of an export's walk.</summary>
    private static Keep? Kept(CombatDump? dump) => Census is not var (census, export) ? dump == null ? null : dump.Keep : (player, status, prompt, observation) =>
    {
        if (status == CombatStatus.InProgress && !prompt) export.Write(HeadlessGame.GameVersion, PromptSelector.Around(), census);
        return dump?.Keep(player, status, prompt, observation);
    };

    /// <summary>With a census: the combat's last decision made again as a second world, which is then the live game, and
    /// in it the game's own code makes the machines and the vars again (<see cref="DelegateCensus.Compare"/>). The world
    /// is thrown away after.</summary>
    private static void MadeAgain(IReadOnlyList<RealStep> steps)
    {
        if (Census is not var (census, _) || steps.Count < 2 || steps[^2].Make is not { } make) return;
        using var world = make();
        if (world.World is EngineWorld) return;
        world.World.Observe();
        var state = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat state");
        census.Compare(state, MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state.RunState) ?? throw new InvalidOperationException("no player"));
    }

    /// <summary>One whole run: a line for each of its combats. The first carries what belongs to the run as a whole
    /// (the time the real run took, the game's errors, what the engine's worlds wrote), the last how the run ended.</summary>
    private static List<ParityLine> PlayRun(Pump pump, SweepRun run, IRunWorlds worlds, CombatDump? dump, int? continues, bool full)
    {
        var clock = Stopwatch.StartNew();
        SweptCombat Info(RunCombat? combat) => new(run.Index, run.Setup.Character, run.Setup.Ascension, combat?.Encounter ?? "<run>", combat?.Act ?? 0, run.Setup.Seed,
            "run", run.Setup.Loadout is { } loadout ? RandomLoadout.Text(loadout) : null, run.PolicySeed, combat?.Floor);
        try
        {
            var (combats, end, gameErrors) = RunLockstep.Record(pump, run.Setup, run.PolicySeed, worlds, Kept(dump), dump == null ? null : dump.Opening);
            if (combats.Count == 0) throw new InvalidOperationException($"the run has no combat: it {end}");
            var lines = new List<ParityLine>();
            foreach (var combat in combats)
            {
                var result = Lockstep.Compare(combat.Steps, continues, run.PolicySeed) with
                {
                    Outcome = combat.End == CombatStatus.Lost ? "Loss" : "Win",
                    GameErrors = lines.Count == 0 ? gameErrors : [],
                };
                dump?.Write(Info(combat), result);
                MadeAgain(combat.Steps);
                lines.Add(ParityLine.Of(Info(combat), result, clock.Elapsed.TotalMilliseconds, full));
                clock.Restart();
            }
            if (worlds is EngineWorlds { Written: var written } engine) lines[0] = lines[0].With(written.Count, written.Bytes, written.Ms, engine.Tally);
            else if (dump != null) lines[0] = lines[0].With(combats.SelectMany(c => c.Steps));
            lines[^1] = lines[^1] with { RunEnd = end };
            return lines;
        }
        catch (Exception e)
        {
            Logs.Sim.LogError(e, "run #{Index} failed", run.Index);
            return [ParityLine.Failed(Info(null), e, clock.Elapsed.TotalMilliseconds)];
        }
    }
}
