using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

internal static partial class SelfTest
{
    /// <summary>
    /// Plays Rolling Boulder against the first multi-enemy encounter in the pool. The power fires at the start of
    /// the next turn and must hit each enemy separately.
    /// </summary>
    private static void RollingBoulder(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var loadout = new Loadout(Deck: Enumerable.Repeat(new CardSpec("ROLLING_BOULDER"), 10).ToList(), Hp: 999, MaxHp: 999);
        foreach (var encounter in pool)
        {
            var setup = new CombatSetup("IRONCLAD", encounter, act, GameSeeds.MakeSeed(0, 0), Loadout: loadout);
            using var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0)));
            if (session.State.HittableEnemies.Count < 2) continue;

            int before = HeadlessPatches.RollingBoulderSplits;
            var policy = new PlayPolicy(new Random(0));
            while (!session.IsOver && session.State.RoundNumber <= 3)
                session.Execute(policy.Choose(session.Decision()));
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
        built.Execute(built.LegalActions().First(a => a.Kind == CombatActionKind.EndTurn));
        var errors = HeadlessGame.TakeErrors();
        check("thorns kill during a sly play", built.Player.Creature.IsDead && HeadlessPatches.PopCardAfterEarlyReturn.Pops > before && errors.Count == 0,
            $"{pool[0].Id.Entry}: dead {built.Player.Creature.IsDead}, pops {HeadlessPatches.PopCardAfterEarlyReturn.Pops - before}, " +
            $"build {string.Join("; ", built.BuildDifferences.Take(2))}, errors {errors.FirstOrDefault()}");
    }
}
