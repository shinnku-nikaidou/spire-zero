using System.CommandLine;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.Tests;

public class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sts2ai-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ChampionFile(Dictionary<string, string> options)
    {
        var path = Path.Combine(_dir, "champion.json");
        File.WriteAllText(path, ProtocolJson.Serialize(new ChampionFile("SL", "test", options, "report.json", "2026-10-27")));
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
    [InlineData("run", "--explore", "0.2")]
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
    [InlineData("--macro-explore", "-1", "--macro", "l0")]
    [InlineData("--macro-explore", "2", "--macro", "random")]
    [InlineData("--combat-explore", "0.01")]
    [InlineData("--value", "v.json", "--macro", "random")]
    public void RefusesChampionsThatDoNotHangTogether(params string[] args)
    {
        var champion = new ChampionArgs(file: false);
        var r = Opt.Command("test", "", champion.Options).Parse(args);
        Assert.Empty(r.Errors);
        Assert.Throws<CliException>(() => champion.Read(r));
    }

    [Fact]
    public void RefusesLeafWeightsWithoutPimc()
    {
        var leaf = Path.Combine(_dir, "leaf.json");
        File.WriteAllText(leaf, ProtocolJson.Serialize(LeafFile.Initial));
        var champion = new ChampionArgs(file: false);
        var command = Opt.Command("test", "", champion.Options);
        Assert.Throws<CliException>(() => champion.Read(command.Parse(["--leaf", leaf, "--policy", "play"])));
        Assert.StartsWith("combat pimc leaf0 8x300,", champion.Read(command.Parse(["--leaf", leaf, "--policy", "pimc"])).Name);
    }

    [Fact]
    public void AChampionsOptionsPlayItAgain()
    {
        var champion = new ChampionArgs(file: true);
        var root = new RootCommand { Opt.Command("test", "", champion.Options) };
        var config = new ParserConfiguration();
        ParseResult Parse(string[] args) => ChampionFileOption.Merge(root, args, root.Parse(args, config), config);

        var played = champion.Read(Parse(["test", "--policy", "pimc", "--macro", "l0", "--worlds", "4", "--nodes", "100", "--elite-x", "2", "--halving",
            "--macro-explore", "2", "--combat-explore", "0.01"]));
        var file = ChampionFile(played.Options(PlayEnvironment.NoSL));
        var again = champion.Read(Parse(["test", "--champion", file]));

        Assert.Equal("combat pimc explore 0.01 4x100 elite x2 halving, combat prompts p0, macro l0 explore 2, macro prompts l0 explore 2", played.Name);
        Assert.Equal(played.Name, again.Name);
        Assert.Equal(played.Options(PlayEnvironment.NoSL), again.Options(PlayEnvironment.NoSL));
        Assert.Equal(4, again.Workers);
    }
}
