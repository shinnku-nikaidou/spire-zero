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
    /// <summary>The planner's effort when --effort is not given.</summary>
    public const string DefaultEffort = "medium";

    /// <summary>The budget options' names in a champion file: they belong to the file's combat brain.</summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>
        { "worlds", "nodes", "elite-x", "boss-x", "halving", "follow", "effort", "beam", "easy-beam", "replan", "loss-plans" };

    private readonly Option<int> _worlds = Opt.Of("--worlds", 8, "pimc's determinized worlds per decision");
    private readonly Option<int?> _nodes = Opt.Maybe<int?>("--nodes", "search nodes per world (default 300; plan: per state and turn, default by --effort)");
    private readonly Option<double?> _elite = Opt.Maybe<double?>("--elite-x", "multiplies the budget in elite rooms: pimc's nodes per world, plan's beam (default 1; plan: by --effort)");
    private readonly Option<double?> _boss = Opt.Maybe<double?>("--boss-x", "multiplies the budget in boss rooms: pimc's nodes per world, plan's beam (default 1; plan: by --effort)");
    private readonly Option<bool> _halving = Opt.Of("--halving", false, "shares the nodes over the first actions by sequential halving");
    private readonly Option<bool> _follow = Opt.Of("--follow", false, "plays the rest of a turn every world planned the same without searching again");
    private readonly Option<string?> _effort = Opt.Maybe<string?>("--effort", $"plan's budget preset: {string.Join(", ", Budget.Efforts)} (default {DefaultEffort})")
        .AcceptOnlyFromAmong([.. Budget.Efforts]);
    private readonly Option<int?> _beam = Opt.Maybe<int?>("--beam", "plan's states kept from one turn to the next (default by --effort)");
    private readonly Option<int?> _easyBeam = Opt.Maybe<int?>("--easy-beam", "plan's states kept when its greedy dive already wins; 0: --beam (default by --effort)");
    private readonly Option<int?> _replan = Opt.Maybe<int?>("--replan", "plan's turns of an unproved losing plan played before planning again, 0: to its end (default by --effort)");
    private readonly Option<int?> _lossPlans = Opt.Maybe<int?>("--loss-plans",
        "plan's unproved losing plans in a row in one combat, the last played to its end; 0: no limit (default by --effort)");

    public IEnumerable<Option> Options => [_worlds, _nodes, _elite, _boss, _halving, _follow, _effort, _beam, _easyBeam, _replan, _lossPlans];

    /// <summary>The budget of the combat brain <paramref name="brain"/> (pimc, plan, or a placeholder).</summary>
    /// <param name="planElsewhere">Whether plan's options belong to another brain of the command (brain's SL hosts).</param>
    public Budget Read(ParseResult r, string brain, bool planElsewhere = false)
    {
        var budget = new Budget(r.GetValue(_worlds), r.GetValue(_nodes) ?? 300, r.GetValue(_elite) ?? 1, r.GetValue(_boss) ?? 1, r.GetValue(_halving),
            r.GetValue(_follow));
        bool planOptions = r.GetValue(_effort) != null || r.GetValue(_beam) != null || r.GetValue(_easyBeam) != null || r.GetValue(_replan) != null
            || r.GetValue(_lossPlans) != null;
        if (brain != "plan")
            return planElsewhere || !planOptions ? budget
                : throw new CliException("--effort, --beam, --easy-beam, --replan and --loss-plans are plan's; they need --policy plan");
        if (budget.Halving || budget.Follow) throw new CliException("--halving and --follow are pimc's; plan follows its plan always");
        var effort = Budget.Effort(r.GetValue(_effort) ?? DefaultEffort);
        budget = budget with
        {
            NodesPerWorld = r.GetValue(_nodes) ?? effort.NodesPerWorld,
            Elite = r.GetValue(_elite) ?? effort.Elite,
            Boss = r.GetValue(_boss) ?? effort.Boss,
            Beam = r.GetValue(_beam) ?? effort.Beam,
            EasyBeam = r.GetValue(_easyBeam) ?? effort.EasyBeam,
            Replan = r.GetValue(_replan) ?? effort.Replan,
            LossPlans = r.GetValue(_lossPlans) ?? effort.LossPlans,
        };
        if (budget.Beam < 1 || budget.EasyBeam < 0) throw new CliException("--beam is at least 1, --easy-beam at least 0");
        if (budget.Elite <= 0 || budget.Boss <= 0) throw new CliException("--elite-x and --boss-x are above 0");
        return budget.Replan >= 0 && budget.LossPlans >= 0 ? budget : throw new CliException("--replan and --loss-plans are at least 0");
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
    /// has no --env: each host names its own). The file's budget is its combat brain's, so a --policy given that is not
    /// the file's keeps its own budget (plan's effort). The same parse if the command has no --champion FILE or was not
    /// given one.
    /// </summary>
    public static ParseResult Merge(Command root, IReadOnlyList<string> args, ParseResult parsed, ParserConfiguration config)
    {
        var command = parsed.CommandResult.Command;
        if (command.Options.OfType<ChampionFileOption>().FirstOrDefault() is not { } option || parsed.GetValue(option) is not { } path)
            return parsed;
        var file = ProtocolJson.Deserialize<ChampionFile>(File.ReadAllText(path));
        bool otherBrain = command.Options.FirstOrDefault(o => o.Name == "--policy") is Option<string> policy && parsed.GetResult(policy) is { Implicit: false }
            && parsed.GetValue(policy) != file.Options.GetValueOrDefault("policy");
        var merged = args.ToList();
        foreach (var (key, value) in file.Options)
        {
            if (command.Options.FirstOrDefault(o => o.Name == "--" + key) is not { } known || parsed.GetResult(known) is { Implicit: false }) continue;
            if (otherBrain && BudgetArgs.Keys.Contains(key)) continue;
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
    private readonly Option<string> _afterstates = Opt.Of("--afterstates", "screen",
            "l0's afterstates: screen predicts them from the screen; clone, in SL only, plays each option on the node's clone (docs: macro-brain §2)")
        .AcceptOnlyFromAmong("screen", "clone");
    private readonly Option<string> _leafPrices = Opt.Of("--leaf-prices", "leaf",
            "what the SL planner's leaves price a combat's end at: leaf, the leaf weights'; v, V's at the decision, for a --combat-net with a net --value (docs: combat-brain §4)")
        .AcceptOnlyFromAmong("leaf", "v");
    private readonly Option<double> _macroExplore = Opt.Of("--macro-explore", 0.0,
        "self-play only: l0 draws each answer by V's values, softmax at this temperature in V's units (0: the best)");
    private readonly Option<double> _combatExplore = Opt.Of("--combat-explore", 0.0,
        "self-play only: pimc draws each searched action by the search's values, softmax at this temperature in leaf units (0: the best)");

    /// <summary>The options that make the champion, without --champion FILE.</summary>
    private readonly Option<double> _cardExplore = Opt.Of("--card-explore", 0.0,
        "self-play only: at each card reward, the chance that l0 draws its outcome evenly among its cards and skipping, whatever V says");

    public IEnumerable<Option> Champion => [_policy, _macro, .. _budget.Options, _workers, _value, _afterstates, .. _leaf.Options, _leafPrices, _macroExplore,
        _combatExplore, _cardExplore];

    public IEnumerable<Option> Options => _file != null ? [_file, .. Champion] : Champion;

    /// <param name="policy">The combat brain instead of --policy's (brain's SL hosts).</param>
    /// <param name="planElsewhere">Whether plan's budget options belong to another champion of the command.</param>
    public ChampionOptions Read(ParseResult r, string? policy = null, bool planElsewhere = false)
    {
        policy ??= r.GetValue(_policy)!;
        var macro = r.GetValue(_macro)!;
        var budget = _budget.Read(r, policy, planElsewhere);
        IRunValue value = RunValue.Initial;
        var valueFile = r.GetValue(_value);
        if (valueFile != null)
        {
            if (macro != "l0") throw new CliException("--value weighs l0's V; it needs --macro l0");
            value = RunValue.Read(File.ReadAllText(valueFile));
        }
        bool clone = r.GetValue(_afterstates) == "clone";
        if (clone && macro != "l0") throw new CliException("--afterstates is l0's; it needs --macro l0");
        double macroExplore = r.GetValue(_macroExplore), combatExplore = r.GetValue(_combatExplore);
        if (macroExplore is not >= 0 || combatExplore is not >= 0) throw new CliException("--macro-explore and --combat-explore are temperatures, from 0");
        if (macroExplore > 0 && macro != "l0") throw new CliException("--macro-explore is l0's; it needs --macro l0");
        if (combatExplore > 0 && policy != "pimc") throw new CliException("--combat-explore is pimc's; it needs --policy pimc");
        double cardExplore = r.GetValue(_cardExplore);
        if (cardExplore is not (>= 0 and <= 1)) throw new CliException("--card-explore is a share of card rewards, from 0 to 1");
        if (cardExplore > 0 && macro != "l0") throw new CliException("--card-explore is l0's; it needs --macro l0");
        var leaf = _leaf.Read(r);
        if (!leaf.IsDefault && policy is not ("pimc" or "plan")) throw new CliException("--leaf and --combat-net value the search's leaves; they need --policy pimc or plan");
        bool vPrices = r.GetValue(_leafPrices) == "v";
        if (vPrices && (policy != "plan" || leaf.Net == null || value is not RunNet))
            throw new CliException("--leaf-prices v prices plan's combat net's endings in V's floors: it needs --policy plan, --combat-net and a net --value");
        return new(policy, macro, budget, r.GetValue(_workers) ?? budget.Worlds, value, valueFile, leaf, macroExplore, combatExplore, clone,
            cardExplore, vPrices);
    }
}
