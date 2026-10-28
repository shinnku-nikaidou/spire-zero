using System.CommandLine;
using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>A mistake in what the command line asks for: the sim reports it in one line and exits with code 2.</summary>
internal sealed class CliException(string message) : Exception(message);

/// <summary>Shorthands for declaring options.</summary>
internal static class Opt
{
    /// <summary>An option with a default.</summary>
    public static Option<T> Of<T>(string name, T fallback, string description) =>
        new(name) { DefaultValueFactory = _ => fallback, Description = description };

    /// <summary>An option without a default: null when not given.</summary>
    public static Option<T> Maybe<T>(string name, string description) => new(name) { Description = description };

    /// <summary>An option that must be given.</summary>
    public static Option<T> Required<T>(string name, string description) => new(name) { Required = true, Description = description };

    /// <summary>A command with these options, in this order.</summary>
    public static Command Command(string name, string description, params IEnumerable<Option>[] options)
    {
        var command = new Command(name, description);
        foreach (var option in options.SelectMany(o => o)) command.Options.Add(option);
        return command;
    }
}

/// <summary>The player's loadout, for every command that plays: omitted parts keep the character's starting values.</summary>
internal sealed class LoadoutArgs
{
    private readonly Option<string> _character = Opt.Of("--character", "SILENT", "the character");
    private readonly Option<string?> _deck = Opt.Maybe<string?>("--deck", "the deck, such as A*5,B+ (ID*N repeats an entry, + upgrades it)");
    private readonly Option<string?> _relics = Opt.Maybe<string?>("--relics", "the relics, such as A,B");
    private readonly Option<string?> _potions = Opt.Maybe<string?>("--potions", "the potions, such as A,B");
    private readonly Option<int?> _hp = Opt.Maybe<int?>("--hp", "the HP to start with");
    private readonly Option<int?> _maxHp = Opt.Maybe<int?>("--max-hp", "the max HP to start with");

    public IEnumerable<Option> Options => [_character, _deck, _relics, _potions, _hp, _maxHp];

    public string Character(ParseResult r) => r.GetValue(_character)!;

    public Loadout Loadout(ParseResult r) =>
        SimWorker.Loadout.Parse(r.GetValue(_deck), r.GetValue(_relics), r.GetValue(_potions), r.GetValue(_hp), r.GetValue(_maxHp));
}

/// <summary>The combats a command plays: one act's room kinds, or one encounter, with the player's loadout.</summary>
internal sealed class PoolArgs
{
    private readonly Option<int> _act = Opt.Of("--act", 0, "the act, from 0");
    private readonly Option<string> _rooms = Opt.Of("--rooms", "weak,regular", "the act's room kinds, from weak, regular, elite, boss");
    private readonly Option<string?> _encounter = Opt.Maybe<string?>("--encounter", "one encounter instead (see list)");
    private readonly LoadoutArgs _loadout = new();

    public IEnumerable<Option> Options => [_act, _rooms, _encounter, .. _loadout.Options];

    /// <summary>The pool, from the game's model database: the game must be booted.</summary>
    public CombatPool Read(ParseResult r)
    {
        string character = _loadout.Character(r);
        var loadout = _loadout.Loadout(r);
        if (r.GetValue(_encounter) is { } id)
        {
            var (encounter, encounterAct) = Cli.Encounter(id);
            return new(encounterAct, encounter.Id.Entry, [encounter], character, loadout);
        }
        int act = r.GetValue(_act);
        if (act < 0 || act >= ModelDb.ActsByIndex.Count) throw new CliException($"no act {act} (0 to {ModelDb.ActsByIndex.Count - 1})");
        var rooms = r.GetValue(_rooms)!;
        var pool = ModelDb.ActsByIndex[act]
            .SelectMany(a => rooms.Split(',').SelectMany(room => room switch
            {
                "weak" => a.AllWeakEncounters,
                "regular" => a.AllRegularEncounters,
                "elite" => a.AllEliteEncounters,
                "boss" => a.AllBossEncounters,
                _ => throw new CliException($"unknown room kind {room} (weak, regular, elite, boss)"),
            }))
            .Distinct().ToList();
        if (pool.Count == 0) throw new CliException("empty encounter pool");
        return new(act, rooms, pool, character, loadout);
    }
}

