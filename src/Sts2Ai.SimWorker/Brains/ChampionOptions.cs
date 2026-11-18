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
/// <param name="Engine">Whether plan searches in the engine (--combat-world engine, run's and brain's default,
/// <see cref="EnginePlanner"/>) rather than on the node's clone. It is the same champion either way, by the same name.
/// pimc always searches in the engine (<see cref="EnginePimc"/>): its worlds are drawn from the real combat there.</param>
internal sealed record ChampionOptions(string Policy, string Macro, Budget Budget, int Workers, IRunValue? Value, string? ValueFile,
    LeafChoice Leaf, bool OnClone = false, double Explore = 0, double MacroExplore = 0, bool Engine = false)
{
    /// <summary>Whether it explores, which only self-play does.</summary>
    public bool Explores => Explore > 0 || MacroExplore > 0;

    /// <summary>The search's workers (plan's, l0's on the clone). A run holds its process's one live game, so search on
    /// the node's clone runs in other processes; pimc's runs in this one's engine and needs none, but NoSL self-play's
    /// event lines (<paramref name="eventLines"/>, <see cref="Harness"/>) are played on the node's clone, in one.</summary>
    public WorkerPool? Pool(bool eventLines = false) =>
        Policy == "plan" || OnClone ? new WorkerPool(Program.SelfCommand(), Workers, Leaf.WorkerOptions())
        : eventLines ? new WorkerPool(Program.SelfCommand(), 1, Leaf.WorkerOptions()) : null;

    /// <summary>Whether event lines can be logged with this champion: NoSL's pimc with l0 (run --event-lines).</summary>
    public bool LogsEventLines => Policy == "pimc" && Macro == "l0";

    /// <summary>Whether the combat brain searches: pimc, or plan in SL.</summary>
    private bool Searches => Policy is "pimc" or "plan";

    /// <summary>
    /// One Harness per run. A card prompt during a combat is the combat brain's, a decision like its others.
    /// Placeholders draw from one generator seeded by the run id, so a run plays the same whatever is logged, in
    /// this process or over the wire; so does the exploring l0, from its own. pimc plays in NoSL; in SL, plan plans the
    /// whole combat on the node's clone, and with --afterstates clone l0 plays each option on it.
    /// plan's search runs in <paramref name="pool"/> (<see cref="Pool"/>); with --combat-world engine, it runs in the
    /// engine, in as many threads as the pool has workers, and the pool plays the node's clone across a card prompt a
    /// plan is rooted at. pimc's runs in the engine, in --workers threads. With
    /// <paramref name="explain"/>, for a host that shows the answers (the mod), map options carry their routes (<see cref="Dispatcher"/>),
    /// and l0 counts a state as decided once the host says it played the answer (<see cref="Executed"/>): its player may act instead.
    /// With <paramref name="eventLines"/> (NoSL self-play, logging; plans/2026-11-18-1340-nosl-v0.md, 3.3) each event
    /// option whose result its button does not show is also played out on the node's clone as NoSL plays it, in
    /// <paramref name="pool"/> (<see cref="Pool"/>), for V's training: the host then sends the node's checkpoints
    /// (<see cref="Dispatcher.TakesNodes"/>), which no answer is valued on.
    /// </summary>
    public Func<Hello, Dispatcher> Harness(WorkerPool? pool, bool log, bool explain = false, bool eventLines = false) => hello =>
    {
        if (!Enum.TryParse<PlayEnvironment>(hello.Env, out var env)) throw new NotSupportedException($"no environment {hello.Env}");
        if (Policy == "plan" && env != PlayEnvironment.SL) throw new NotSupportedException($"plan plays in SL only, not in {env}");
        if (Policy == "pimc" && env != PlayEnvironment.NoSL) throw new NotSupportedException($"pimc plays in NoSL only, not in {env}");
        if (OnClone && env != PlayEnvironment.SL) throw new NotSupportedException($"l0 plays on the node's clone in SL only, not in {env}");
        if (Macro == "l0" && env == PlayEnvironment.SL && !OnClone)
            throw new NotSupportedException("l0 plays on the node's clone in SL (--afterstates clone): the screen's predicted afterstates are NoSL's");
        if (eventLines && (!log || !LogsEventLines || env != PlayEnvironment.NoSL || pool == null))
            throw new NotSupportedException("event lines are NoSL self-play's log: pimc and l0 in NoSL, logging, with the clone's worker (Pool)");
        Random Rng(string stream) => new(unchecked((int)ProtocolJson.Hash(hello.Run.RunId + stream)));
        var rng = Rng("");
        // The nets read every state in the run's environment (env:NoSL in NoSL); the workers' leaves are the planner's,
        // in SL (Serve).
        ICombatLeaf InEnv() => Leaf.Leaf.In(env);
        var combat = Policy == "pimc" ? new EnginePimc(InEnv(), Workers).Route(Budget, Combat)
            : pool == null || !Searches ? CombatRoute.Of(Policy, Policies.Combat(Policy, rng))
            : Engine ? new EnginePlanner(InEnv(), Workers).Route(Budget, Combat, pool) : new PooledPlanner(pool).Route(Budget, Combat);
        Dispatcher dispatcher;
        if (Macro == "l0")
        {
            // With event lines, each event option valued pending is played out as NoSL plays it, for V's training
            // (EventLines), on the clone the dispatcher gives for them alone.
            var l0 = new MacroBrain(Value!.In(env), Explore > 0 ? new Uniform(Explore, Rng(":explore uniform")) : null, OnClone ? InEnv() : null,
                MacroExplore > 0 ? new Softmax(MacroExplore, Rng(":explore")) : null, hostSaysPlayed: explain,
                eventLines: eventLines ? Value.In(env) : null);
            dispatcher = new Dispatcher(hello.Run.RunId, env, combat, new(L0, l0), new(L0, l0), log,
                OnClone || eventLines ? node => new PooledClone(pool!, node) : null, explain);
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

    /// <summary>The champion as a player sees it on the mod's panel (<see cref="Ready.Shown"/>): its V and combat net,
    /// then the size of the combat search (<see cref="Budget.Size"/>).</summary>
    public string Shown
    {
        get
        {
            string size = Searches ? $" {Budget.Size}" : "";
            string[] nets = [.. new[] { Value?.Name, Leaf.Net?.Name }.OfType<string>()];
            return nets.Length > 0 ? $"net {string.Join(" + ", nets)}{size}" : $"{Combat}{size}";
        }
    }

    public override string ToString() =>
        $"policy {Combat}{(Searches ? $" budget {Budget} on {Workers} workers" : "")}{(Engine ? " in the engine" : "")}, macro {(Macro == "l0" ? L0 : Macro)}";
}
