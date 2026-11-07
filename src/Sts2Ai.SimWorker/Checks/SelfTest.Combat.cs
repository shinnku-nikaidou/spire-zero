using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

internal static partial class SelfTest
{
    /// <summary>
    /// A run for a combat started on its own, which has none, for the leaves to price its endings with: the combat's
    /// character, act, HP, gold, relics and potions, its piles as the deck, and a map of one point.
    /// </summary>
    private static RunView Alone(CombatObservation obs) => new()
    {
        Character = obs.Player.Character,
        Ascension = obs.Ascension,
        Act = obs.Act,
        ActId = "<none>",
        Floor = 1,
        Hp = obs.Player.Hp,
        MaxHp = obs.Player.MaxHp,
        Gold = obs.Player.Gold,
        Deck = [.. obs.Hand.Concat(obs.Draw).Concat(obs.Discard).Concat(obs.Exhaust).Select(c => c with { Uid = null })
            .OrderBy(ProtocolJson.CardKey, StringComparer.Ordinal)],
        Relics = obs.Relics,
        Potions = obs.Potions,
        Map = new MapView { Points = [new MapPointView { Col = 0, Row = 0, Type = "Monster", Next = [] }], Visited = [], Boss = "<none>" },
    };

    private sealed record Recording(List<CombatSnapshot> Snapshots, List<string> Observations, List<string> Hidden, string Trace, List<string> ProtocolErrors);

    /// <summary>
    /// A few decisions, each searched here and in a pool of <paramref name="workers"/> serve processes with the same
    /// seeds and the same combat net: the candidates, their values and the plan must be the same.
    /// </summary>
    private static void PooledMatches(Pump pump, CombatPool combats, int seed, int workers, LeafChoice net, Action<string, bool, string> check)
    {
        var brain = new PimcBrain(net.Leaf);
        using var workerPool = new WorkerPool(Program.SelfCommand(), workers, net.WorkerOptions());
        var pooled = new PooledPimc(workerPool);
        for (int i = 0; i < 4; i++)
        {
            var setup = combats.Setup(i, GameSeeds.MakeSeed(seed + 2, i));
            var encounter = setup.Encounter;
            CombatObservation obs;
            IReadOnlyList<CombatAction> legal;
            var rng = new Random(i);
            using (var session = CombatSession.Start(pump, setup, new RandomChoices(rng)))
            {
                var policy = new PlayPolicy(rng);
                for (int a = 0; a < i && !CombatSession.IsOver; a++) session.Execute(policy.Choose(session.Decision()));
                (obs, legal) = (session.Observation(), session.LegalActions());
            }
            ulong decisionSeed = CombatSession.StableHash($"{setup.Seed}:{i}");
            string buildSeed = GameSeeds.MakeSeed((int)decisionSeed, 0);
            var budget = new Budget(4, 120);
            CombatDecisionResult here;
            using (var worlds = SimWorlds.NoSL(pump, obs, buildSeed, Alone(obs)))
                here = brain.Decide(obs, legal, worlds, budget, decisionSeed, []);
            var there = pooled.Decide(obs, legal, buildSeed, budget, decisionSeed, [], Alone(obs));
            check("pooled search matches in-process search",
                Show(here) == Show(there), $"#{i} {encounter.Id.Entry} {budget}: here {Show(here)}; pooled {Show(there)}");
            var errors = workerPool.TakeGameErrors();
            check("no game errors in workers", errors.Count == 0, $"#{i}: {errors.FirstOrDefault()}");
        }
        check("no worker restarts", workerPool.Restarts == 0, $"{workerPool.Restarts} restarts");

        static string Show(CombatDecisionResult r) =>
            $"{r.First} [{string.Join(" ", r.Plan)}] " + string.Join(" ", r.Candidates.Select(c => $"{c.Action}={c.Value:R}")) +
            (r.PlanHashes == null ? "" : $" agreed {string.Join(",", r.PlanHashes)}");
    }