/// <summary>A command's encounter pool and the player's loadout to fight it with.</summary>
internal sealed record CombatPool(int Act, string Rooms, List<EncounterModel> Encounters, string Character, Loadout Loadout)
{
    /// <summary>The i-th combat: the pool's encounters in turn.</summary>
    public CombatSetup Setup(int i, string seed) => Setup(Encounters[i % Encounters.Count], seed);

    public CombatSetup Setup(EncounterModel encounter, string seed) => new(Character, encounter, Act, seed, Loadout: Loadout);
}

/// <summary>pimc's and plan's budget (docs: combat-brain §3, §5).</summary>
internal sealed class BudgetArgs
{
    /// <summary>The planner's beam when --beam is not given.</summary>
    public const int DefaultBeam = 8;

    private readonly Option<int> _worlds = Opt.Of("--worlds", 8, "pimc's determinized worlds per decision");
    private readonly Option<int> _nodes = Opt.Of("--nodes", 300, "search nodes per world (plan: per state and turn)");
    private readonly Option<double> _elite = Opt.Of("--elite-x", 1.0, "multiplies the nodes per world in elite rooms");
    private readonly Option<double> _boss = Opt.Of("--boss-x", 1.0, "multiplies the nodes per world in boss rooms");
    private readonly Option<bool> _halving = Opt.Of("--halving", false, "shares the nodes over the first actions by sequential halving");
    private readonly Option<bool> _follow = Opt.Of("--follow", false, "plays the rest of a turn every world planned the same without searching again");
    private readonly Option<int?> _beam = Opt.Maybe<int?>("--beam", $"plan's states kept from one turn to the next (default {DefaultBeam})");

    public IEnumerable<Option> Options => [_worlds, _nodes, _elite, _boss, _halving, _follow, _beam];

    /// <summary>The budget of the combat brain <paramref name="brain"/> (pimc, plan, or a placeholder).</summary>
    public Budget Read(ParseResult r, string brain)
    {
        var budget = new Budget(r.GetValue(_worlds), r.GetValue(_nodes), r.GetValue(_elite), r.GetValue(_boss), r.GetValue(_halving), r.GetValue(_follow));
        if (brain != "plan")
            return r.GetValue(_beam) == null ? budget : throw new CliException("--beam is plan's; it needs --policy plan");
        if (budget.Halving || budget.Follow) throw new CliException("--halving and --follow are pimc's; plan follows its plan always");
        int beam = r.GetValue(_beam) ?? DefaultBeam;
        return beam > 0 ? budget with { Beam = beam } : throw new CliException("--beam is at least 1");
    }
}

/// <summary>What pimc values its leaves with (docs: combat-brain §4).</summary>
internal sealed class LeafArgs
{
    private readonly Option<string?> _leaf = Opt.Maybe<string?>("--leaf", "the leaf value's weights file (default: the initial weights)");
    private readonly Option<string?> _net = Opt.Maybe<string?>("--combat-net",
        "a combat net file, which values the leaves still in progress at the leaf weights' prices (docs: learning §1)");

    public IEnumerable<Option> Options => [_leaf, _net];

    public LeafChoice Read(ParseResult r) => LeafChoice.Read(r.GetValue(_leaf), r.GetValue(_net));
}

/// <summary>--champion FILE for the commands that play a champion: the champion file's options, under the ones given.</summary>
internal sealed class ChampionFileOption : Option<string?>
{
    public ChampionFileOption() : base("--champion") =>
        Description = "plays the champion this champion file names (docs: evaluation §5); options given override the file's";

