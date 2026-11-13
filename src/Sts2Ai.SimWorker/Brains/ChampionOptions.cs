using System.Globalization;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Brains;

/// <summary>The champion that --policy and --macro name, pimc's budget, and l0's V (--value).</summary>
/// <param name="Value">l0's V; null with no l0.</param>
/// <param name="ValueFile">Where <paramref name="Value"/> came from.</param>
/// <param name="Leaf">pimc's and plan's combat net (--combat-net).</param>
/// <param name="OnClone">Whether l0 plays each option on the node's clone (--afterstates clone; SL only).</param>
/// <param name="Explore">For self-play only: the share ε of macro decisions whose answer l0 draws
/// evenly, whatever V says (--explore, <see cref="Uniform"/>); 0 for none.</param>
/// <param name="MacroExplore">For self-play only: l0's softmax temperature τ over the log of V's values for the decisions
/// not drawn evenly (--macro-explore, <see cref="Softmax"/>); 0 for none, V's best.</param>
/// <param name="Engine">Whether plan searches in the engine (--combat-world engine, <see cref="EnginePlanner"/>) rather than
/// on the node's clone. It is the same champion either way, by the same name.</param>
internal sealed record ChampionOptions(string Policy, string Macro, Budget Budget, int Workers, IRunValue? Value, string? ValueFile,
    LeafChoice Leaf, bool OnClone = false, double Explore = 0, double MacroExplore = 0, bool Engine = false)
{
    /// <summary>Whether it explores, which only self-play does.</summary>
    public bool Explores => Explore > 0 || MacroExplore > 0;

    /// <summary>The search's workers (pimc's, plan's, l0's on the clone). A run holds its process's one live game, so search runs in other processes.</summary>
    public WorkerPool? Pool() => Searches || OnClone ? new WorkerPool(Program.SelfCommand(), Workers, Leaf.WorkerOptions()) : null;

    /// <summary>Whether the combat brain searches: pimc, or plan in SL.</summary>
    private bool Searches => Policy is "pimc" or "plan";

    /// <summary>
    /// One Harness per run. A card prompt during a combat is the combat brain's, a decision like its others.
    /// Placeholders draw from one generator seeded by the run id, so a run plays the same whatever is logged, in
    /// this process or over the wire; so does the exploring l0, from its own. pimc plays in NoSL; in SL, plan plans the
    /// whole combat on the node's clone, and with --afterstates clone l0 plays each option on it.
    /// The search runs in <paramref name="pool"/> (<see cref="Pool"/>); with --combat-world engine, plan's runs in the
    /// engine, in as many threads as the pool has workers, and the pool serves the plans rooted at a card prompt. With
    /// <paramref name="explain"/>, for a host that shows the answers, map options carry their routes (<see cref="Dispatcher"/>).
    /// </summary>
    public Func<Hello, Dispatcher> Harness(WorkerPool? pool, bool log, bool explain = false) => hello =>
    {
        if (!Enum.TryParse<PlayEnvironment>(hello.Env, out var env)) throw new NotSupportedException($"no environment {hello.Env}");
        if (Policy == "plan" && env != PlayEnvironment.SL) throw new NotSupportedException($"plan plays in SL only, not in {env}");
        if (Policy == "pimc" && env != PlayEnvironment.NoSL) throw new NotSupportedException($"pimc plays in NoSL only, not in {env}");
        if (OnClone && env != PlayEnvironment.SL) throw new NotSupportedException($"l0 plays on the node's clone in SL only, not in {env}");
        if (Macro == "l0" && env == PlayEnvironment.SL && !OnClone)
            throw new NotSupportedException("l0 plays on the node's clone in SL (--afterstates clone): the screen's predicted afterstates are NoSL's");
        Random Rng(string stream) => new(unchecked((int)ProtocolJson.Hash(hello.Run.RunId + stream)));
        var rng = Rng("");
        var combat = pool == null || !Searches ? CombatRoute.Of(Policy, Policies.Combat(Policy, rng))
            : Policy == "plan" ? Engine ? new EnginePlanner(Leaf.Leaf, Workers, new PooledPlanner(pool)).Route(Budget, Combat) : new PooledPlanner(pool).Route(Budget, Combat)
            : new PooledPimc(pool).Route(Budget, Combat);
        Dispatcher dispatcher;
        if (Macro == "l0")
        {
            var l0 = new MacroBrain(Value!, Explore > 0 ? new Uniform(Explore, Rng(":explore uniform")) : null, OnClone ? Leaf.Leaf : null,
                MacroExplore > 0 ? new Softmax(MacroExplore, Rng(":explore")) : null);
            dispatcher = new Dispatcher(hello.Run.RunId, env, combat, new(L0, l0), new(L0, l0), log,
                OnClone ? node => new PooledClone(pool!, node) : null, explain);
        }
        else dispatcher = new Dispatcher(hello.Run.RunId, env, combat, new("random", new RandomMacro(rng)), new("random", new RandomChoices(rng)), log);
        if (dispatcher.Champion != Name) throw new InvalidOperationException($"champion {dispatcher.Champion} goes by {Name} in champion files");
        return dispatcher;
    };

    /// <summary>The combat brain, pimc or plan by its combat net's name.</summary>
    private string Combat => Policy + Leaf.Suffix;

    /// <summary>l0 by its V's name, then "clone" if it plays on the node's clone, then "explore τ" and "eps ε" if it explores.</summary>
    private string L0 => $"l0 {Value?.Name}" + (OnClone ? " clone" : "") + (MacroExplore > 0 ? FormattableString.Invariant($" explore {MacroExplore}") : "")
        + (Explore > 0 ? FormattableString.Invariant($" eps {Explore}") : "");

    /// <summary>The champion as Harness names it (<see cref="Dispatcher.Champion"/>), which runs check.</summary>
    public string Name
    {
        get
        {
            string macro = Macro == "l0" ? L0 : "random";
            return $"combat {Combat}{(Searches ? $" {Budget}" : "")}, combat prompts first, macro {macro}, macro prompts {macro}";
        }
    }

    public override string ToString() =>
        $"policy {Combat}{(Searches ? $" budget {Budget} on {Workers} workers" : "")}{(Engine ? " in the engine" : "")}, macro {(Macro == "l0" ? L0 : Macro)}";
}
