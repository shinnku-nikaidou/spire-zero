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
        var file = ChampionFile(new()
        {
            ["env"] = "NoSL", ["policy"] = "pimc", ["macro"] = "l0", ["value"] = value, ["combat-net"] = net, ["afterstates"] = afterstates,
        });
        var (noSl, sl) = BrainCommand.Champions(Cli.Parse(["brain", "--champion", file]));

        Assert.False(noSl.OnClone);
        Assert.True(sl.OnClone);
        Assert.Equal("combat pimc combat-tiny 8x300, combat prompts first, macro l0 pure-v-tiny-win, macro prompts l0 pure-v-tiny-win", noSl.Name);
        Assert.EndsWith(", combat prompts first, macro l0 pure-v-tiny-win clone, macro prompts l0 pure-v-tiny-win clone", sl.Name);
        Assert.StartsWith("combat plan combat-tiny ", sl.Name);
    }

    [Fact]
    public void BrainPlaysSLHostsWithTheirOwnChampionFile()
    {
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var slValue = Path.Combine(_dir, "v-sl.json");
        File.Copy(value, slValue);
        var slFile = Path.Combine(_dir, "sl.json");
        File.Move(ChampionFile(new()
        {
            ["env"] = "SL", ["policy"] = "plan", ["macro"] = "l0", ["effort"] = "medium", ["value"] = slValue, ["combat-net"] = net, ["afterstates"] = "clone",
        }), slFile);
        var noSlFile = ChampionFile(new() { ["env"] = "NoSL", ["policy"] = "pimc", ["macro"] = "l0", ["value"] = value, ["combat-net"] = net });

        var (noSl, sl) = BrainCommand.Champions(Cli.Parse(["brain", "--champion", noSlFile, "--sl-champion", slFile, "--workers", "3"]));

        Assert.Equal(("pimc", value, false, 3), (noSl.Policy, noSl.ValueFile, noSl.OnClone, noSl.Workers));
        Assert.Equal(("plan", slValue, true, 3), (sl.Policy, sl.ValueFile, sl.OnClone, sl.Workers));
        Assert.Throws<CliException>(() => BrainCommand.Champions(Cli.Parse(["brain", "--champion", noSlFile, "--sl-champion", slFile, "--sl-policy", "plan"])));
    }

    [Theory]
    [InlineData("--macro", "l0")]
    [InlineData("--explore", "0.1", "--macro", "random")]
    [InlineData("--macro-explore", "0.05", "--macro", "random")]
    [InlineData("--take", "A", "--macro", "random")]
    [InlineData("--refuse", "A", "--macro", "random")]
    [InlineData("--take", "A,B", "--refuse", "B", "--macro", "l0", "--value", "Fixtures/pure-v-tiny-win.json")]
    [InlineData("--take", ",", "--macro", "l0", "--value", "Fixtures/pure-v-tiny-win.json")]
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
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var champion = new ChampionArgs(file: false);
        var command = Opt.Command("test", "", champion.Options);
        Assert.Throws<CliException>(() => champion.Read(command.Parse(["--combat-net", net, "--value", value, "--policy", "play"])));
        // V prices the combat's endings, so the net needs it.
        Assert.Throws<CliException>(() => champion.Read(command.Parse(["--combat-net", net, "--policy", "pimc"])));
        Assert.StartsWith("combat pimc combat-tiny 8x300,", champion.Read(command.Parse(["--combat-net", net, "--value", value, "--policy", "pimc"])).Name);
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
        var played = champion.Read(Parse(["test", "--policy", "pimc", "--combat-net", net, "--macro", "l0", "--value", value, "--worlds", "4", "--nodes", "100",
            "--explore", "0.1"]));
        var file = ChampionFile(new()
        {
            ["env"] = "NoSL", ["policy"] = "pimc", ["combat-net"] = net, ["macro"] = "l0", ["value"] = value, ["worlds"] = "4", ["nodes"] = "100",
            ["explore"] = "0.1",
        });
        var again = champion.Read(Parse(["test", "--champion", file]));

        Assert.Equal("combat pimc combat-tiny 4x100, combat prompts first, macro l0 pure-v-tiny-win eps 0.1, macro prompts l0 pure-v-tiny-win eps 0.1", played.Name);
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
        var played = champion.Read(command.Parse(["--macro", "l0", "--value", value, "--take", "BLADE_DANCE, LEADING_STRIKE", "--refuse", "SNAKEBITE"]));
        Assert.Equal(new HashSet<string> { "BLADE_DANCE", "LEADING_STRIKE" }, played.Cards!.Take);
        Assert.Equal(new HashSet<string> { "SNAKEBITE" }, played.Cards.Refuse);
        Assert.EndsWith("macro l0 pure-v-tiny-win take 2 refuse 1, macro prompts l0 pure-v-tiny-win take 2 refuse 1", played.Name);
        Assert.True(played.Explores);
        Assert.Null(champion.Read(command.Parse(["--macro", "l0", "--value", value])).Cards);
    }

    [Fact]
    public void MaxPlaysTheSolver()
    {
        var champion = new ChampionArgs(file: true);
        var root = new RootCommand { Opt.Command("test", "", champion.Options) };
        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var played = champion.Read(root.Parse(["test", "--policy", "plan", "--combat-net", net, "--value", value, "--effort", "max"],
            new ParserConfiguration()));
        Assert.Equal(Budget.Effort("max"), played.Budget);
        Assert.True(played.Budget.Solve > 0);
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
        var played = champion.Read(Parse(["test", "--policy", "plan", "--combat-net", net, "--macro", "l0", "--value", value,
            "--elite-effort", "low", "--boss-effort", "medium", "--explore", "0.1", "--macro-explore", "0.05"]));
        // An elite at low is every other fight's budget, so only the boss's shows.
        Assert.Null(played.Budget.Elite);
        Assert.Equal(Budget.Effort("medium"), played.Budget.Boss);
        Assert.Equal("combat plan combat-tiny beam 8x300 easy 4 loss plans 3 boss (beam 8x300), combat prompts first, " +
            "macro l0 pure-v-tiny-win explore 0.05 eps 0.1, macro prompts l0 pure-v-tiny-win explore 0.05 eps 0.1", played.Name);
        Assert.True(played.Explores);

        var file = ChampionFile(new()
        {
            ["env"] = "SL", ["policy"] = "plan", ["combat-net"] = net, ["macro"] = "l0", ["value"] = value, ["effort"] = "low",
            ["boss-effort"] = "medium", ["explore"] = "0.1", ["macro-explore"] = "0.05",
        });
        var again = champion.Read(Parse(["test", "--champion", file]));
        Assert.Equal(played.Name, again.Name);
        Assert.Equal(played.Budget, again.Budget);
    }
}
