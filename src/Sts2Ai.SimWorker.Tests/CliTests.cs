using System.CommandLine;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.SimWorker.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sts2ai-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ChampionFile(Dictionary<string, string> options)
    {
        var path = Path.Combine(_dir, "champion.json");
        File.WriteAllText(path, ProtocolJson.Serialize(new ChampionFile("SL", "test", options, new Dictionary<string, string> { ["SILENT"] = "report.json" }, "2026-10-27")));
        return path;
    }

    [Fact]
    public void FillsInTheDefaults()
    {
        var r = Cli.Parse(["run"]);
        Assert.Empty(r.Errors);
        Assert.Equal(PlayEnvironment.NoSL, r.GetValue<PlayEnvironment>("--env"));
        Assert.Equal("play", r.GetValue<string>("--policy"));
        Assert.Equal(8, r.GetValue<int>("--worlds"));
        Assert.Null(r.GetValue<int?>("--n"));
    }

    [Fact]
    public void ReadsEnvironmentsInAnyCase() => Assert.Equal(PlayEnvironment.SL, Cli.Parse(["run", "--env", "sl"]).GetValue<PlayEnvironment>("--env"));

    [Theory]
    [InlineData("run", "--wat")]
    [InlineData("run", "--policy", "greedy")]
    [InlineData("run", "--worlds", "many")]
    [InlineData("report", "--seeds", "s.txt")]
    [InlineData("nonsense")]
    public void ReportsMistakes(params string[] args) => Assert.NotEmpty(Cli.Parse(args).Errors);

    [Fact]
    public void TakesVerboseAnywhereAndNoResponseFiles()
    {
        var r = Cli.Parse(["run", "--seeds", "@seeds.txt", "--verbose"]);
        Assert.Empty(r.Errors);
        Assert.Equal("@seeds.txt", r.GetValue<string>("--seeds"));
    }

    [Fact]
    public void AChampionFilesOptionsGoUnderTheOnesGiven()
    {
        var file = ChampionFile(new() { ["env"] = "SL", ["policy"] = "pimc", ["worlds"] = "4", ["nodes"] = "100" });
        var r = Cli.Parse(["run", "--champion", file, "--nodes", "50"]);
        Assert.Empty(r.Errors);
        Assert.Equal(PlayEnvironment.SL, r.GetValue<PlayEnvironment>("--env"));
        Assert.Equal("pimc", r.GetValue<string>("--policy"));
        Assert.Equal(4, r.GetValue<int>("--worlds"));
        Assert.Equal(50, r.GetValue<int>("--nodes"));
    }

    [Fact]
    public void RunPlaysAChampionFilesCharacterAndAscension()
    {
        var file = ChampionFile(new() { ["env"] = "NoSL", ["character"] = "IRONCLAD", ["asc"] = "3" });
        var r = Cli.Parse(["run", "--champion", file]);
        Assert.Empty(r.Errors);
        Assert.Equal("IRONCLAD", r.GetValue<string>("--character"));
        Assert.Equal(3, r.GetValue<int>("--asc"));
    }

    [Fact]
    public void RunPlaysItsCharactersOwnNetsFromAChampionFile()
    {
        var path = Path.Combine(_dir, "champion.json");
        File.WriteAllText(path, ProtocolJson.Serialize(new ChampionFile("NoSL", "test", new Dictionary<string, string> { ["env"] = "NoSL", ["value"] = "v-all.json" },
            new Dictionary<string, string>(), "2026-11-22", Characters: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["IRONCLAD"] = new Dictionary<string, string> { ["value"] = "v-ironclad.json" },
            })));
        string? Value(params string[] args) => Cli.Parse(["run", "--champion", path, .. args]).GetValue<string?>("--value");

        Assert.Equal("v-ironclad.json", Value("--character", "IRONCLAD"));
        Assert.Equal("v-all.json", Value("--character", "SILENT"));
        Assert.Equal("v-all.json", Value());
        Assert.Equal("v-given.json", Value("--character", "IRONCLAD", "--value", "v-given.json"));
    }

    [Fact]
    public void BrainPlaysEachCharactersOwnNets()
    {
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var fNet = Path.Combine(AppContext.BaseDirectory, "Fixtures", "f-tiny.json");
        string Write(string name, string env, Dictionary<string, string> options)
        {
            var own = Path.Combine(_dir, $"v-{name}-ironclad.json");
            File.Copy(value, own);
            var path = Path.Combine(_dir, $"{name}.json");
            File.WriteAllText(path, ProtocolJson.Serialize(new ChampionFile(env, "test", options, new Dictionary<string, string>(), "2026-11-22",
                Characters: new Dictionary<string, IReadOnlyDictionary<string, string>> { ["IRONCLAD"] = new Dictionary<string, string> { ["value"] = own } })));
            return path;
        }
        var slFile = Write("sl", "SL", new()
        {
            ["env"] = "SL", ["policy"] = "plan", ["macro"] = "l0", ["effort"] = "medium", ["value"] = value, ["combat-net"] = net, ["f-net"] = fNet, ["afterstates"] = "clone",
        });
        var noSlFile = Write("nosl", "NoSL", new() { ["env"] = "NoSL", ["policy"] = "pimc", ["macro"] = "l0", ["value"] = value, ["combat-net"] = net, ["f-net"] = fNet });
        var r = Cli.Parse(["brain", "--champion", noSlFile, "--sl-champion", slFile, "--workers", "3"]);
        var (noSl, sl) = BrainCommand.Champions(r);

        var slIronclad = BrainCommand.Of(r, sl, sl: true, "IRONCLAD");
        var noSlIronclad = BrainCommand.Of(r, noSl, sl: false, "IRONCLAD");
        Assert.Equal((Path.Combine(_dir, "v-sl-ironclad.json"), true, 3, "plan"), (slIronclad.ValueFile, slIronclad.OnClone, slIronclad.Workers, slIronclad.Policy));
        Assert.Equal((Path.Combine(_dir, "v-nosl-ironclad.json"), false, 3, "pimc"), (noSlIronclad.ValueFile, noSlIronclad.OnClone, noSlIronclad.Workers, noSlIronclad.Policy));
        Assert.Equal("pure-v-tiny-win", slIronclad.Shown);
        Assert.Same(sl, BrainCommand.Of(r, sl, sl: true, "SILENT"));
        Assert.Same(noSl, BrainCommand.Of(r, noSl, sl: false, "SILENT"));
        Assert.Throws<CliException>(() => BrainCommand.Champions(Cli.Parse(["brain", "--champion", noSlFile])));
    }

    [Fact]
    public void BrainTakesAChampionFileWithoutItsEnvironment()
    {
        var file = ChampionFile(new() { ["env"] = "SL", ["policy"] = "pimc", ["worlds"] = "4" });
        var r = Cli.Parse(["brain", "--champion", file]);
        Assert.Empty(r.Errors);
        Assert.Equal(4, r.GetValue<int>("--worlds"));
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("clone")]
    public void BrainsL0PlaysOnTheScreenInNoSLAndOnTheCloneInSL(string afterstates)
    {
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var fNet = Path.Combine(AppContext.BaseDirectory, "Fixtures", "f-tiny.json");
        var file = ChampionFile(new()
        {
            ["env"] = "NoSL", ["policy"] = "pimc", ["macro"] = "l0", ["value"] = value, ["combat-net"] = net, ["f-net"] = fNet, ["afterstates"] = afterstates,
        });
        var (noSl, sl) = BrainCommand.Champions(Cli.Parse(["brain", "--champion", file]));

        Assert.False(noSl.OnClone);
        Assert.True(sl.OnClone);
        Assert.Equal("combat pimc combat-tiny + f-tiny 8x300, combat prompts first, macro l0 pure-v-tiny-win, macro prompts l0 pure-v-tiny-win", noSl.Name);
        Assert.EndsWith(", combat prompts first, macro l0 pure-v-tiny-win clone, macro prompts l0 pure-v-tiny-win clone", sl.Name);
        Assert.StartsWith("combat plan combat-tiny + f-tiny ", sl.Name);
    }

    [Fact]
    public void BrainPlaysSLHostsWithTheirOwnChampionFile()
    {
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var fNet = Path.Combine(AppContext.BaseDirectory, "Fixtures", "f-tiny.json");
        var slValue = Path.Combine(_dir, "v-sl.json");
        File.Copy(value, slValue);
        var slFile = Path.Combine(_dir, "sl.json");
        File.Move(ChampionFile(new()
        {
            ["env"] = "SL", ["policy"] = "plan", ["macro"] = "l0", ["effort"] = "medium", ["value"] = slValue, ["combat-net"] = net, ["f-net"] = fNet, ["afterstates"] = "clone",
        }), slFile);
        var noSlFile = ChampionFile(new() { ["env"] = "NoSL", ["policy"] = "pimc", ["macro"] = "l0", ["value"] = value, ["combat-net"] = net, ["f-net"] = fNet });

        var (noSl, sl) = BrainCommand.Champions(Cli.Parse(["brain", "--champion", noSlFile, "--sl-champion", slFile, "--workers", "3"]));

        Assert.Equal(("pimc", value, false, 3), (noSl.Policy, noSl.ValueFile, noSl.OnClone, noSl.Workers));
        Assert.Equal(("plan", slValue, true, 3), (sl.Policy, sl.ValueFile, sl.OnClone, sl.Workers));
        Assert.Throws<CliException>(() => BrainCommand.Champions(Cli.Parse(["brain", "--champion", noSlFile, "--sl-champion", slFile, "--sl-policy", "plan"])));
    }

    [Theory]
    [InlineData("--macro", "l0")]
    [InlineData("--explore", "0.1", "--macro", "random")]
    [InlineData("--macro-explore", "0.05", "--macro", "random")]
    [InlineData("--favor", "A", "--favor-by", "0.2", "--macro", "random")]
    [InlineData("--refuse", "A", "--macro", "random")]
    [InlineData("--keep", "A", "--macro", "random")]
    [InlineData("--upgrade-first", "A", "--macro", "random")]
    [InlineData("--keep", "A,B", "--refuse", "B", "--macro", "l0", "--value", "Fixtures/pure-v-tiny-win.json")]
    [InlineData("--favor", "A,B", "--favor-by", "0.2", "--refuse", "B", "--macro", "l0", "--value", "Fixtures/pure-v-tiny-win.json")]
    [InlineData("--favor", ",", "--favor-by", "0.2", "--macro", "l0", "--value", "Fixtures/pure-v-tiny-win.json")]
    [InlineData("--favor", "A", "--macro", "l0", "--value", "Fixtures/pure-v-tiny-win.json")]
    [InlineData("--favor", "A", "--favor-by", "0", "--macro", "l0", "--value", "Fixtures/pure-v-tiny-win.json")]
    [InlineData("--boss-effort", "high", "--policy", "play")]
    [InlineData("--value", "Fixtures/pure-v-tiny-win.json", "--macro", "random")]
    [InlineData("--policy", "pimc")]
    public void RefusesChampionsThatDoNotHangTogether(params string[] args)
    {
        var champion = new ChampionArgs(file: false);
        var r = Opt.Command("test", "", champion.Options).Parse(args);
        Assert.Empty(r.Errors);
        Assert.Throws<CliException>(() => champion.Read(r));
    }

    [Fact]
    public void ACombatNetGoesWithASearchAndNamesIt()
    {
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var fNet = Path.Combine(AppContext.BaseDirectory, "Fixtures", "f-tiny.json");
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var champion = new ChampionArgs(file: false);
        var command = Opt.Command("test", "", champion.Options);
        Assert.Throws<CliException>(() => champion.Read(command.Parse(["--combat-net", net, "--f-net", fNet, "--value", value, "--policy", "play"])));
        // V prices the combat's endings, so the net needs it.
        Assert.Throws<CliException>(() => champion.Read(command.Parse(["--combat-net", net, "--f-net", fNet, "--policy", "pimc"])));
        Assert.StartsWith("combat pimc combat-tiny + f-tiny 8x300,", champion.Read(command.Parse(["--combat-net", net, "--f-net", fNet, "--value", value, "--policy", "pimc"])).Name);
    }

    [Fact]
    public void AChampionFilesOptionsPlayItsChampion()
    {
        var champion = new ChampionArgs(file: true);
        var root = new RootCommand { Opt.Command("test", "", champion.Options) };
        var config = new ParserConfiguration();
        ParseResult Parse(string[] args) => ChampionFileOption.Merge(root, args, root.Parse(args, config), config);

        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var fNet = Path.Combine(AppContext.BaseDirectory, "Fixtures", "f-tiny.json");
        var played = champion.Read(Parse(["test", "--policy", "pimc", "--combat-net", net, "--f-net", fNet, "--macro", "l0", "--value", value, "--worlds", "4", "--nodes", "100",
            "--explore", "0.1"]));
        var file = ChampionFile(new()
        {
            ["env"] = "NoSL", ["policy"] = "pimc", ["combat-net"] = net, ["f-net"] = fNet, ["macro"] = "l0", ["value"] = value, ["worlds"] = "4", ["nodes"] = "100",
            ["explore"] = "0.1",
        });
        var again = champion.Read(Parse(["test", "--champion", file]));

        Assert.Equal("combat pimc combat-tiny + f-tiny 4x100, combat prompts first, macro l0 pure-v-tiny-win eps 0.1, macro prompts l0 pure-v-tiny-win eps 0.1", played.Name);
        Assert.Equal(played.Name, again.Name);
        Assert.Equal(4, again.Workers);
        // As promote reads a champion file with new nets: the file's champion, the options given over its own.
        Assert.Equal(played.Name, ChampionArgs.Of(file, []).Name);
        Assert.Equal(played.Name.Replace("4x100", "4x50"), ChampionArgs.Of(file, ["--nodes", "50"]).Name);
    }

    [Fact]
    public void LocksL0sCardsForSelfPlayAndSaysSoInItsName()
    {
        var champion = new ChampionArgs(file: false);
        var command = Opt.Command("test", "", champion.Options);
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var played = champion.Read(command.Parse(["--macro", "l0", "--value", value, "--favor", "BLADE_DANCE, LEADING_STRIKE", "--favor-by", "0.2",
            "--refuse", "SNAKEBITE"]));
        Assert.Equal(new HashSet<string> { "BLADE_DANCE", "LEADING_STRIKE" }, played.Cards!.Favor);
        Assert.Equal(0.2, played.Cards.Bonus);
        Assert.Equal(new HashSet<string> { "SNAKEBITE" }, played.Cards.Refuse);
        Assert.EndsWith("macro l0 pure-v-tiny-win favor 2 by 0.2 refuse 1, macro prompts l0 pure-v-tiny-win favor 2 by 0.2 refuse 1", played.Name);
        Assert.True(played.Explores);
        Assert.Null(champion.Read(command.Parse(["--macro", "l0", "--value", value])).Cards);
        // Tyranny's runs (plans/2026-11-23-1545-sl-regent-g0.md): favored, kept and upgraded first.
        var tyranny = champion.Read(command.Parse(["--macro", "l0", "--value", value, "--favor", "TYRANNY", "--favor-by", "10",
            "--keep", "TYRANNY", "--upgrade-first", "TYRANNY"]));
        Assert.Equal(new HashSet<string> { "TYRANNY" }, tyranny.Cards!.Keep);
        Assert.Equal(new HashSet<string> { "TYRANNY" }, tyranny.Cards.UpgradeFirst);
        Assert.EndsWith("macro l0 pure-v-tiny-win favor 1 by 10 refuse 0 keep 1 upgrade first 1, macro prompts l0 pure-v-tiny-win favor 1 by 10 refuse 0 keep 1 upgrade first 1",
            tyranny.Name);
        Assert.NotNull(champion.Read(command.Parse(["--macro", "l0", "--value", value, "--keep", "TYRANNY"])).Cards);
    }

    [Fact]
    public void MaxPlaysTheSolver()
    {
        var champion = new ChampionArgs(file: true);
        var root = new RootCommand { Opt.Command("test", "", champion.Options) };
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var fNet = Path.Combine(AppContext.BaseDirectory, "Fixtures", "f-tiny.json");
        var played = champion.Read(root.Parse(["test", "--policy", "plan", "--combat-net", net, "--f-net", fNet, "--value", value, "--effort", "max"],
            new ParserConfiguration()));
        Assert.Equal(Budget.Effort("max"), played.Budget);
        Assert.True(played.Budget.Solve > 0);

        // xhigh: max with its early stop, which the name shows.
        var xhigh = champion.Read(root.Parse(["test", "--policy", "plan", "--combat-net", net, "--f-net", fNet, "--value", value, "--effort", "xhigh"],
            new ParserConfiguration()));
        Assert.Equal(Budget.Effort("xhigh"), xhigh.Budget);
        Assert.Equal(Budget.XhighStop, xhigh.Budget.Stop);
        Assert.Contains("solve 16384 stop 1024", xhigh.Name);
    }

    [Fact]
    public void PlansEliteAndBossFightsAtTheirOwnEffortAndDrawsBySoftmaxAndAChampionFileKeepsBoth()
    {
        var champion = new ChampionArgs(file: true);
        var root = new RootCommand { Opt.Command("test", "", champion.Options) };
        var config = new ParserConfiguration();
        ParseResult Parse(string[] args) => ChampionFileOption.Merge(root, args, root.Parse(args, config), config);

        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var fNet = Path.Combine(AppContext.BaseDirectory, "Fixtures", "f-tiny.json");
        var played = champion.Read(Parse(["test", "--policy", "plan", "--combat-net", net, "--f-net", fNet, "--macro", "l0", "--value", value,
            "--elite-effort", "low", "--boss-effort", "medium", "--explore", "0.1", "--macro-explore", "0.05"]));
        // An elite at low is every other fight's budget, so only the boss's shows.
        Assert.Null(played.Budget.Elite);
        Assert.Equal(Budget.Effort("medium"), played.Budget.Boss);
        Assert.Equal("combat plan combat-tiny + f-tiny beam 8x300 easy 4 loss plans 3 boss (beam 8x300), combat prompts first, " +
            "macro l0 pure-v-tiny-win explore 0.05 eps 0.1, macro prompts l0 pure-v-tiny-win explore 0.05 eps 0.1", played.Name);
        Assert.True(played.Explores);

        var file = ChampionFile(new()
        {
            ["env"] = "SL", ["policy"] = "plan", ["combat-net"] = net, ["f-net"] = fNet, ["macro"] = "l0", ["value"] = value, ["effort"] = "low",
            ["boss-effort"] = "medium", ["explore"] = "0.1", ["macro-explore"] = "0.05",
        });
        var again = champion.Read(Parse(["test", "--champion", file]));
        Assert.Equal(played.Name, again.Name);
        Assert.Equal(played.Budget, again.Budget);
    }
}
