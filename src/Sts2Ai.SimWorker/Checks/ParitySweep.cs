using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

/// <summary>The game's ids by kind, as the lockstep check counts them: every kind of thing a combat is made of.</summary>
internal static class GameIds
{
    public static readonly string[] Kinds = ["cards", "powers", "relics", "potions", "monsters", "encounters", "orbs", "enchantments", "afflictions"];

    /// <summary>Every id of this game build, by kind.</summary>
    public static Dictionary<string, List<string>> All() => new()
    {
        ["cards"] = Entries<CardModel>(),
        ["powers"] = Entries<PowerModel>(),
        ["relics"] = Entries<RelicModel>(),
        ["potions"] = Entries<PotionModel>(),
        ["monsters"] = Entries<MonsterModel>(),
        ["encounters"] = Entries<EncounterModel>(),
        ["orbs"] = Entries<OrbModel>(),
        ["enchantments"] = Entries<EnchantmentModel>(),
        ["afflictions"] = Entries<AfflictionModel>(),
    };

    private static List<string> Entries<T>() where T : AbstractModel =>
        [.. ModelDb.All.OfType<T>().Where(Real).Select(m => m.Id.Entry).Distinct().Where(id => !Unreached.ContainsKey(id)).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The ids that no combat of one player's standard run meets, each with why, read from the game's own code
    /// (v0.113.0). <see cref="All"/> leaves them out, so that "never met" names only what a sweep should have met; one
    /// of them that is met after all is reported (<see cref="ParitySummary"/>): the list is then wrong for the game.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Unreached = new Dictionary<string, string>
    {
        // Powers of cards that target an ally: the cards are multiplayer only, and one player has no ally to play them on.
        ["CONCOCT_POWER"] = "only the card Concoct applies it, to an ally (multiplayer only)",
        ["COORDINATE_POWER"] = "only the card Coordinate applies it, to an ally (multiplayer only)",
        ["COVERED_POWER"] = "only the card Intercept applies it, to an ally (multiplayer only)",
        ["INTERCEPT_POWER"] = "only Covered power applies it, which only the card Intercept gives (multiplayer only)",
        ["FADE_POWER"] = "only the card Fade applies it, to an ally (multiplayer only)",
        ["IMITATION_LEARNING_POWER"] = "only the card Imitation Learning applies it, played on an ally (multiplayer only)",
        ["SOULBOUND_POWER"] = "only the card Soulbound applies it, to an ally (multiplayer only)",
        ["GUARDED_POWER"] = "only Tank power applies it, to its owner's teammates",
        // Powers that nothing in the game's code applies.
        ["GRAVITY_POWER"] = "nothing applies it",
        ["LEADERSHIP_POWER"] = "nothing applies it",
        ["MAGIC_BOMB_POWER"] = "nothing applies it",
        ["NO_ENERGY_GAIN_POWER"] = "nothing applies it",
        // The bosses' dominions of the Dread game mode.
        ["AEONGLASS_DOMINION_POWER"] = "Dread mode only; the simulator plays standard runs (the Aeonglass's dominion applies it)",
        ["INSATIABLE_DOMINION_POWER"] = "Dread mode only; the simulator plays standard runs (the Insatiable's dominion applies it)",
        ["LAGAVULIN_MATRIARCH_DOMINION_POWER"] = "Dread mode only; the simulator plays standard runs (the Lagavulin Matriarch's dominion applies it)",
        ["EXPLOSIVE_POWER"] = "Dread mode only; the simulator plays standard runs (only the Waterfall Giant's dominion applies it)",
        ["YOU_ARE_MINE_POWER"] = "Dread mode only; the simulator plays standard runs (only the Queen's dominion applies it)",
        ["AMNESIA_POWER"] = "Dread mode only; the simulator plays standard runs (only choosing the card Amnesia applies it, which only the Knowledge Demon's dominion offers)",
        // The game's own test models outside its Mocks namespaces, which no encounter uses, and its placeholders for removed content.
        ["BIG_DUMMY"] = "a test monster: only mock encounters use it",
        ["ONE_HP_MONSTER"] = "a test monster: no encounter uses it",
        ["TEN_HP_MONSTER"] = "a test monster: no encounter uses it",
        ["SINGLE_ATTACK_MOVE_MONSTER"] = "a test monster: no encounter uses it",
        ["MULTI_ATTACK_MOVE_MONSTER"] = "a test monster: no encounter uses it",
        ["THE_ADVERSARY_MK_ONE"] = "no encounter uses it",
        ["THE_ADVERSARY_MK_TWO"] = "no encounter uses it",
        ["THE_ADVERSARY_MK_THREE"] = "no encounter uses it",
        ["DEPRECATED_MONSTER"] = "a placeholder for a monster a save names and the game no longer has",
        ["DEPRECATED_CARD"] = "a placeholder for a card a save names and the game no longer has",
        ["DEPRECATED_ENCHANTMENT"] = "a placeholder for an enchantment a save names and the game no longer has",
        ["DEPRECATED_ENCOUNTER"] = "a placeholder with no monsters: over before a decision",
    };

    /// <summary>Not one of the game's own test models, which sit in namespaces of their own (Models.Cards.Mocks).</summary>
    public static bool Real(AbstractModel model) => model.GetType().Namespace?.Contains(".Mock") != true;

    /// <summary>
    /// The ids in the combat as it stands, by kind: the cards of every pile and of a waiting prompt with their
    /// enchantments and afflictions, every creature's powers (the ones no icon shows too) and the cards they keep, the
    /// relics, the potions held, the monsters, the encounter, the orbs.
    /// </summary>
    public static Dictionary<string, HashSet<string>> Present(CombatState st, Player player, CardChoice? prompt)
    {
        var ids = Kinds.ToDictionary(k => k, _ => new HashSet<string>());
        var pcs = player.PlayerCombatState ?? throw new InvalidOperationException("player has no combat state");
        void Card(CardModel card)
        {
            ids["cards"].Add(card.Id.Entry);
            if (card.Enchantment is { } enchantment) ids["enchantments"].Add(enchantment.Id.Entry);
            if (card.Affliction is { } affliction) ids["afflictions"].Add(affliction.Id.Entry);
        }
        foreach (var card in pcs.AllCards) Card(card);
        foreach (var option in prompt?.Options ?? [])
        {
            ids["cards"].Add(option.Id);
            if (option.Enchant is { } enchantment) ids["enchantments"].Add(enchantment.Id);
            if (option.Affliction is { } affliction) ids["afflictions"].Add(affliction.Id);
        }
        foreach (var creature in st.Creatures)
        {
            if (creature.Monster is { } monster) ids["monsters"].Add(monster.Id.Entry);
            foreach (var power in creature.Powers)
            {
                ids["powers"].Add(power.Id.Entry);
                if (HeldCards.Get(power) is { } held) Card(held);
            }
        }
        foreach (var relic in player.Relics) ids["relics"].Add(relic.Id.Entry);
        foreach (var potion in player.PotionSlots)
            if (potion != null) ids["potions"].Add(potion.Id.Entry);
        if (st.Encounter is { } encounter) ids["encounters"].Add(encounter.Id.Entry);
        foreach (var orb in pcs.OrbQueue.Orbs) ids["orbs"].Add(orb.Id.Entry);
        return ids;
    }
}

/// <summary>One combat of a sweep.</summary>
/// <param name="Index">Its place in the sweep, which names it: the same options and index give the same combat.</param>
/// <param name="Deck">"starting", "random" or "given".</param>
/// <param name="PolicySeed">Seeds the player (<see cref="Lockstep.Check"/>).</param>
internal sealed record SweepCombat(int Index, CombatSetup Setup, string Deck, int PolicySeed)
{
    public SweptCombat Info => new(Index, Setup.Character, Setup.Ascension, Setup.Encounter.Id.Entry, Setup.ActIndex, Setup.Seed, Deck,
        Setup.Loadout is { } loadout ? RandomLoadout.Text(loadout) : null, PolicySeed);
}

/// <summary>What a checked combat is, as its line and its dump say it: a combat of a sweep, or one of a whole run.</summary>
/// <param name="Index">The combat's place in its sweep; for a run's combat, the run's.</param>
/// <param name="Seed">The game's seed: the combat's own, or the run's.</param>
/// <param name="Deck">"starting", "random" or "given"; "run" for a combat of a whole run.</param>
/// <param name="Loadout">The loadout the combat, or the run, was started with instead of the character's own.</param>
/// <param name="Floor">For a combat of a whole run: the floor it is on.</param>
internal sealed record SweptCombat(int Index, string Character, int Ascension, string Encounter, int Act, string Seed, string Deck, string? Loadout, int PolicySeed,
    int? Floor = null);

/// <summary>One whole run of a sweep of runs.</summary>
internal sealed record SweepRun(int Index, RunSetup Setup, int PolicySeed);

/// <summary>
/// The runs of a lockstep sweep over whole runs: <paramref name="runs"/> for every character and ascension given, each
/// started as the game starts a new run, or with <paramref name="given"/> in place of the character's own loadout.
/// Consecutive runs go through the characters, then the ascensions.
/// </summary>
internal sealed class RunSweep(IReadOnlyList<CharacterModel> characters, IReadOnlyList<int> ascensions, int runs, int seed, Loadout? given)
{
    public int Count => characters.Count * ascensions.Count * runs;