    /// <summary>
    /// The parse again with the file's options after the ones given: each that the command has and was not given (brain
    /// has no --env: each host names its own). The same parse if the command has no --champion FILE or was not given one.
    /// </summary>
    public static ParseResult Merge(Command root, IReadOnlyList<string> args, ParseResult parsed, ParserConfiguration config)
    {
        var command = parsed.CommandResult.Command;
        if (command.Options.OfType<ChampionFileOption>().FirstOrDefault() is not { } option || parsed.GetValue(option) is not { } path)
            return parsed;
        var file = ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(path));
        var merged = args.ToList();
        foreach (var (key, value) in file.Options)
        {
            if (command.Options.FirstOrDefault(o => o.Name == "--" + key) is not { } known || parsed.GetResult(known) is { Implicit: false }) continue;
            merged.AddRange(["--" + key, value]);
        }
        return root.Parse(merged, config);
    }
}

/// <summary>The options that name a champion (docs: harness §4): its brains, the search's budget and workers, and weights files.</summary>
/// <param name="file">Whether the command also takes --champion FILE to play (run, brain; gate's --champion is the one to beat).</param>
internal sealed class ChampionArgs(bool file)
{
    private readonly ChampionFileOption? _file = file ? new() : null;
    private readonly Option<string> _policy = Opt.Of("--policy", "play", "the combat brain: pimc, plan (SL only), or a placeholder policy")
        .AcceptOnlyFromAmong(["pimc", "plan", .. Policies.CombatNames]);
    private readonly Option<string> _macro = Opt.Of("--macro", "random", "the brain of every decision outside combat: l0 (the macro brain with V) or random")
        .AcceptOnlyFromAmong("random", "l0");
    private readonly BudgetArgs _budget = new();
    private readonly Option<int?> _workers = Opt.Maybe<int?>("--workers", "pimc's and plan's search worker processes (default: one per world, --worlds)");
    private readonly Option<string?> _value = Opt.Maybe<string?>("--value", "l0's V: V v0's weights or a net file of V v2 (default: the initial weights)");
    private readonly LeafArgs _leaf = new();
    private readonly Option<double> _macroExplore = Opt.Of("--macro-explore", 0.0,
        "self-play only: l0 draws each answer by V's values, softmax at this temperature in V's units (0: the best)");
    private readonly Option<double> _combatExplore = Opt.Of("--combat-explore", 0.0,
        "self-play only: pimc draws each searched action by the search's values, softmax at this temperature in leaf units (0: the best)");

    /// <summary>The options that make the champion, without --champion FILE.</summary>
    public IEnumerable<Option> Champion => [_policy, _macro, .. _budget.Options, _workers, _value, .. _leaf.Options, _macroExplore, _combatExplore];

    public IEnumerable<Option> Options => _file != null ? [_file, .. Champion] : Champion;

    public ChampionOptions Read(ParseResult r)
    {
        var policy = r.GetValue(_policy)!;
        var macro = r.GetValue(_macro)!;
        var budget = _budget.Read(r, policy);
        IRunValue value = RunValue.Initial;
        var valueFile = r.GetValue(_value);
        if (valueFile != null)
        {
            if (macro != "l0") throw new CliException("--value weighs l0's V; it needs --macro l0");
            value = RunValue.Read(File.ReadAllText(valueFile));
        }
        double macroExplore = r.GetValue(_macroExplore), combatExplore = r.GetValue(_combatExplore);
        if (macroExplore is not >= 0 || combatExplore is not >= 0) throw new CliException("--macro-explore and --combat-explore are temperatures, from 0");
        if (macroExplore > 0 && macro != "l0") throw new CliException("--macro-explore is l0's; it needs --macro l0");
        if (combatExplore > 0 && policy != "pimc") throw new CliException("--combat-explore is pimc's; it needs --policy pimc");
        var leaf = _leaf.Read(r);
        if (!leaf.IsDefault && policy is not ("pimc" or "plan")) throw new CliException("--leaf and --combat-net value the search's leaves; they need --policy pimc or plan");
        return new(policy, macro, budget, r.GetValue(_workers) ?? budget.Worlds, value, valueFile, leaf, macroExplore, combatExplore);
    }
}
