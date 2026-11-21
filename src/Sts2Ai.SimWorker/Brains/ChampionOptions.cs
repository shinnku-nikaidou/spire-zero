using System.Globalization;
using Sts2Ai.Brain;
using Sts2Ai.Brain.Macro;
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
/// <param name="Cards">For self-play only: l0's card lock (--favor, --favor-by, --refuse, <see cref="CardLock"/>); null for none.</param>
internal sealed record ChampionOptions(string Policy, string Macro, Budget Budget, int Workers, IRunValue? Value, string? ValueFile,
    LeafChoice Leaf, bool OnClone = false, double Explore = 0, double MacroExplore = 0, CardLock? Cards = null)
{
    /// <summary>Whether it explores or locks its cards, which only self-play does.</summary>
    public bool Explores => Explore > 0 || MacroExplore > 0 || Cards != null;

    /// <summary>The node's clone's workers: plan's, which cross a card prompt a plan is rooted at, and l0's on the clone.
    /// A run holds its process's one live game, so the clone plays in other processes; the searches run in this one's
    /// engine, but NoSL self-play's event lines (<paramref name="eventLines"/>, <see cref="Harness"/>) are played on the
    /// node's clone, in one.</summary>
    public WorkerPool? Pool(bool eventLines = false) =>
        Policy == "plan" || OnClone ? new WorkerPool(Program.SelfCommand(), Workers)
        : eventLines ? new WorkerPool(Program.SelfCommand(), 1) : null;

    /// <summary>Whether event lines can be logged with this champion: NoSL's pimc with l0 (run --event-lines).</summary>
    public bool LogsEventLines => Policy == "pimc" && Macro == "l0";

    /// <summary>Whether the combat brain searches: pimc, or plan in SL.</summary>
    private bool Searches => Policy is "pimc" or "plan";

    /// <summary>
    /// One Harness per run. A card prompt during a combat is the combat brain's, a decision like its others.
    /// Placeholders draw from one generator seeded by the run id, so a run plays the same whatever is logged, in
    /// this process or over the wire; so does the exploring l0, from its own. pimc plays in NoSL; in SL, plan plans the
    /// whole combat, and with --afterstates clone l0 plays each option on the node's clone. plan's search runs in the
    /// engine (<see cref="EnginePlanner"/>), in as many threads as the pool has workers, and the pool
    /// (<see cref="Pool"/>) plays the node's clone across a card prompt a plan is rooted at. pimc's runs in the engine,
    /// in --workers threads. With
    /// <paramref name="explain"/>, for a host that shows the answers (the mod), map options carry their routes (<see cref="Dispatcher"/>),
    /// and l0 counts a state as decided once the host says it played the answer (<see cref="Executed"/>): its player may act instead.
    /// With <paramref name="eventLines"/> (NoSL self-play, logging; plans/2026-11-18-1340-nosl-v0.md, 3.3) each event
    /// option whose result its button does not show is also played out on the node's clone as NoSL plays it, in
    /// <paramref name="pool"/> (<see cref="Pool"/>), for V's training: the host then sends the node's checkpoints
    /// (<see cref="Dispatcher.TakesNodes"/>), which no answer is valued on.
    /// With <paramref name="background"/>, for a host whose game takes its time between decisions (the mod), max's
    /// solver searches between them too (<see cref="SolverRoute"/>); its decisions then depend on that time.
    /// </summary>
    public Func<Hello, Dispatcher> Harness(WorkerPool? pool, bool log, bool explain = false, bool eventLines = false, bool background = false) => hello =>
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
        // The mod's brain (background) also gives each combat answer its line's table, for the details panel.
        var combat = Policy == "pimc" ? new EnginePimc((CombatNet)InEnv(), Workers).Route(Budget, Combat, tables: background)
            : pool == null || !Searches ? CombatRoute.Of(Policy, Policies.Combat(Policy, rng))
            : new EnginePlanner((CombatNet)InEnv(), Workers).Route(Budget, Combat, pool, background, tables: background);
        Dispatcher dispatcher;
        if (Macro == "l0")
        {
            // With event lines, each event option valued pending is played out as NoSL plays it, for V's training
            // (EventLines), on the clone the dispatcher gives for them alone.
            var l0 = new MacroBrain(Value!.In(env), Explore > 0 ? new Uniform(Explore, Rng(":explore uniform")) : null, OnClone ? InEnv() : null,
                MacroExplore > 0 ? new Softmax(MacroExplore, Rng(":explore")) : null, hostSaysPlayed: explain,
                eventLines: eventLines ? Value.In(env) : null, cards: Cards);
            dispatcher = new Dispatcher(hello.Run.RunId, env, combat, new(L0, l0), new(L0, l0), log,
                OnClone || eventLines ? node => new PooledClone(pool!, node) : null, explain);
        }
        else dispatcher = new Dispatcher(hello.Run.RunId, env, combat, new("random", new RandomMacro(rng)), new("random", new RandomChoices(rng)), log);
        if (dispatcher.Champion != Name) throw new InvalidOperationException($"champion {dispatcher.Champion} goes by {Name} in champion files");
        return dispatcher;
    };

    /// <summary>The combat brain, pimc or plan by its combat net's name.</summary>
    private string Combat => Policy + Leaf.Suffix;

    /// <summary>l0 by its V's name, then "clone" if it plays on the node's clone, then "explore τ" and "eps ε" if it explores,
    /// then "favor N by B refuse M" if it locks its cards.</summary>
    private string L0 => $"l0 {Value?.Name}" + (OnClone ? " clone" : "") + (MacroExplore > 0 ? FormattableString.Invariant($" explore {MacroExplore}") : "")
        + (Explore > 0 ? FormattableString.Invariant($" eps {Explore}") : "") + (Cards != null ? $" {Cards.Name}" : "");

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
    /// then the size of the combat search (<see cref="Budget.Size"/>) and the solver's nodes a decision, if it solves.</summary>
    public string Shown
    {
        get
        {
            string size = Searches ? $" {Budget.Size}{(Budget.Solve > 0 ? FormattableString.Invariant($" solve {Budget.Solve}") : "")}" : "";
            string[] nets = [.. new[] { Value?.Name, Leaf.Net?.Name }.OfType<string>()];
            return nets.Length > 0 ? $"net {string.Join(" + ", nets)}{size}" : $"{Combat}{size}";
        }
    }

    public override string ToString() =>
        $"policy {Combat}{(Searches ? $" budget {Budget} on {Workers} workers" : "")}, macro {(Macro == "l0" ? L0 : Macro)}";
}