    public SweepRun this[int i] => new(i,
        new RunSetup(characters[i % characters.Count].Id.Entry, GameSeeds.MakeSeed(seed, i), ascensions[i / characters.Count % ascensions.Count], given),
        unchecked(seed * 1_000_003 + i));
}

/// <summary>
/// The combats of a lockstep sweep: every character, ascension and encounter given, each with <paramref name="decks"/>
/// loadouts. The first loadout is the character's starting one; the others are drawn (<see cref="RandomLoadout"/>). With
/// <paramref name="given"/>, every loadout is that one. Consecutive combats go through the characters, then the
/// ascensions, then the encounters, then the loadouts, so a part of the sweep is spread over all of them.
/// </summary>
internal sealed class Sweep(IReadOnlyList<CharacterModel> characters, IReadOnlyList<int> ascensions, IReadOnlyList<(EncounterModel Encounter, int Act)> encounters,
    int decks, int seed, Loadout? given)
{
    public int Count => characters.Count * ascensions.Count * encounters.Count * decks;

    public SweepCombat this[int i]
    {
        get
        {
            var character = characters[i % characters.Count];
            int ascension = ascensions[i / characters.Count % ascensions.Count];
            var (encounter, act) = encounters[i / (characters.Count * ascensions.Count) % encounters.Count];
            int deck = i / (characters.Count * ascensions.Count * encounters.Count);
            var (loadout, kind) = given != null ? (given, "given")
                : deck == 0 ? (null, "starting")
                : (RandomLoadout.Draw(character, new Random(unchecked(seed * 7_919 + i))), "random");
            return new(i, new CombatSetup(character.Id.Entry, encounter, act, GameSeeds.MakeSeed(seed, i), ascension, loadout), kind, unchecked(seed * 1_000_003 + i));
        }
    }

    /// <summary>Every encounter of the game, each with its act: the ones the game lists (the acts', then the events' it
    /// names), then the ones it has besides.</summary>
    public static List<(EncounterModel Encounter, int Act)> AllEncounters() =>
        [.. ModelDb.AllEncounters.Concat(ModelDb.All.OfType<EncounterModel>().OrderBy(e => e.Id.Entry, StringComparer.Ordinal)).Distinct().Where(GameIds.Real)
            .Select(e => (e, Enumerable.Range(0, ModelDb.ActsByIndex.Count).FirstOrDefault(i => ModelDb.ActsByIndex[i].Any(a => a.AllEncounters.Contains(e)))))];
}

/// <summary>
/// A loadout drawn for a sweep, to meet what a run can bring into a combat: a deck of 10 to 30 cards, four in five from
/// the character's pool and the rest from the pools every character shares (colorless, curses, statuses, events, quests,
/// tokens), some upgraded and some enchanted with an enchantment the card takes; the starting relics and up to six more
/// from the character's and the shared pools; up to three potions; and half the time less than full HP.
/// </summary>
internal static class RandomLoadout
{
    public static Loadout Draw(CharacterModel character, Random rng)
    {
        var own = character.CardPool.AllCards.Where(InDecks).ToList();
        var shared = ModelDb.AllSharedCardPools.SelectMany(p => p.AllCards).Where(InDecks).ToList();
        var enchantments = ModelDb.DebugEnchantments.Where(OnDeckCards).OrderBy(e => e.Id.Entry, StringComparer.Ordinal).ToList();
        var deck = new List<CardSpec>();
        int size = rng.Next(10, 31);
        for (int i = 0; i < size; i++)
        {
            var card = rng.Next(5) == 0 ? shared[rng.Next(shared.Count)] : own[rng.Next(own.Count)];
            int upgrades = card.MaxUpgradeLevel > 0 && rng.Next(3) == 0 ? rng.Next(1, Math.Min(card.MaxUpgradeLevel, 3) + 1) : 0;
            var fits = rng.Next(6) == 0 ? enchantments.Where(e => e.CanEnchant(card)).ToList() : [];
            var enchantment = fits.Count > 0 ? fits[rng.Next(fits.Count)] : null;
            deck.Add(new(card.Id.Entry, upgrades, enchantment?.Id.Entry, enchantment == null ? 0 : enchantment.ShowAmount ? rng.Next(1, 4) : 1,
                card is MadScience ? Tinkered(rng) : null));
        }

        var relicPool = character.RelicPool.AllRelics.Concat(ModelDb.AllRelicPools.Except(ModelDb.CharacterRelicPools).SelectMany(p => p.AllRelics))
            .Where(GameIds.Real).Select(r => r.Id.Entry).Distinct().ToList();
        var relics = character.StartingRelics.Select(r => r.Id.Entry).ToList();
        for (int i = rng.Next(7); i > 0; i--)
            if (relicPool[rng.Next(relicPool.Count)] is var relic && !relics.Contains(relic)) relics.Add(relic);

        var potionPool = character.PotionPool.AllPotions.Concat(ModelDb.AllPotionPools.Except(ModelDb.AllCharacterPotionPools).SelectMany(p => p.AllPotions))
            .Where(GameIds.Real).Select(p => p.Id.Entry).Distinct().ToList();
        var potions = Enumerable.Range(0, rng.Next(3)).Select(_ => potionPool[rng.Next(potionPool.Count)]).ToList();

        int maxHp = character.StartingHp;
        return new(deck, relics, potions, rng.Next(2) == 0 ? maxHp : rng.Next(maxHp / 4, maxHp + 1), maxHp);
    }

