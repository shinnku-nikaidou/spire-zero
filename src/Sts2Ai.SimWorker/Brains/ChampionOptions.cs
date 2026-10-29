using System.Globalization;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>The champion that --policy and --macro name (docs: harness §4), pimc's budget, and l0's V (--value).</summary>
/// <param name="ValueFile">Where <paramref name="Value"/> came from; null for the initial weights.</param>
/// <param name="MacroExplore">For self-play only (docs: learning §5): l0's softmax temperature over V's values, in V's
/// units (--macro-explore); 0 for none.</param>
/// <param name="CombatExplore">The same for pimc over the search's values, in the leaf's units (--combat-explore).</param>
/// <param name="OnClone">Whether l0 plays each option on the node's clone (--afterstates clone; SL only).</param>
/// <param name="Explore">For self-play only (plan v6 §3): the share ε of macro decisions whose answer l0 draws evenly,
/// whatever V says (--explore, <see cref="Uniform"/>); 0 for none.</param>
/// <param name="VPrices">Whether the SL planner's leaves price a combat's ends at V's prices (--leaf-prices v,
/// <see cref="PricedLeaf"/>) instead of the leaf weights'.</param>
internal sealed record ChampionOptions(string Policy, string Macro, Budget Budget, int Workers, IRunValue Value, string? ValueFile,
    LeafChoice Leaf, double MacroExplore, double CombatExplore, bool OnClone = false, double Explore = 0, bool VPrices = false)
{
    /// <summary>Whether it explores, which only self-play does.</summary>
    public bool Explores => MacroExplore > 0 || CombatExplore > 0 || Explore > 0;

    /// <summary>The search's workers (pimc's, plan's, l0's on the clone). A run holds its process's one live game, so search runs in other processes.</summary>
    public WorkerPool? Pool() => Searches || OnClone ? new WorkerPool(Program.SelfCommand(), Workers, Leaf.WorkerOptions()) : null;

    /// <summary>Whether the combat brain searches: pimc, or plan in SL.</summary>
    private bool Searches => Policy is "pimc" or "plan";

    /// <summary>
    /// One Harness per run. Combat prompts go to p0 (<see cref="PromptBrain"/>), which search worlds ask too.
    /// Placeholders draw from one generator seeded by the run id, so a run plays the same whatever is logged, in
    /// this process or over the wire; so do the exploring brains, each from its own. In SL, pimc searches this turn on
    /// the node's clone and plan the whole combat (docs: combat-brain §3), and with --afterstates clone l0 plays each
    /// option on it (docs: macro-brain §2); nothing else looks further than in NoSL. The search runs in
    /// <paramref name="pool"/> (<see cref="Pool"/>).
    /// </summary>
    public Func<Hello, Dispatcher> Harness(WorkerPool? pool, bool log) => hello =>
    {
        if (!Enum.TryParse<PlayEnvironment>(hello.Env, out var env)) throw new NotSupportedException($"no environment {hello.Env}");
        if (Policy == "plan" && env != PlayEnvironment.SL) throw new NotSupportedException($"plan plays in SL only, not in {env}");
        if (OnClone && env != PlayEnvironment.SL) throw new NotSupportedException($"l0 plays on the node's clone in SL only, not in {env}");
        Leaf.Check(env);
        Random Rng(string stream) => new(unchecked((int)ProtocolJson.Hash(hello.Run.RunId + stream)));
        var rng = Rng("");
        var combat = pool == null || !Searches ? CombatRoute.Of(Policy, Policies.Combat(Policy, rng))
            : Policy == "plan" ? new PooledPlanner(pool).Route(Budget, Combat, VPrices ? run => PricedLeaf.Of(Value, run) : null)
            : new PooledPimc(pool).Route(Budget, Combat);
        if (CombatExplore > 0) combat = combat.Exploring(new Softmax(CombatExplore, Rng(":explore combat")));
        var prompts = new Route<IChoicePolicy>("p0", PromptBrain.Instance);
        Dispatcher dispatcher;
        if (Macro == "l0")
        {
            var l0 = new MacroBrain(Value, MacroExplore > 0 ? new Softmax(MacroExplore, Rng(":explore")) : null,
                Explore > 0 ? new Uniform(Explore, Rng(":explore uniform")) : null);
            dispatcher = new Dispatcher(hello.Run.RunId, env, combat, prompts, new(L0, l0), new(L0, l0), log,
                OnClone ? node => new PooledClone(pool!, node) : null);
        }
        else dispatcher = new Dispatcher(hello.Run.RunId, env, combat, prompts, new("random", new RandomMacro(rng)), new("random", new RandomChoices(rng)), log);
        if (dispatcher.Champion != Name) throw new InvalidOperationException($"champion {dispatcher.Champion} goes by {Name} in champion files");
        return dispatcher;
    };

    /// <summary>The combat brain, pimc or plan by its leaf's names unless it is the default one, "v prices" if V prices its
    /// leaves' ends, then its exploration if any.</summary>
    private string Combat => Policy + Leaf.Suffix + (VPrices ? " v prices" : "") + Exploration(CombatExplore);

    /// <summary>l0 by its V's name, unless V has the initial weights, then "clone" if it plays on the node's clone, then its exploration if any
    /// ("explore T" for a softmax, "eps ε" for even draws).</summary>
    private string L0 => (Value == RunValue.Initial ? "l0" : $"l0 {Value.Name}") + (OnClone ? " clone" : "") + Exploration(MacroExplore)
        + (Explore > 0 ? FormattableString.Invariant($" eps {Explore}") : "");

    private static string Exploration(double temperature) => temperature > 0 ? FormattableString.Invariant($" explore {temperature}") : "";

    /// <summary>The champion as Harness names it (<see cref="Dispatcher.Champion"/>), which runs check.</summary>
    public string Name
    {
        get
        {
            string macro = Macro == "l0" ? L0 : "random";
            return $"combat {Combat}{(Searches ? $" {Budget}" : "")}, combat prompts p0, macro {macro}, macro prompts {macro}";
        }
    }

    /// <summary>The options that play this champion, for a champion file.</summary>
    public Dictionary<string, string> Options(PlayEnvironment env)
    {
        var options = new Dictionary<string, string> { ["env"] = env.ToString(), ["policy"] = Policy, ["macro"] = Macro };
        if (Policy == "plan")
        {
            options["beam"] = $"{Budget.Beam}";
            if (Budget.EasyBeam > 0) options["easy-beam"] = $"{Budget.EasyBeam}";
            options["replan"] = $"{Budget.Replan}";
            options["loss-plans"] = $"{Budget.LossPlans}";
        }
        if (Policy == "pimc") options["worlds"] = $"{Budget.Worlds}";
        if (Searches)
        {
            options["nodes"] = $"{Budget.NodesPerWorld}";
            if (Budget.Elite != 1) options["elite-x"] = Budget.Elite.ToString(CultureInfo.InvariantCulture);
            if (Budget.Boss != 1) options["boss-x"] = Budget.Boss.ToString(CultureInfo.InvariantCulture);
            if (Budget.Halving) options["halving"] = "true";
            if (Budget.Follow) options["follow"] = "true";
        }
        if (ValueFile != null) options["value"] = ValueFile;
        if (OnClone) options["afterstates"] = "clone";
        if (MacroExplore > 0) options["macro-explore"] = MacroExplore.ToString(CultureInfo.InvariantCulture);
        if (Explore > 0) options["explore"] = Explore.ToString(CultureInfo.InvariantCulture);
        if (CombatExplore > 0) options["combat-explore"] = CombatExplore.ToString(CultureInfo.InvariantCulture);
        if (Leaf.WeightsPath != null) options["leaf"] = Leaf.WeightsPath;
        if (Leaf.NetPath != null) options["combat-net"] = Leaf.NetPath;
        if (VPrices) options["leaf-prices"] = "v";
        return options;
    }

    /// <summary>
    /// The options of this challenger as the character's champion, with its character and ascension. Each weights file
    /// it plays with (V, the leaf weights, the combat net) must be in the character's weights folder, weights/CHARACTER,
    /// where a name is used once, so the champion file names it where it is (docs: learning §6).
    /// </summary>
    public Dictionary<string, string> Promoted(PlayEnvironment env, string character, int ascension)
    {
        var options = Options(env);
        options["character"] = character;
        options["asc"] = ascension.ToString(CultureInfo.InvariantCulture);
        var folder = Layout.Weights(character);
        foreach (var key in new[] { "value", "leaf", "combat-net" })
            if (options.TryGetValue(key, out var path) && Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(folder))
                throw new CliException($"not promoted: {path} is not in {folder}, where a champion's weights files are (docs: learning §6)");
        return options;
    }

    public override string ToString() =>
        $"policy {Combat}{(Searches ? $" budget {Budget} on {Workers} workers" : "")}, macro {(Macro == "l0" ? L0 : Macro)}";
}
