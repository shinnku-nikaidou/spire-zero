using MegaCrit.Sts2.Core.Entities.Relics;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

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
        using (session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed, 0)), FirstPicks.Instance, capture, new RandomMacro(rng)))
            while (!session.IsOver && steps.Count < 8 && (clone == null || session.NodeStart == node)) session.Step();
        if (clone == null || steps.Count < 2)
        {
            check("SL world shows the run's combat", false, $"{label}: no combat with 3 decisions");
            return;
        }
        static string Json(CombatObservation obs) => ProtocolJson.Serialize(obs);
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
        using (var session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 1, 0)), FirstPicks.Instance, new PlayPolicy(rng), new RandomMacro(rng)))
        {
            while (!session.IsOver && nodes.Count < 3)
            {
                if (nodes.Count == 0 || nodes[^1].Start != session.NodeStart) nodes.Add((session.Clone().Save, session.NodeStart));
                session.Step();
            }
            trace = [.. session.Trace];
        }
        if (nodes.Count < 3)
        {
            check("SL clone stops at its node's exit", false, "the run ended before its third node");
            return;
        }
        var clone = new NodeClone(nodes[1].Save, [.. trace.Skip(nodes[1].Start).Take(nodes[2].Start - nodes[1].Start)]);
        using var loaded = RunSession.Load(pump, clone, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance);
        check("SL clone stops at its node's exit", loaded.AtHorizon && clone.Trace[^1].StartsWith(Surfaces.Map + " ", StringComparison.Ordinal),
            $"{clone.Trace.Count} decisions, the last {clone.Trace[^1]}, at horizon {loaded.AtHorizon}");
        check("SL clone refuses to step past its node's exit", Throws(loaded.Step), $"stepped past {clone.Trace[^1]}");
    }

    /// <summary>
    /// A death that no enemy's turn deals still ends its combat in the record: cards in hand at the turn's end kill inside
    /// the end-turn action, and Royal Poison kills as the first turn starts, before any decision.
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
            using var session = RunSession.Start(pump, setup, FirstPicks.Instance, policy, new RandomMacro(rng));
            var creature = session.Player.Creature;
            for (int i = 0; i < 5000 && !session.IsOver; i++)
            {
                // Before each decision outside the fight, as Neow may heal.
                if (session.Combats.Count == 0) creature.SetCurrentHpInternal(2);
                session.Step();
            }
            check($"a death by {what} ends its combat",
                session.IsOver && !session.Won && policy.Asked == asked && session.Combats is [{ End: { Died: true, Hp: 0 } }],
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
        using (var session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 3, 0)), FirstPicks.Instance, new PlayPolicy(rng), new RandomMacro(rng),
                   obs => seen.Add(ProtocolJson.Serialize(obs))))
        {
            session.Player.Relics[0].Status = RelicStatus.Disabled;
            while (!session.IsOver && nodes.Count < 3)
            {
                if (nodes.Count == 0 || nodes[^1].Start != session.NodeStart) nodes.Add((session.Clone().Save, session.NodeStart));
                session.Step();
            }
            trace = [.. session.Trace];
        }
        if (nodes.Count < 3)
        {
            check("SL clone keeps relic state the save drops", false, "the run ended before its third node");
            return;
        }
        var (start, end) = (nodes[1].Start, nodes[2].Start);
        var cloned = new List<string>();
        using (RunSession.Load(pump, new NodeClone(nodes[1].Save, [.. trace.Skip(start).Take(end - start)]),
                   RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, obs => cloned.Add(ProtocolJson.Serialize(obs))))
        {
        }
        bool disabled = seen[start].Contains("\"status\":\"Disabled\"", StringComparison.Ordinal);
        check("SL clone keeps relic state the save drops", disabled && cloned.SequenceEqual(seen.Skip(start).Take(end - start)),
            $"run shows the relic disabled: {disabled}; {cloned.Count} of {end - start} observations, the first differing at " +
            $"{Enumerable.Range(0, Math.Min(cloned.Count, end - start)).FirstOrDefault(i => cloned[i] != seen[start + i], -1)}");
    }

    /// <summary>
    /// The auto-win rule in the first act (<c>run --auto-win 1</c>): a run with random policies, into the second act or to
    /// its end. In the first act the combat brain is asked in elite and boss fights only, in each of them entered, and
    /// every plain monster fight ends won at no HP lost; in the second act a monster fight is asked as in the normal game;
    /// every observation's run carries the rule. Each node of the first act, loaded from its clone, which keeps the rule,
    /// shows the node's observations as the run did.
    /// </summary>
    private static void AutoWinFightsElitesAndBosses(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed + 6);
        var play = new PlayPolicy(rng);
        var asked = new List<CombatObservation>();
        var combat = new CapturePolicy(decision =>
        {
            asked.Add(decision.Observation);
            return play.Choose(decision);
        });
        var seen = new List<string>();
        var nodes = new List<(string Save, int Start, int Act)>();
        List<string> trace;
        List<CombatEntry> combats;
        using (var session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 6, 0), AutoWin: [0]), FirstPicks.Instance, combat,
                   new RandomMacro(rng), obs => seen.Add(ProtocolJson.Serialize(obs))))
        {
            for (int i = 0; i < 5000 && !session.IsOver && !asked.Any(o => o.Act == 1); i++)
            {
                if (nodes.Count == 0 || nodes[^1].Start != session.NodeStart) nodes.Add((session.Clone().Save, session.NodeStart, session.Run.CurrentActIndex));
                session.Step();
            }
            (trace, combats) = ([.. session.Trace], [.. session.Combats]);
        }
        var monsters = combats.Where(c => c.Act == 0 && c.Room == "Monster").ToList();
        var hurt = monsters.FirstOrDefault(c => c.End is not { Died: false } end || end.Hp < c.Hp);
        var fought = combats.Where(c => c.Room != "Monster").ToList();
        var unasked = fought.FirstOrDefault(c => !asked.Any(o => o.Encounter == c.Encounter));
        var wrong = asked.FirstOrDefault(o => o.Act == 0 && o.Room == "Monster");
        bool second = combats.Any(c => c.Act == 1), secondAsked = asked.Any(o => o.Act == 1 && o.Room == "Monster");
        int unruled = seen.Count(s => !s.Contains("\"auto_win\":[0]", StringComparison.Ordinal));
        check("auto-win in act 1 asks the combat brain in its elites and bosses only, wins its monster fights at no HP lost, asks act 2's",
            monsters.Count > 0 && fought.Count > 0 && hurt == null && unasked == null && wrong == null && second == secondAsked && unruled == 0,
            $"{combats.Count} combats ({monsters.Count} act 1 monsters, {fought.Count} elites and bosses), act 2 reached {second}, asked {secondAsked}; " +
            $"first hurt {hurt}, first unasked {unasked}, first monster asked {wrong?.Encounter}, observations without the rule {unruled}");

        int replayed = 0;
        string? mismatch = null;
        for (int k = 0; k + 1 < nodes.Count && nodes[k + 1].Act == 0; k++)
        {
            var (save, start, _) = nodes[k];
            int end = nodes[k + 1].Start;
            var cloned = new List<string>();
            try
            {
                using (RunSession.Load(pump, new NodeClone(save, [.. trace.Skip(start).Take(end - start)], [0]),
                           RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, obs => cloned.Add(ProtocolJson.Serialize(obs))))
                {
                }
            }
            catch (InvalidOperationException e)
            {
                mismatch ??= $"node {k}: {Logs.FirstLine(e.Message)}";
                continue;
            }
            if (cloned.SequenceEqual(seen.Skip(start).Take(end - start))) replayed++;
            else mismatch ??= $"node {k}: {cloned.Count} of {end - start} observations";
        }
        check("auto-win: each node of act 1 replays from its clone, which keeps the rule", replayed > 0 && mismatch == null,
            $"{replayed} nodes replayed; {mismatch}");
    }

    /// <summary>
    /// A run with random policies, stopped at the first decision of its second combat. The SL planner plans the rest of
    /// that combat on the node's clone (here, and with <paramref name="workers"/> in a pool too, which must plan the
    /// same). A hallway fight at full HP: the plan must win it. The clone, played to the plan's end, must end the combat as
    /// planned and refuse to step past it. Then the run is played again to the decision and follows the plan: each step
    /// must find the observation the plan expects, and the combat must end where the plan ends, as the plan says (docs:
    /// combat-brain §7, test 6).
    /// </summary>
    private static void PlanHoldsInTheRun(Pump pump, int seed, int workers, LeafChoice net, Action<string, bool, string> check)
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
        RunView planned;
        using (var world = new NodeWorld(pump, clone))
        {
            plan = new SlPlanner(net.Leaf).Plan(world, budget, history);
            world.Restore(new([]));
            foreach (var step in plan.Result.Plan.Prepend(plan.Result.First)) world.Execute(step);
            check("SL world ends its combat as planned", plan.End == CombatStatus.Won && world.Status == plan.End, $"{label}: {world.Status}, planned {plan.End}");
            planned = world.Run();
            double worth = net.Leaf.Value(world.Status, world.Observe(), planned, history);
            check("SL plan is worth V of the run its combat leaves", Math.Abs(worth - plan.Value) < 1e-9, $"{label}: {worth:F6}, planned {plan.Value:F6}");
            check("SL world refuses to step past its combat", world.Legal().Count == 0 && Throws(() => world.Execute(CombatAction.EndTurn)), label);
        }
        if (workers > 0)
        {
            using var workerPool = new WorkerPool(Program.SelfCommand(), workers, net.WorkerOptions());
            var pooled = new PooledPlanner(workerPool).Plan(clone, budget, history);
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
        check("SL plan ends the run's combat as planned",
            end is { Died: false } e && e.Hp == planned.Hp && e.MaxHp == planned.MaxHp && e.Potions == planned.Potions.Count(p => p != null),
            $"{label}: {end}, planned {plan.End} at {planned.Hp}/{planned.MaxHp} HP");

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
            using (session = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(seed + 2, 0)), FirstPicks.Instance, policy, macro))
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
        public MacroAnswer Choose(MacroDecision decision)
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