    /// <summary>Whether a deck can hold the card.</summary>
    private static bool InDecks(CardModel card) => GameIds.Real(card) && card is not DeprecatedCard;

    /// <summary>What the Tinker Time event gives the Mad Science it makes, and the game cannot play one without: one of
    /// its three types, and one of that type's three riders.</summary>
    private static string Tinkered(Random rng)
    {
        (CardType Type, TinkerTime.RiderEffect[] Riders)[] kinds =
        [
            (CardType.Attack, [TinkerTime.RiderEffect.Sapping, TinkerTime.RiderEffect.Violence, TinkerTime.RiderEffect.Choking]),
            (CardType.Skill, [TinkerTime.RiderEffect.Energized, TinkerTime.RiderEffect.Wisdom, TinkerTime.RiderEffect.Chaos]),
            (CardType.Power, [TinkerTime.RiderEffect.Expertise, TinkerTime.RiderEffect.Curious, TinkerTime.RiderEffect.Improvement]),
        ];
        var (type, riders) = kinds[rng.Next(kinds.Length)];
        return $"{nameof(MadScience.TinkerTimeType)}={type};{nameof(MadScience.TinkerTimeRider)}={riders[rng.Next(riders.Length)]}";
    }

    /// <summary>Whether the game puts the enchantment on cards of the deck. Inky is put on a card in combat, by Blade of
    /// Ink, and the game cannot play it on a card with no target.</summary>
    private static bool OnDeckCards(EnchantmentModel enchantment) => GameIds.Real(enchantment) && enchantment is not (Inky or DeprecatedEnchantment);

    /// <summary>The loadout as the command line takes it (--deck, --relics, --potions, --hp, --max-hp), to play it again.</summary>
    public static string Text(Loadout loadout) => string.Join(" ", new[]
    {
        loadout.Deck is { } deck ? "--deck " + string.Join(",", deck.Select(c => c.Text)) : null,
        loadout.Relics is { } relics ? "--relics " + (relics.Count == 0 ? "\"\"" : string.Join(",", relics)) : null,
        loadout.Potions is { } potions ? "--potions " + (potions.Count == 0 ? "\"\"" : string.Join(",", potions)) : null,
        loadout.Hp is { } hp ? $"--hp {hp}" : null,
        loadout.MaxHp is { } maxHp ? $"--max-hp {maxHp}" : null,
    }.Where(part => part != null));
}
