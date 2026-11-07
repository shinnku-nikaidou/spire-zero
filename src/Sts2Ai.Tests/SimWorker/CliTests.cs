using System.CommandLine;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.Tests.SimWorker;

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

    [Theory]
    [InlineData("--macro", "l0")]
    [InlineData("--explore", "0.1", "--macro", "random")]
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
    public void AChampionsOptionsPlayItAgain()
    {
        var champion = new ChampionArgs(file: true);
        var root = new RootCommand { Opt.Command("test", "", champion.Options) };
        var config = new ParserConfiguration();
        ParseResult Parse(string[] args) => ChampionFileOption.Merge(root, args, root.Parse(args, config), config);

        var value = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pure-v-tiny-win.json");
        var net = Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-tiny.json");
        var played = champion.Read(Parse(["test", "--policy", "pimc", "--combat-net", net, "--macro", "l0", "--value", value, "--worlds", "4", "--nodes", "100",
            "--explore", "0.1"]));
        var file = ChampionFile(played.Options(PlayEnvironment.NoSL));
        var again = champion.Read(Parse(["test", "--champion", file]));

        Assert.Equal("combat pimc combat-tiny 4x100, combat prompts first, macro l0 pure-v-tiny-win eps 0.1, macro prompts l0 pure-v-tiny-win eps 0.1", played.Name);
        Assert.Equal(played.Name, again.Name);
        Assert.Equal(played.Options(PlayEnvironment.NoSL), again.Options(PlayEnvironment.NoSL));
        Assert.Equal(4, again.Workers);
    }
}
