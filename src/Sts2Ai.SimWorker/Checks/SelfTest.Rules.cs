using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

internal static partial class SelfTest
{
    /// <summary>
    /// Plays Rolling Boulder against the first multi-enemy encounter in the pool. The power fires at the start of
    /// the next turn and must hit each enemy separately.
    /// </summary>
    private static void RollingBoulder(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var loadout = new Loadout(Deck: [.. Enumerable.Repeat(new CardSpec("ROLLING_BOULDER"), 10)], Hp: 999, MaxHp: 999);
        foreach (var encounter in pool)
        {
            var setup = new CombatSetup("IRONCLAD", encounter, act, GameSeeds.MakeSeed(0, 0), Loadout: loadout);
            using var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0)));
            if (CombatSession.State.HittableEnemies.Count < 2) continue;

            int before = HeadlessPatches.RollingBoulderSplits;
            var policy = new PlayPolicy(new Random(0));
            while (!CombatSession.IsOver && CombatSession.State.RoundNumber <= 3)
                session.Step(policy);
            var errors = HeadlessGame.TakeErrors();
            check("rolling boulder hits one by one", HeadlessPatches.RollingBoulderSplits > before && errors.Count == 0,
                $"{encounter.Id.Entry}: splits {HeadlessPatches.RollingBoulderSplits - before}, errors {errors.FirstOrDefault()}");
            return;
        }
        check("rolling boulder hits one by one", false, "no encounter in the pool has two or more enemies");
    }

    /// <summary>
    /// Promotion-1 seed 68's game error: at 2 HP behind a block the enemies' turn cannot pass, with Tools of the Trade
    /// and only a Ricochet in the deck, against Thorns on every enemy. The next turn's block is gone, Tools of the
    /// Trade discards the Ricochet, Sly plays it, and Thorns kill the player in the middle of the play: the combat
    /// must end lost with no game error, through <see cref="HeadlessPatches.PopCardAfterEarlyReturn"/>.
    /// </summary>
    private static void ThornsKillDuringSlyPlay(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var loadout = new Loadout(Deck: [new CardSpec("RICOCHET")]);
        var setup = new CombatSetup("SILENT", pool[0], act, GameSeeds.MakeSeed(0, 0), Loadout: loadout);
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        obs = obs with
        {
            Player = obs.Player with { Hp = 2, Block = 999, Powers = [.. obs.Player.Powers, new PowerView("TOOLS_OF_THE_TRADE_POWER", 1)] },
            Enemies = [.. obs.Enemies.Select(e => e with { Powers = [.. e.Powers, new PowerView("THORNS_POWER", 5)] })],
        };
        int before = HeadlessPatches.PopCardAfterEarlyReturn.Pops;
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        built.EndTurn();
        var errors = HeadlessGame.TakeErrors();
        check("thorns kill during a sly play", built.Player.Creature.IsDead && HeadlessPatches.PopCardAfterEarlyReturn.Pops > before && errors.Count == 0,
            $"{pool[0].Id.Entry}: dead {built.Player.Creature.IsDead}, pops {HeadlessPatches.PopCardAfterEarlyReturn.Pops - before}, " +
            $"build {string.Join("; ", built.BuildDifferences.Take(2))}, errors {errors.FirstOrDefault()}");
    }

    /// <summary>
    /// Seed 1151522's game error (v9g2-2, DEFECT): Entropy 2 and Smokestack, a hand of Dazed at the turn's start. The
    /// first Dazed transformed becomes another status card, Smokestack kills every enemy on it, and the second
    /// transform is asked in an ending combat: the combat must end won with no game error, through
    /// <see cref="HeadlessPatches.NoRandomTransformInAnEndingCombat"/>.
    /// </summary>
    private static void TransformKillsBeforeTheNextTransform(Pump pump, Action<string, bool, string> check)
    {
        var (encounter, act) = Opt.Encounter("SOUL_NEXUS_ELITE");
        var loadout = new Loadout(Deck: [.. Enumerable.Repeat(new CardSpec("DAZED"), 10)]);
        var setup = new CombatSetup("DEFECT", encounter, act, GameSeeds.MakeSeed(0, 0), Loadout: loadout);
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, FirstPicks.Instance))
            obs = session.Observation();
        obs = obs with
        {
            Player = obs.Player with
            {
                Block = 999,
                Powers = [.. obs.Player.Powers, new PowerView("ENTROPY_POWER", 2), new PowerView("SMOKESTACK_POWER", 999)],
            },
        };
        int before = HeadlessPatches.NoRandomTransformInAnEndingCombat.Skipped;
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), FirstPicks.Instance);
        built.Execute(CombatAction.EndTurn);
        bool asked = built.Prompt != null;
        if (asked) built.Execute(CombatAction.Pick([0, 1]));
        int skipped = HeadlessPatches.NoRandomTransformInAnEndingCombat.Skipped - before;
        var errors = HeadlessGame.TakeErrors();
        check("a transform that ends the combat leaves the next transform out", asked && built.Outcome == CombatOutcome.Win && skipped == 1 && errors.Count == 0,
            $"asked {asked}, outcome {built.Outcome}, transforms left out {skipped}, build {string.Join("; ", built.BuildDifferences.Take(2))}, " +
            $"errors {errors.FirstOrDefault()}");
    }

    /// <summary>
    /// The Knowledge Demon's first move asks the player for a card, in the enemies' turn: the combat's turns wait on the
    /// prompt. A session dropped there must end them, since the game starts a combat's turns only when the turns of the
    /// combat before it have ended (<see cref="PromptSelector.Drop"/>): the next combat must start, and play on through
    /// the same prompt.
    /// </summary>
    private static void DropsAtAnEnemysPrompt(Pump pump, Action<string, bool, string> check)
    {
        var (encounter, act) = Opt.Encounter("KNOWLEDGE_DEMON_BOSS");
        var setup = new CombatSetup("SILENT", encounter, act, GameSeeds.MakeSeed(0, 0));
        bool asked;
        using (var session = CombatSession.Start(pump, setup, FirstPicks.Instance))
        {
            session.Execute(CombatAction.EndTurn);
            asked = session.Prompt != null && session.LegalActions().Count == 0;
        }
        using var next = CombatSession.Start(pump, setup, FirstPicks.Instance);
        bool started = next.WaitingForInput && next.LegalActions().Count > 0;
        if (started) next.EndTurn();
        var errors = HeadlessGame.TakeErrors();
        check("a combat dropped at an enemy's prompt lets the next one play", asked && started && CombatSession.State.RoundNumber == 2 && errors.Count == 0,
            $"asked {asked}, the next started {started}, round {CombatSession.State.RoundNumber}, errors {errors.FirstOrDefault()}");
    }

    /// <summary>
    /// Stampede plays an attack of the hand as the turn ends, and a Headbutt played there asks for a card of the discard
    /// pile: the combat's turns wait on that prompt too, though the game asks it as the end-turn action finishes, with
    /// the action executor still on that action. A session dropped there must end them as at an enemy's prompt
    /// (<see cref="PromptSelector.Drop"/>): the next combat must start, and play on.
    /// </summary>
    private static void DropsAtATurnEndsPrompt(Pump pump, Action<string, bool, string> check)
    {
        var (encounter, act) = Opt.Encounter("KAISER_CRAB_BOSS");
        var loadout = new Loadout(Deck: [.. Enumerable.Repeat(new CardSpec("HEADBUTT"), 10)]);
        var setup = new CombatSetup("IRONCLAD", encounter, act, GameSeeds.MakeSeed(0, 0), Loadout: loadout);
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, FirstPicks.Instance))
            obs = session.Observation();
        obs = obs with
        {
            Player = obs.Player with { Powers = [.. obs.Player.Powers, new PowerView("STAMPEDE_POWER", 1)] },
            Draw = [.. obs.Draw.Skip(3)],
            Discard = [.. obs.Draw.Take(3)],
        };
        bool asked;
        using (var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), FirstPicks.Instance))
        {
            built.Execute(CombatAction.EndTurn);
            asked = built.Prompt != null && built.LegalActions().Count == 0;
        }
        using var next = CombatSession.Start(pump, setup, FirstPicks.Instance);
        bool started = next.WaitingForInput && next.LegalActions().Count > 0;
        if (started) next.EndTurn();
        var errors = HeadlessGame.TakeErrors();
        check("a combat dropped at a turn end's prompt lets the next one play", asked && started && CombatSession.State.RoundNumber == 2 && errors.Count == 0,
            $"asked {asked}, the next started {started}, round {CombatSession.State.RoundNumber}, errors {errors.FirstOrDefault()}");
    }

    /// <summary>
    /// The fight round cap (<see cref="FightCap"/>): a combat built at the cap's last round, at 999 HP that no enemy turn
    /// takes, is still on there; ending that turn starts round <see cref="FightCap.MaxRounds"/> + 1, and the fight is lost.
    /// </summary>
    private static void FightCapLoses(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var setup = new CombatSetup("SILENT", pool[0], act, GameSeeds.MakeSeed(0, 0), Loadout: new Loadout(Hp: 999, MaxHp: 999));
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        obs = obs with { Round = FightCap.MaxRounds };
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        bool onAtCap = !CombatSession.IsOver && CombatSession.State.RoundNumber == FightCap.MaxRounds;
        built.EndTurn();
        var errors = HeadlessGame.TakeErrors();
        check("a fight past the round cap is lost", onAtCap && built.Outcome == CombatOutcome.Loss && errors.Count == 0,
            $"{pool[0].Id.Entry}: on at round {FightCap.MaxRounds} {onAtCap}, then {built.Outcome} at round {CombatSession.State.RoundNumber}, " +
            $"hp {built.Player.Creature.CurrentHp}, errors {errors.FirstOrDefault()}");
    }

    /// <summary>
    /// The observation states what a card gathers in a combat, and a combat built from it still reads as built: Murder's
    /// number, the replay of a Spiral, Glam spent once the card is played.
    /// </summary>
    private static void GatheredCardState(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var loadout = new Loadout(Deck: [new CardSpec("MURDER"), new CardSpec("STRIKE_SILENT", Enchant: "SPIRAL", EnchantAmount: 1), new CardSpec("STRIKE_SILENT", Enchant: "GLAM", EnchantAmount: 1)]);
        var setup = new CombatSetup("SILENT", pool[0], act, GameSeeds.MakeSeed(0, 0), Loadout: loadout);
        using var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0)));
        var obs = session.Observation();
        var cards = obs.Hand.Concat(obs.Draw).ToList();
        CombatAction? glam = session.LegalActions().Where(a => a.Kind == CombatActionKind.PlayCard && obs.Hand.Any(c => c.Uid == a.Card && c.Enchant?.Id == "GLAM")).Cast<CombatAction?>().FirstOrDefault();
        if (glam is { } play) session.Execute(play);
        var after = session.Observation();
        var seen = after.Hand.Concat(after.Draw).Concat(after.Discard).ToList();
        check("gathered card state", cards.Any(c => c.Id == "MURDER" && c.Calc != null) && cards.Any(c => c.Enchant?.Id == "SPIRAL" && c.Replay == 1)
            && (glam == null || seen.Any(c => c.Enchant?.Id == "GLAM" && c.Spent == true)), $"{ProtocolJson.Serialize(cards)}; after {ProtocolJson.Serialize(seen)}");
    }

    /// <summary>
    /// A loadout whose relics give potion slots holds more potions than the base three: the slots are what the game
    /// gives for those relics (3 + Potion Belt's 2 + Alchemical Coffer's 4 + Phial Holster's 1), in a combat and at
    /// the start of a run, where ascension 4 (Tight Belt) takes one away, and the relics' pickups do not run again.
    /// </summary>
    private static void LoadoutPotionSlots(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        string[] potions = ["FIRE_POTION", "BLOCK_POTION", "SWIFT_POTION", "FYSH_OIL", "COLORLESS_POTION", "CLARITY", "TOUCH_OF_INSANITY",
            "FIRE_POTION", "BLOCK_POTION"];
        var loadout = new Loadout(Relics: ["RING_OF_THE_SNAKE", "POTION_BELT", "ALCHEMICAL_COFFER", "PHIAL_HOLSTER"], Potions: potions);
        static string Slots(IEnumerable<string?> slots) => string.Join(",", slots.Select(p => p ?? "-"));
        string want = Slots(potions);
        using (var combat = CombatSession.Start(pump, new CombatSetup("SILENT", pool[0], act, GameSeeds.MakeSeed(0, 0), Loadout: loadout),
                   new RandomChoices(new Random(0))))
        {
            var slots = Slots(combat.Observation().Potions);
            check("loadout gets its relics' potion slots", slots == want + ",-", $"combat: slots {slots}");
        }
        using var run = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(0, 0), Ascension: 4, Loadout: loadout), FirstPicks.Instance,
            RunCommand.Unasked.Instance, new RandomMacro(new Random(0)));
        var runSlots = Slots(run.Player.PotionSlots.Select(p => p?.Id.Entry));
        check("loadout gets its relics' potion slots", runSlots == want, $"run at ascension 4: slots {runSlots}");
    }

    /// <summary>
    /// A Foul Potion is thrown out of combat as the shipped game allows (headless patch FoulPotionAtMerchants): at a
    /// shop's merchant for 100 gold, and at the Fake Merchant, whose fight it starts and which is played to its end.
    /// Each room is entered from a run's first decision; the macro policy throws the potion where it is offered.
    /// </summary>
    private static void FoulPotionAtMerchants(Pump pump, Action<string, bool, string> check)
    {
        foreach (bool fake in new[] { false, true })
        {
            var macro = new Thrower();
            using var run = RunSession.Start(pump, new RunSetup("SILENT", GameSeeds.MakeSeed(0, 0), Loadout: new Loadout(Potions: ["FOUL_POTION"])),
                FirstPicks.Instance, new PlayPolicy(new Random(0)), macro);
            var enter = fake ? RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<FakeMerchant>(), showTransition: false)
                : RunManager.Instance.EnterRoomDebug(RoomType.Shop, showTransition: false);
            pump.RunUntil(() => enter.IsCompleted, run.Describe);
            if (enter.IsFaulted) throw enter.Exception!.GetBaseException();
            int gold = run.Player.Gold;
            run.Step();
            run.Settle();
            bool thrown = macro.Offered && run.Player.PotionSlots.All(p => p == null);
            string seen = $"offered {macro.Offered}, belt {string.Join(",", run.Player.PotionSlots.Select(p => p?.Id.Entry ?? "-"))}";
            if (!fake)
            {
                var errors = HeadlessGame.TakeErrors();
                check("Foul Potion thrown at a shop's merchant", thrown && run.Player.Gold == gold + 100 && errors.Count == 0,
                    $"{seen}, gold {gold} -> {run.Player.Gold}, errors {errors.FirstOrDefault()}");
            }
            else
            {
                // The fight is played to its end, so that the event's combat is checked whole.
                bool fight = run.Run.CurrentRoom is CombatRoom { Encounter: FakeMerchantEventEncounter };
                for (int steps = 0; fight && CombatManager.Instance.IsInProgress && !run.IsOver && steps < 2000; steps++) run.Step();
                var errors = HeadlessGame.TakeErrors();
                check("Foul Potion thrown at the Fake Merchant", thrown && fight && !CombatManager.Instance.IsInProgress && errors.Count == 0,
                    $"{seen}, fight {fight}, errors {errors.FirstOrDefault()}");
            }
        }
    }

    /// <summary>Throws the potion in slot 0 where it is offered, and otherwise takes the first legal action.</summary>
    private sealed class Thrower : IMacroPolicy
    {
        public bool Offered { get; private set; }

        public MacroAnswer Choose(MacroDecision decision)
        {
            var potion = MacroAction.Of(MacroAction.Potion, 0);
            if (!decision.Legal.Contains(potion)) return new(decision.Legal[0]);
            if (Offered) throw new InvalidOperationException("the potion offered again after it was thrown");
            Offered = true;
            return new(potion);
        }
    }
}
