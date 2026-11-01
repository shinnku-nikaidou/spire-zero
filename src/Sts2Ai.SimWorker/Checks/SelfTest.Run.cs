using MegaCrit.Sts2.Core.Entities.Relics;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

internal static partial class SelfTest
{
    /// <summary>
    /// A run with random policies, stopped at its third combat decision: the SL world loaded from the node's clone must
    /// show that decision's observation, give the run's next observations for the run's actions (up to a card prompt,
    /// which the world answers its own way), and show the first observation again after a restore.
    /// </summary>
    private static void NodeWorldPlaysAsTheRun(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed);
        RunSession? session = null;
        var steps = new List<(CombatObservation Obs, CombatAction Action, int Trace)>();
        NodeClone? clone = null;
        int decisions = 0, node = -1;
        var play = new PlayPolicy(rng);
        var capture = new CapturePolicy(decision =>
        {
            var action = play.Choose(decision);
            if (++decisions >= 3 && steps.Count < 8)
            {
                clone ??= session!.Clone();
                node = session!.NodeStart;
                steps.Add((decision.Observation, action, session.Trace.Count));
            }
            return action;
        });
        string label = $"run {GameSeeds.MakeSeed(seed, 0)}";
        using (session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed, 0)), PromptBrain.Instance, capture, new RandomMacro(rng)))
            while (!session.IsOver && steps.Count < 8 && (clone == null || session.NodeStart == node)) session.Step();
        if (clone == null || steps.Count < 2)
        {
            check("SL world shows the run's combat", false, $"{label}: no combat with 3 decisions");
            return;
        }
        string Json(CombatObservation obs) => ProtocolJson.Serialize(obs);
        using var world = new NodeWorld(pump, clone);
        var root = world.Snapshot();
        check("SL world shows the run's combat", Json(world.Observe()) == Json(steps[0].Obs), label);
        int played = 0;
        for (int k = 0; k + 1 < steps.Count && steps[k + 1].Trace == steps[k].Trace + 1; k++, played++)
        {
            world.Execute(steps[k].Action);
            check("SL world plays as the run", Json(world.Observe()) == Json(steps[k + 1].Obs), $"{label} after {steps[k].Action}");
        }
        world.Restore(root);
        check("SL world restores", Json(world.Observe()) == Json(steps[0].Obs), $"{label} after {played} actions");
    }

    /// <summary>
    /// A run with random policies, to its third node: its first node's clone (the save on arrival and every decision of
    /// the node, the travel last) must load at its horizon, without travelling, and refuse another step (docs:
    /// environments §7, guard 8).
    /// </summary>
    private static void CloneStopsAtTheExit(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed + 1);
        var nodes = new List<(string Save, int Start)>();
        List<string> trace;
        using (var session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 1, 0)), PromptBrain.Instance, new PlayPolicy(rng), new RandomMacro(rng)))
        {
            while (!session.IsOver && nodes.Count < 3)
            {
                if (nodes.Count == 0 || nodes[^1].Start != session.NodeStart) nodes.Add((session.Clone().Save, session.NodeStart));
                session.Step();
            }
            trace = session.Trace.ToList();
        }
        if (nodes.Count < 3)
        {
            check("SL clone stops at its node's exit", false, "the run ended before its third node");
            return;
        }
        var clone = new NodeClone(nodes[1].Save, trace.Skip(nodes[1].Start).Take(nodes[2].Start - nodes[1].Start).ToList());
        using var loaded = RunSession.Load(pump, clone, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance);
        check("SL clone stops at its node's exit", loaded.AtHorizon && clone.Trace[^1].StartsWith(Surfaces.Map + " ", StringComparison.Ordinal),
            $"{clone.Trace.Count} decisions, the last {clone.Trace[^1]}, at horizon {loaded.AtHorizon}");
        check("SL clone refuses to step past its node's exit", Throws(loaded.Step), $"stepped past {clone.Trace[^1]}");
    }

    /// <summary>
    /// The boss gauntlet (<c>run --auto-win</c>): a run with a random macro brain, and a combat brain that must never be
    /// asked, into the second act. Every combat entered, the first act's boss among them, must end won at no HP lost.
    /// </summary>
    private static void AutoWinWinsEveryCombat(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed + 5);
        using var session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 5, 0), AutoWin: true), PromptBrain.Instance,
            RunCommand.Unasked.Instance, new RandomMacro(rng));
        for (int i = 0; i < 5000 && !session.IsOver && session.Run.CurrentActIndex < 1; i++) session.Step();
        var combats = session.Combats;
        bool boss = combats.Any(c => c.Act == 0 && c.Room == "Boss");
        var hurt = combats.FirstOrDefault(c => c.End is not { Died: false } end || end.Hp < c.Hp);
        check("auto-win wins every combat at no HP lost, the first act's boss included", boss && hurt == null && session.Run.CurrentActIndex >= 1,
            $"act {session.Run.CurrentActIndex}, {combats.Count} combats, boss {boss}, first hurt {hurt}");
    }

    /// <summary>
    /// The boss gauntlet (<c>run --gauntlet</c>): a run with random macro and combat brains, into the second act or to its
    /// end. The combat brain is asked in elite and boss fights only, in each of them entered, and in one at least; every
    /// plain monster combat ends won at no HP lost; a lost run died in its last combat, a fought one, whose end has the
    /// enemies' HP (a won combat's are gone: 0 of 0).
    /// </summary>
    private static void GauntletFightsElitesAndBosses(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed + 6);
        var asked = new Asked(new PlayPolicy(rng));
        using var session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 6, 0), Gauntlet: true), PromptBrain.Instance,
            asked, new RandomMacro(rng));
        for (int i = 0; i < 5000 && !session.IsOver && session.Run.CurrentActIndex < 1; i++) session.Step();
        var combats = session.Combats;
        var fought = combats.Where(c => c.Room != "Monster").ToList();
        var hurt = combats.FirstOrDefault(c => c.Room == "Monster" && (c.End is not { Died: false } end || end.Hp < c.Hp));
        var unasked = fought.FirstOrDefault(c => !asked.Encounters.Contains(c.Encounter));
        bool died = session.IsOver && !session.Won;
        bool deathSeen = !died || combats is [.., { Room: not "Monster", End: { Died: true, EnemyMaxHp: > 0 } }];
        check("gauntlet asks the combat brain in elite and boss fights only and wins the rest at no HP lost; a death has the enemies' HP",
            fought.Count > 0 && hurt == null && unasked == null && deathSeen && asked.Rooms.All(r => r is "Elite" or "Boss"),
            $"act {session.Run.CurrentActIndex}, {combats.Count} combats ({fought.Count} fought), died {died}, rooms asked [{string.Join(",", asked.Rooms)}], " +
            $"first hurt {hurt}, first unasked {unasked}, last {combats.LastOrDefault()}");
    }

    /// <summary>
    /// A death that no enemy's turn deals still ends its combat in the record, with the enemies' HP: cards in hand at the
    /// turn's end kill inside the end-turn action, and Royal Poison kills as the first turn starts, before any decision.
    /// </summary>
    private static void DeathsOutsideTheEnemysTurnEndTheirCombat(Pump pump, int seed, Action<string, bool, string> check)
    {
        (string What, string? Deck, string? Relics, bool Asked)[] deaths =
        [
            ("cards in hand at the turn's end", "BECKON*10", null, true),
            ("a relic before the first decision", null, "RING_OF_THE_SNAKE,ROYAL_POISON", false),
        ];
        foreach (var (what, deck, relics, asked) in deaths)
        {
            var rng = new Random(seed + 7);
            var policy = new EndsTurns();
            var setup = new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 7, 0), Loadout: Loadout.Parse(deck, relics, null, null, null));
            using var session = RunSession.Start(pump, setup, PromptBrain.Instance, policy, new RandomMacro(rng));
            var creature = session.Player.Creature;
            for (int i = 0; i < 5000 && !session.IsOver; i++)
            {
                // Before each decision outside the fight, as Neow may heal.
                if (session.Combats.Count == 0) creature.SetCurrentHpInternal(2);
                session.Step();
            }
            check($"a death by {what} ends its combat with the enemies' HP",
                session.IsOver && !session.Won && policy.Asked == asked && session.Combats is [{ End: { Died: true, Hp: 0, EnemyMaxHp: > 0 } }],
                $"over {session.IsOver}, asked {policy.Asked}, combats [{string.Join(", ", session.Combats)}]");
        }
    }

    /// <summary>A combat policy that only ends the turn, and keeps whether it was asked.</summary>
    private sealed class EndsTurns : ICombatPolicy
    {
        public bool Asked { get; private set; }

        public CombatAction Choose(CombatDecision decision)
        {
            Asked = true;
            return CombatAction.EndTurn;
        }
    }

    /// <summary>A combat policy that keeps the rooms and encounters it was asked in.</summary>
    private sealed class Asked(ICombatPolicy policy) : ICombatPolicy
    {
        public readonly HashSet<string?> Rooms = [];
        public readonly HashSet<string?> Encounters = [];

        public CombatAction Choose(CombatDecision decision)
        {
            Rooms.Add(decision.Observation.Room);
            Encounters.Add(decision.Observation.Encounter);
            return policy.Choose(decision);
        }
    }

    /// <summary>
    /// A run with random policies, to its third node, whose first relic is disabled at the start, a status that the save
    /// drops, as Lava Rock's own. The second node's clone must show every observation of the node as the run did, the
    /// disabled relic among them (docs: simulator/worlds §6).
    /// </summary>
    private static void CloneKeepsRelicState(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed + 3);
        var nodes = new List<(string Save, int Start)>();
        var seen = new List<string>();
        List<string> trace;
        using (var session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 3, 0)), PromptBrain.Instance, new PlayPolicy(rng), new RandomMacro(rng),
                   obs => seen.Add(ProtocolJson.Serialize(obs))))
        {
            session.Player.Relics[0].Status = RelicStatus.Disabled;
            while (!session.IsOver && nodes.Count < 3)
            {
                if (nodes.Count == 0 || nodes[^1].Start != session.NodeStart) nodes.Add((session.Clone().Save, session.NodeStart));
                session.Step();
            }
            trace = session.Trace.ToList();
        }
        if (nodes.Count < 3)
        {
            check("SL clone keeps relic state the save drops", false, "the run ended before its third node");
            return;
        }
        var (start, end) = (nodes[1].Start, nodes[2].Start);
        var cloned = new List<string>();
        using (RunSession.Load(pump, new NodeClone(nodes[1].Save, trace.Skip(start).Take(end - start).ToList()),
                   RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, obs => cloned.Add(ProtocolJson.Serialize(obs))))
        {
        }
        bool disabled = seen[start].Contains("\"status\":\"Disabled\"", StringComparison.Ordinal);
        check("SL clone keeps relic state the save drops", disabled && cloned.SequenceEqual(seen.Skip(start).Take(end - start)),
            $"run shows the relic disabled: {disabled}; {cloned.Count} of {end - start} observations, the first differing at " +
            $"{Enumerable.Range(0, Math.Min(cloned.Count, end - start)).FirstOrDefault(i => cloned[i] != seen[start + i], -1)}");
    }

    /// <summary>
    /// A run with random policies, stopped at the first decision of its second combat. The SL planner plans the rest of
    /// that combat on the node's clone (here, and with <paramref name="workers"/> in a pool too, which must plan the
    /// same). A hallway fight at full HP: the plan must win it. The clone, played to the plan's end, must end the combat as
    /// planned and refuse to step past it. Then the run is played again to the decision and follows the plan: each step
    /// must find the observation the plan expects, and the combat must end where the plan ends, as the plan says (docs:
    /// combat-brain §7, test 6).
    /// </summary>
    private static void PlanHoldsInTheRun(Pump pump, int seed, int workers, Action<string, bool, string> check)
    {
        var budget = new Budget(NodesPerWorld: 150, Beam: 4);
        string label = $"run {GameSeeds.MakeSeed(seed + 2, 0)}";
        RunSession? session = null;
        NodeClone? clone = null;
        CombatObservation? at = null;
        var record = new MoveRecord();
        Play((decision, _) =>
        {
            clone ??= session!.Clone();
            at ??= decision.Observation;
            record.Witness(decision.Observation);
            return null;
        });
        var history = record.History;
        if (clone == null || at == null)
        {
            check("SL plan holds in the run", false, $"{label}: no second combat");
            return;
        }

        SlPlan plan;
        using (var world = new NodeWorld(pump, clone))
        {
            plan = new SlPlanner().Plan(at, world, budget.For(at.Room), history);
            world.Restore(new([]));
            foreach (var step in plan.Result.Plan.Prepend(plan.Result.First)) world.Execute(step);
            check("SL world ends its combat as planned", plan.End == CombatStatus.Won && world.Status == plan.End, $"{label}: {world.Status}, planned {plan.End}");
            check("SL world refuses to step past its combat", world.Legal().Count == 0 && Throws(() => world.Execute(CombatAction.EndTurn)), label);
        }
        if (workers > 0)
        {
            using var workerPool = new WorkerPool(Program.SelfCommand(), workers);
            var pooled = new PooledPlanner(workerPool).Plan(clone, budget.For(at.Room), history);
            check("pooled planner plans what the planner plans here", Show(pooled) == Show(plan), $"{label}: here {Show(plan)}; pooled {Show(pooled)}");
            check("no game errors in workers", workerPool.TakeGameErrors() is [], label);
        }

        var steps = new Queue<CombatAction>(plan.Result.Plan.Prepend(plan.Result.First));
        var hashes = new Queue<ulong>(plan.Result.PlanHashes!.Prepend(ProtocolJson.Hash(at)));
        string? broke = null;
        var end = Play((decision, inCombat) =>
        {
            if (!inCombat || broke != null) return null;
            if (!steps.TryDequeue(out var step)) broke = "the combat went on past the plan";
            else if (hashes.Dequeue() != ProtocolJson.Hash(decision.Observation)) broke = $"before {step}, {steps.Count} steps from the end, the observation differs";
            return broke == null ? step : null;
        });
        check("SL plan holds in the run", broke == null && steps.Count == 0 && plan.Result.Plan.Count >= 2,
            $"{label}: {broke ?? $"{steps.Count} steps left of {plan.Result.Plan.Count + 1}"}");
        var w = LeafValue.Default;
        check("SL plan ends the run's combat as planned", end is { Died: false } e && Math.Abs(w.WinBase + w.PerHp * e.Hp / e.MaxHp + w.PerPotion * e.Potions - plan.Value) < 1e-9,
            $"{label}: {end}, planned {plan.End} at {plan.Value:F4}");

        // Plays the run to the first decision of its second combat, then asks follow at each combat decision of that
        // combat (true) and the next decision after it (false), which plays the rest of it, until follow gives null.
        CombatEnd? Play(Func<CombatDecision, bool, CombatAction?> follow)
        {
            var rng = new Random(seed + 2);
            var play = new PlayPolicy(rng);
            bool done = false, following = false;
            var policy = new CapturePolicy(decision =>
            {
                if (!following && session!.Combats.Count == 2) following = true;
                if (!following) return play.Choose(decision);
                var action = follow(decision, true);
                done |= action == null;
                return action ?? play.Choose(decision);
            });
            var macro = new FollowMacro(new RandomMacro(rng), () =>
            {
                if (following && !done) done = follow(null!, false) == null;
            });
            using (session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 2, 0)), PromptBrain.Instance, policy, macro))
            {
                while (!session.IsOver && !done) session.Step();
                return session.Combats.ElementAtOrDefault(1)?.End;
            }
        }

        static string Show(SlPlan p) =>
            $"{p.Result.First} [{string.Join(" ", p.Result.Plan)}] {p.Value:R} {p.End} {p.Nodes} nodes {p.Turns} turns {p.Proved} " +
            string.Join(",", p.Result.PlanHashes!);
    }

    private static bool Throws(Action act)
    {
        try
        {
            act();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>A macro policy that tells <paramref name="before"/> of each decision it is asked.</summary>
    private sealed class FollowMacro(IMacroPolicy macro, Action before) : IMacroPolicy
    {
        public string Choose(MacroDecision decision)
        {
            before();
            return macro.Choose(decision);
        }
    }

    private sealed class CapturePolicy(Func<CombatDecision, CombatAction> choose) : ICombatPolicy
    {
        public CombatAction Choose(CombatDecision decision) => choose(decision);
    }
}