    /// <summary>
    /// Builds <paramref name="obs"/>, plays a few actions, and rebuilds that point by replay. The built combat must
    /// show the observation exactly; the replay must give the same visible and hidden state.
    /// </summary>
    private static void BuildFrom(Pump pump, CombatObservation obs, string seed, int k, string at, Action<string, bool, string> check)
    {
        CombatSnapshot snapshot;
        string json, hidden;
        using (var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, seed), new RandomChoices(new Random(k))))
        {
            check("build shows the observation", built.BuildDifferences.Count == 0, $"{at}: {string.Join("; ", built.BuildDifferences.Take(4))}");
            var policy = new PlayPolicy(new Random(k));
            for (int a = 0; a < 3 && !CombatSession.IsOver; a++) built.Execute(policy.Choose(built.Decision()));
            snapshot = built.Snapshot();
            (json, hidden) = (Json(built), Hidden(built));
        }
        using var restored = CombatSession.Restore(pump, snapshot, new RandomChoices(new Random(0)));
        check("built combat restores", Json(restored) == json && Hidden(restored) == hidden, at);
        restored.PlayOut(new PlayPolicy(new Random(k)));
        check("plays on after build", CombatSession.IsOver || CombatSession.State.RoundNumber > obs.Round, at);
    }

    private static Recording Record(Pump pump, CombatSetup setup, int rngSeed)
    {
        var snapshots = new List<CombatSnapshot>();
        var observations = new List<string>();
        var hidden = new List<string>();
        var protocolErrors = new List<string>();
        var rng = new Random(rngSeed);
        using var session = CombatSession.Start(pump, setup, new RandomChoices(rng));
        session.PlayOut(new PlayPolicy(rng), s =>
        {
            snapshots.Add(s.Snapshot());
            var json = Json(s);
            observations.Add(json);
            hidden.Add(Hidden(s));
            if (ProtocolJson.Serialize(ProtocolJson.Deserialize<CombatObservation>(json)) != json)
                protocolErrors.Add($"observation at step {snapshots.Count - 1} changes in a round trip");
            foreach (var action in s.LegalActions())
                if (CombatAction.Parse(action.ToString()) != action || ProtocolJson.Deserialize<CombatAction>(ProtocolJson.Serialize(action)) != action)
                    protocolErrors.Add($"action {action} changes in a round trip");
        });
        return new(snapshots, observations, hidden, string.Join(" ", session.Trace), protocolErrors);
    }

    private static string Json(CombatSession session) => ProtocolJson.Serialize(session.Observation());

    private static (string Observation, string DrawOrder) DeterminizeAt(Pump pump, CombatSnapshot snapshot, ulong seed)
    {
        using var session = CombatSession.Restore(pump, snapshot, new RandomChoices(new Random(0)));
        session.Determinize(seed);
        return (Json(session), DrawOrder(session));
    }

    private static string DrawOrder(CombatSession session) =>
        string.Join("\n", session.Player.PlayerCombatState!.DrawPile.Cards.Select(Observe.CardKey));

    /// <summary>State the observation leaves out: draw order, every RNG stream's position, each monster's queued move.</summary>
    private static string Hidden(CombatSession session)
    {
        var lines = new List<string> { DrawOrder(session) };
        lines.AddRange(Enum.GetValues<RunRngType>().Select(t => $"{t} {Show(session.Run.Rng.GetRng(t))}"));
        lines.AddRange(Enum.GetValues<PlayerRngType>().Select(t => $"{t} {Show(session.Player.PlayerRng.GetRng(t))}"));
        lines.AddRange(CombatSession.State.Enemies.Select(e => $"{e.Monster?.Id.Entry} {e.Monster?.NextMove.Id} {(e.Monster == null ? "" : Show(e.Monster.Rng))}"));
        return string.Join("\n", lines);

        static string Show(Rng rng)
        {
            var r = rng.ToSerializable();
            return $"{r.counter}:{r.state0:x}:{r.state1:x}:{r.state2:x}:{r.state3:x}";
        }
    }

    /// <summary>FastHookListeners yields the same models, in the same order, as the game's own iterators.</summary>
    private static bool SameListeners(CombatSession session)
    {
        var (combat, run) = (CombatSession.State, session.Run);
        // The reverse patches must reach the game's compiler-generated iterators, or this compares ours with ours.
        if (FastHookListeners.Original.Combat(combat).GetType().DeclaringType != typeof(CombatState)) return false;
        if (FastHookListeners.Original.Run(run, null).GetType().DeclaringType != typeof(RunState)) return false;
        return FastHookListeners.Original.Combat(combat).SequenceEqual(combat.IterateHookListeners(), ReferenceEqualityComparer.Instance)
            && FastHookListeners.Original.Run(run, combat).SequenceEqual(run.IterateHookListeners(combat), ReferenceEqualityComparer.Instance)
            && FastHookListeners.Original.Run(run, null).SequenceEqual(run.IterateHookListeners(null), ReferenceEqualityComparer.Instance);
    }

    private static int DistinctCards(string drawOrder) => drawOrder.Split('\n').Distinct().Count();

    /// <summary>
    /// A player built at low HP still plays after a start-of-combat effect that would kill them: Royal Poison deals 4
    /// at the start of the first turn, which every build starts with.
    /// </summary>
    private static void BuildsAtLowHp(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var setup = new CombatSetup("SILENT", pool[0], act, GameSeeds.MakeSeed(0, 0));
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        obs = obs with { Player = obs.Player with { Hp = 3 }, Relics = [.. obs.Relics, new RelicView { Id = "ROYAL_POISON" }] };
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        check("build survives a deadly start", built.BuildDifferences.Count == 0 && built.Player.IsActiveForHooks,
            $"{pool[0].Id.Entry} at 3 HP with Royal Poison: hooks {(built.Player.IsActiveForHooks ? "on" : "off")}; {string.Join("; ", built.BuildDifferences.Take(4))}");
    }

    /// <summary>
    /// The Waterfall Giant about to erupt shows its eruption as a death blow, a number the monster keeps that grows all
    /// fight and can pass 100: the build must find it.
    /// </summary>
    private static void BuildsAGrownEruption(Pump pump, Action<string, bool, string> check)
    {
        var (encounter, act) = Opt.Encounter("WATERFALL_GIANT_BOSS");
        var setup = new CombatSetup("SILENT", encounter, act, GameSeeds.MakeSeed(0, 0));
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        obs = obs with { Enemies = [obs.Enemies[0] with { Intents = [new IntentView { Type = "DeathBlow", Damage = 150, Hits = 1 }] }, .. obs.Enemies.Skip(1)] };
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        check("build finds an eruption past 100", built.BuildDifferences.Count == 0, string.Join("; ", built.BuildDifferences.Take(4)));
    }

    /// <summary>
    /// A Defect combat's observation edited to show numbers that only a model's own state holds: a Glass orb that has
    /// fired once (3 passive, 6 evoke, against a fresh one's 4 and 8), a Silver Crucible used up (no counter after its
    /// third card reward, and a treasure room since), a Pael's Tooth keeping two cards (its counter is their number),
    /// Sloth after one card of its 3 (the icon shows the 1), Nightmare on
    /// the first card in hand, and a Hardened Shell of 20 on the first enemy after 6 damage this turn (it shows 14). The
    /// built combat must show them, still let cards be played, and add Nightmare's copies next turn.
    /// </summary>
    private static void BuildsInternalNumbers(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var setup = new CombatSetup("DEFECT", pool[0], act, GameSeeds.MakeSeed(0, 0));
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        var held = obs.Hand[0] with { Uid = null };
        obs = obs with
        {
            Player = obs.Player with
            {
                Orbs = [new OrbView("GLASS_ORB", 3, 6), .. obs.Player.Orbs.Take(obs.Player.OrbSlots - 1)],
                Powers = [.. obs.Player.Powers, new PowerView("SLOTH_POWER", 1, 3), new PowerView("NIGHTMARE_POWER", 3, Card: held)],
            },
            Relics =
            [
                .. obs.Relics, new RelicView { Id = "SILVER_CRUCIBLE", Status = "Disabled", UsedUp = true },
                new RelicView { Id = "PAELS_TOOTH", Counter = 2, Cards = [obs.Hand[0].Id, obs.Hand[^1].Id] },
            ],
            Enemies = [obs.Enemies[0] with { Powers = [.. obs.Enemies[0].Powers, new PowerView("HARDENED_SHELL_POWER", 14, 20)] }, .. obs.Enemies.Skip(1)],
        };
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        check("build solves internal numbers", built.BuildDifferences.Count == 0, $"{pool[0].Id.Entry}: {string.Join("; ", built.BuildDifferences.Take(4))}");
        check("build keeps a power's stacks", built.LegalActions().Any(a => a.Kind == CombatActionKind.PlayCard), "Sloth after 1 card of 3 allows no card");
        built.Execute(built.LegalActions().First(a => a.Kind == CombatActionKind.EndTurn));
        var hand = built.Observation().Hand;
        check("build keeps a power's card", hand.Count(c => c.Id == held.Id) >= 3, $"Nightmare of {held.Id} left a hand of {string.Join(",", hand.Select(c => c.Id))}");
    }

    /// <summary>
    /// With Toasty Mittens, which asks for a card to exhaust at the start of each turn, a world plays Survivor (which
    /// asks for a card to discard), then is rebuilt from its snapshot: the start's own prompt must not take Survivor's
    /// recorded pick. Over several search seeds, since a wrong pick can match by chance.
    /// </summary>
    private static void WorldRestoresAfterPrompts(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var deck = new[] { "SURVIVOR", "SURVIVOR", "SURVIVOR", "SURVIVOR", "SURVIVOR", "STRIKE_SILENT", "DEFEND_SILENT", "NEUTRALIZE" };
        var loadout = new Loadout(Deck: [.. deck.Select(id => new CardSpec(id))], Relics: ["TOASTY_MITTENS"], Hp: 999, MaxHp: 999);
        var setup = new CombatSetup("SILENT", pool[0], act, GameSeeds.MakeSeed(0, 0), Loadout: loadout);
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        using var worlds = SimWorlds.NoSL(pump, obs, setup.Seed, Alone(obs));
        foreach (var world in worlds.Worlds(6, 1))
        {
            var root = world.Snapshot();
            uint survivor = world.Observe().Hand.First(c => c.Id == "SURVIVOR").Uid!.Value;
            world.Execute(world.Legal().First(a => a.Card == survivor));
            string after = ProtocolJson.Serialize(world.Observe());
            var played = world.Snapshot();
            world.Restore(root);
            world.Observe();
            world.Restore(played);
            check("world restores after prompts", ProtocolJson.Serialize(world.Observe()) == after, $"{pool[0].Id.Entry}: {string.Join(" ", played.Steps)}");
        }
    }

    /// <summary>
    /// On the real game, with --combat-net's leaf and the champion's nodes, against 535 HP: Monologue and two Flash of Steel,
    /// which draw each other. A plain search does not get past the first chain, in either world. Then with a prompt in front
    /// of the cycle: Particle Wall costs stars this character has none of until Touch of Insanity makes it free, and the
    /// prompt policy would give that to Haze.
    /// </summary>
    private static void CyclesKillInOneTurn(Pump pump, List<EncounterModel> pool, int act, ICombatLeaf leaf, Action<string, bool, string> check)
    {
        void Case(string name, string[] deck, string[] potions)
        {
            var loadout = new Loadout(Deck: [.. deck.Select(id => new CardSpec(id))], Relics: [], Potions: potions, Hp: 70, MaxHp: 70);
            var setup = new CombatSetup("SILENT", pool[0], act, GameSeeds.MakeSeed(0, 0), Loadout: loadout);
            CombatObservation obs;
            using (var session = CombatSession.Start(pump, setup, PromptBrain.Instance))
            {
                var seen = session.Observation();
                obs = seen with { Enemies = [seen.Enemies[0] with { Hp = 535, MaxHp = 535 }] };
            }
            CombatSnapshot root;
            IReadOnlyList<CombatAction> legal;
            using (var session = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), PromptBrain.Instance))
            {
                root = session.Snapshot();
                legal = session.LegalActions();
            }
            using (var worlds = SimWorlds.SL(pump, root, Alone(obs)))
            {
                var world = worlds.Worlds(1, 0)[0];
                var plan = new SlPlanner(leaf).Plan(world, new Budget(NodesPerWorld: 300, Beam: 8));
                check($"SL planner repeats a cycle to lethal ({name})", plan.End == CombatStatus.Won && plan.Result.First != CombatAction.EndTurn && plan.Result.Plan.Count(a => a == CombatAction.EndTurn) <= 1, $"{plan.End}: {plan.Result.Plan.Count + 1} actions");
                world.Restore(new([]));
                foreach (var step in plan.Result.Plan.Prepend(plan.Result.First)) world.Execute(step);
                check($"SL world ends a cycle as planned ({name})", world.Status == plan.End, $"{world.Status}, planned {plan.End}");
            }
            using (var worlds = SimWorlds.NoSL(pump, obs, setup.Seed, Alone(obs)))
            {
                var pimc = new PimcBrain(leaf).Decide(obs, legal, worlds, new Budget(Worlds: 2, NodesPerWorld: 300), 1);
                check($"PIMC finds a cycle ({name})", pimc.Repeats, $"{pimc.First} [{string.Join(" ", pimc.Plan.Take(8))}...]");
            }
        }
        Case("draws", ["MONOLOGUE", "FLASH_OF_STEEL", "FLASH_OF_STEEL"], []);
        Case("prompt", ["MONOLOGUE", "PARTICLE_WALL", "NEUTRALIZE", "HAZE", "HAZE"], ["TOUCH_OF_INSANITY"]);
    }
}
