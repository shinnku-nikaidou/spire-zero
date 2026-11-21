using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using System.CommandLine;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// scout: predicts a real game from its seed or its run save (the seed-scout skill, and the mod's ai scout): the acts,
/// maps, ? rooms, events, shops, chests and every player's rewards along a route, as the game's own code makes them,
/// without playing the fights. Each room is entered as RunManager.EnterMapPointInternal enters it; rewards come from
/// RewardsCmd.GenerateForRoomEnd and the hook before they are offered, as CombatRoom.OfferRoomEndRewards does. Ids are
/// printed raw; the mod names them in the game's language.
/// </summary>
internal static class ScoutCommand
{
    static readonly JsonSerializerOptions SpecJson = new() { PropertyNameCaseInsensitive = true };

    private static readonly Option<string> SpecFile = Opt.Required<string>("--spec", "the spec, as JSON: {save | seed, ascension, seats}, routes, picks, show, edges");

    public static Command Command()
    {
        var command = Opt.Command("scout", "what a game holds ahead along a route, from its seed or its run save", [SpecFile]);
        command.SetAction(Cli.Game((r, _) => Run(r.GetValue(SpecFile)!)));
        return command;
    }

    static int Run(string path)
    {
        var spec = JsonSerializer.Deserialize<ScoutSpec>(File.ReadAllText(path), SpecJson)
            ?? throw new ArgumentException("empty spec");
        if (spec.Save != null) return FromSave(spec);
        var chars = spec.Seats.Select(n => ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry.Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no character {n}")).ToList();
        var players = chars.Select((c, i) => Player.CreateForNewRun(c, UnlockState.all, (ulong)(i + 1))).ToList();
        // StartRunLobby.BeginRunLocally: the acts are rolled from the seed.
        var actRng = new Rng(StringHelper.GetDeterministicHashCode(spec.Seed), "act_selection");
        var acts = ActModel.GetRandomList(actRng, UnlockState.all, isMultiplayer: players.Count > 1).Select(a => a.ToMutable()).ToList();
        var run = RunState.CreateForNewRun(players, acts, [], GameMode.Standard, isCustom: false, spec.Ascension, spec.Seed);
        RunManager.Instance.SetUpNewSingleplayer(run, shouldSave: false);
        Console.WriteLine($"seed {spec.Seed}, ascension {spec.Ascension}, {players.Count} players: " +
            string.Join(", ", chars.Select((c, i) => $"seat{i} {c.Id.Entry}")));
        new ScoutWalk(run, spec).All();
        RunManager.Instance.CleanUp(graceful: true);
        return 0;
    }
    // A run save (current_run.save or current_run_mp.save) loaded as the game's Continue loads it: the walk goes on
    // from the room the save was about to enter, with every stream, odds and bag as the save left them.
    static int FromSave(ScoutSpec spec)
    {
        var read = SaveManager.FromJson<SerializableRun>(File.ReadAllText(spec.Save!));
        var save = read.SaveData ?? throw new InvalidDataException($"save does not load: {read.Status} {read.ErrorMessage}");
        var run = RunState.FromSerializable(save);
        RunManager.Instance.SetUpSavedSingleplayer(run, save).GetAwaiter().GetResult();
        Console.WriteLine($"save of seed {run.Rng.StringSeed}, ascension {run.AscensionLevel}, {run.Players.Count} players: " +
            string.Join(", ", run.Players.Select((p, i) => $"seat{i} {p.Character.Id.Entry}")));
        // The local player, for the text some options build (energy icons).
        LocalContext.NetId = run.Players[0].NetId;
        new ScoutWalk(run, spec).FromSave(save.PreFinishedRoom == null ? null : AbstractRoom.FromSerializable(save.PreFinishedRoom, run));
        RunManager.Instance.CleanUp(graceful: true);
        return 0;
    }
}

/// <summary>The game to scout: who sits where, the route of each act, and the choices that draw a player's rewards.</summary>
internal sealed record ScoutSpec
{
    /// <summary>A run save to go on from instead of a new run; Seed, Ascension and Seats then come from it.</summary>
    public string? Save { get; init; }
    public string Seed { get; init; } = "";
    public int Ascension { get; init; }
    /// <summary>Characters in seat order (the party list shows the local player first, then the others in seat order).</summary>
    public string[] Seats { get; init; } = [];
    /// <summary>
    /// Per act, the steps after the ancient: "c,r", "left", "right", or "left*" / "right*" to the boss. From a save, the
    /// first list is the save's act, its steps after the room the save was entering.
    /// </summary>
    public string[][] Routes { get; init; } = [];
    public ScoutPick[] Picks { get; init; } = [];
    /// <summary>The seats whose rooms are printed (all by default); every seat is still computed.</summary>
    public int[]? Show { get; init; }
    /// <summary>Prints each act's map links too.</summary>
    public bool Edges { get; init; }
}

/// <summary>
/// What a seat did at a floor (the game's own floor count): Option, the event option it chose when that option draws
/// from its rewards stream; Relic, a relic it got there that changes later rewards or rooms (applied after the room).
/// </summary>
internal sealed record ScoutPick
{
    public int Floor { get; init; }
    public int Seat { get; init; }
    public string? Option { get; init; }
    public string? Relic { get; init; }
}

internal sealed class ScoutWalk(RunState run, ScoutSpec spec)
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    ActMap _map = NullActMap.Instance;
    int _floorBase;

    public void All()
    {
        for (int act = 0; act < spec.Routes.Length; act++) Act(act, spec.Routes[act]);
    }

    void Act(int act, string[] route)
    {
        // RunManager.SetActInternal and GenerateMap, less the nodes and the map hooks.
        run.CurrentActIndex = act;
        run.ClearVisitedMapCoordsDebug();
        run.Odds.UnknownMapPoint.ResetToBase();
        _map = run.Act.CreateMap(run, replaceTreasureWithElites: false);
        if (act == 0 && !run.ExtraFields.StartedWithNeow) _map.StartingMapPoint.PointType = MapPointType.Monster;
        run.Map = _map;
        PrintAct(act);

        var cur = _map.StartingMapPoint;
        Enter(cur);
        Follow(act, cur, route);
    }

    public void FromSave(AbstractRoom? finished)
    {
        // RunManager.GenerateMap (the save's own map when it has one), then LoadIntoLatestMapCoord: the room the save
        // was entering is entered again, its coordinate already visited.
        int act = run.CurrentActIndex;
        _floorBase = run.MapPointHistory.Take(act).Sum(h => h.Count);
        RunManager.Instance.GenerateMap().GetAwaiter().GetResult();
        _map = run.Map;
        PrintAct(act);
        var cur = _map.GetPoint(run.VisitedMapCoords[^1])!;
        // A save made after a fight and before its rewards (the history has the room): only the rewards are left.
        if (finished is CombatRoom { IsPreFinished: true } done)
        {
            Console.WriteLine($"F{_floorBase + cur.coord.row + 1} [act {act + 1} {cur.coord.col},{cur.coord.row}] {cur.PointType} {done.ModelId?.Entry}, fought");
            Rewards(done);
        }
        else Enter(cur, visit: false);
        Follow(act, cur, spec.Routes.FirstOrDefault() ?? []);
        for (int i = 1; i < spec.Routes.Length; i++) Act(act + i, spec.Routes[i]);
    }

    void Follow(int act, MapPoint cur, string[] route)
    {
        foreach (var step in route)
        {
            if (step.EndsWith('*'))
                while (Next(cur, step[..^1]) is { } next) Enter(cur = next);
            else if (step is "left" or "right")
                Enter(cur = Next(cur, step) ?? throw new ArgumentException($"act {act + 1}: no step {step} from {cur.coord.col},{cur.coord.row}"));
            else
            {
                var at = step.Split(',').Select(int.Parse).ToArray();
                var next = cur.Children.FirstOrDefault(p => p.coord.col == at[0] && p.coord.row == at[1])
                    ?? throw new ArgumentException($"act {act + 1}: {step} does not follow {cur.coord.col},{cur.coord.row}");
                Enter(cur = next);
            }
        }
        // A route that stops just short of the boss goes on through it (and the second boss).
        while (cur.Children.Count == 1 && (cur.Children.First() == _map.BossMapPoint || cur.Children.First() == _map.SecondBossMapPoint))
            Enter(cur = cur.Children.First());
        _floorBase += (_map.SecondBossMapPoint ?? _map.BossMapPoint).coord.row + 1;
    }

    static MapPoint? Next(MapPoint from, string side) =>
        from.Children.Count == 0 ? null
        : side == "left" ? from.Children.MinBy(p => p.coord.col)
        : side == "right" ? from.Children.MaxBy(p => p.coord.col)
        : throw new ArgumentException($"no side {side}");

    void PrintAct(int act)
    {
        var a = run.Act;
        var rooms = (RoomSet)typeof(ActModel).GetField("_rooms", Hidden)!.GetValue(a)!;
        Console.WriteLine();
        Console.WriteLine($"act {act + 1} {a.Id.Entry}: boss {a.BossEncounter.Id.Entry}" +
            (a.SecondBossEncounter != null ? $" then {a.SecondBossEncounter.Id.Entry}" : "") + $", ancient {a.Ancient?.Id.Entry}");
        Console.WriteLine($"  monsters in order: {string.Join(", ", rooms.normalEncounters.Select(e => e.Id.Entry))}");
        Console.WriteLine($"  elites in order: {string.Join(", ", rooms.eliteEncounters.Select(e => e.Id.Entry))}");
        Console.WriteLine($"  events in order (the ancient and disallowed ones are skipped): {string.Join(", ", rooms.events.Select(e => e.Id.Entry))}");
        var points = _map.GetAllMapPoints().Where(p => p != _map.StartingMapPoint).ToList();
        int cols = points.Max(p => p.coord.col) + 1;
        Console.WriteLine("  map (floor: columns 0.., A ancient, M monster, E elite, R rest, $ shop, T chest, ? unknown):");
        foreach (var row in points.GroupBy(p => p.coord.row).OrderByDescending(g => g.Key))
        {
            var cells = Enumerable.Range(0, cols).Select(c => row.FirstOrDefault(p => p.coord.col == c) is { } p ? Letter(p.PointType) : '.');
            Console.WriteLine($"  F{_floorBase + row.Key + 1,-3} {string.Join(" ", cells)}");
        }
        Console.WriteLine($"  F{_floorBase + 1,-3} start {_map.StartingMapPoint.coord.col},0 {_map.StartingMapPoint.PointType}");
        if (spec.Edges)
            foreach (var p in points.Prepend(_map.StartingMapPoint).OrderBy(p => p.coord.row).ThenBy(p => p.coord.col))
                Console.WriteLine($"  {p.coord.col},{p.coord.row} {p.PointType} -> {string.Join(" ", p.Children.OrderBy(x => x.coord.col).Select(x => $"{x.coord.col},{x.coord.row}"))}");
    }

    static char Letter(MapPointType t) => t switch
    {
        MapPointType.Monster => 'M',
        MapPointType.Elite => 'E',
        MapPointType.RestSite => 'R',
        MapPointType.Shop => '$',
        MapPointType.Treasure => 'T',
        MapPointType.Unknown => '?',
        MapPointType.Ancient => 'A',
        MapPointType.Boss => 'B',
        _ => 'x',
    };

    static object? Rm(string name, params object?[] args) =>
        typeof(RunManager).GetMethod(name, Hidden)!.Invoke(RunManager.Instance, args);

    bool Shown(int seat) => spec.Show == null || spec.Show.Contains(seat);

    string Seat(Player p) => $"seat{run.GetPlayerSlotIndex(p)} {p.Character.Id.Entry}";

    void Enter(MapPoint point, bool visit = true)
    {
        int floor = _floorBase + point.coord.row + 1;
        // RunManager.EnterMapCoord, then EnterMapPointInternal: the roll, the room, the history, the visit.
        if (visit) run.AddVisitedMapCoord(point.coord);
        run.ActFloor = point.coord.row + 1;
        var blacklist = RunManager.BuildRoomTypeBlacklist(run.CurrentMapPointHistoryEntry, run.CurrentMapPoint?.Children ?? []);
        var roomType = (RoomType)Rm("RollRoomTypeFor", point.PointType, blacklist)!;
        var room = (AbstractRoom)Rm("CreateRoom", roomType, point.PointType, null)!;
        run.AppendToMapPointHistory(point.PointType, room.RoomType, room.ModelId);
        run.Act.MarkRoomVisited(room.RoomType);
        string rolled = point.PointType == MapPointType.Unknown ? $" -> {room.RoomType}" : "";
        Console.WriteLine($"F{floor} [act {run.CurrentActIndex + 1} {point.coord.col},{point.coord.row}] {point.PointType}{rolled} {room.ModelId?.Entry}");

        switch (room)
        {
            case EventRoom er:
                Event(er);
                break;
            case CombatRoom cr:
                // PaelsTooth.AfterCombatEnd, before the rewards: one of the cards it holds comes back, drawn from the
                // rewards stream.
                foreach (var p in run.Players)
                    if (p.Relics.OfType<PaelsTooth>().FirstOrDefault() is { SerializableCards.Count: > 0 } tooth)
                    {
                        var back = p.PlayerRng.Rewards.NextItem(tooth.SerializableCards)!;
                        tooth.SerializableCards.Remove(back);
                        if (Shown(run.GetPlayerSlotIndex(p))) Console.WriteLine($"  {Seat(p)}: PAELS_TOOTH gives back {back.Id?.Entry}+");
                    }
                Rewards(cr);
                break;
            case MerchantRoom:
                foreach (var p in run.Players)
                {
                    var inv = MerchantInventory.CreateForNormalMerchant(p);
                    if (!Shown(run.GetPlayerSlotIndex(p))) continue;
                    Console.WriteLine($"  {Seat(p)} cards: " + string.Join(", ", inv.CardEntries.Select(e => $"{CardName(e.CreationResult?.Card)} {e.Cost}")));
                    Console.WriteLine($"  {Seat(p)} relics: " + string.Join(", ", inv.RelicEntries.Select(e => $"{e.Model?.Id.Entry} {e.Cost}")) +
                        "; potions: " + string.Join(", ", inv.PotionEntries.Select(e => $"{e.Model?.Id.Entry} {e.Cost}")));
                }
                break;
            case TreasureRoom:
                var relics = RunManager.Instance.TreasureRoomRelicSynchronizer;
                relics.BeginRelicPicking();
                Console.WriteLine("  relics for the party: " + string.Join(", ", relics.CurrentRelics!.Select(x => x.Id.Entry)));
                relics.OnRoomExited();
                relics.GetType().GetField("_currentRelics", Hidden)!.SetValue(relics, null);
                // OneOffSynchronizer.DoTreasureRoomRewards: each player's gold, before the poverty multiplier.
                foreach (var p in run.Players)
                {
                    int gold = p.PlayerRng.Rewards.NextInt(42, 53);
                    p.Gold += gold;
                    if (Shown(run.GetPlayerSlotIndex(p))) Console.WriteLine($"  {Seat(p)}: {gold} gold before the poverty multiplier");
                }
                break;
        }

        foreach (var pick in spec.Picks.Where(x => x.Floor == floor).OrderBy(x => x.Seat))
        {
            var p = run.Players[pick.Seat];
            if (pick.Option != null) Choose(p, room, pick.Option);
            if (pick.Relic != null) Obtain(p, pick.Relic);
        }
        // Events whose every option draws from the rewards stream: a seat without its option has later rewards off.
        if (room is EventRoom { CanonicalEvent.Id.Entry: "BRAIN_LEECH" or "COLORFUL_PHILOSOPHERS" } always)
            foreach (var p in run.Players.Where(p => Shown(run.GetPlayerSlotIndex(p))))
                if (!spec.Picks.Any(x => x.Floor == floor && x.Seat == run.GetPlayerSlotIndex(p) && x.Option != null))
                    Console.WriteLine($"  {Seat(p)}: every {always.CanonicalEvent.Id.Entry} option draws from the rewards stream; without this seat's option its later rewards are unreliable");
    }

    // CombatRoom.OfferRoomEndRewards.
    void Rewards(CombatRoom cr)
    {
        var sets = run.Players.Select(p => RewardsCmd.GenerateForRoomEnd(p, cr).GetAwaiter().GetResult()).ToList();
        foreach (var rs in sets)
        {
            Hook.BeforeCombatRewardOffered(rs, run, cr).GetAwaiter().GetResult();
            foreach (var g in rs.Rewards.OfType<GoldReward>()) rs.Player.Gold += g.Amount;
            if (Shown(run.GetPlayerSlotIndex(rs.Player)))
                Console.WriteLine($"  {Seat(rs.Player)}: " + (rs.Rewards.Count == 0 ? "no rewards" : string.Join("; ", rs.Rewards.Select(Show))));
        }
    }

    void Event(EventRoom er)
    {
        foreach (var p in run.Players)
        {
            var ev = er.CanonicalEvent.ToMutable();
            ev.BeginEvent(p, null, false).GetAwaiter().GetResult();
            if (!Shown(run.GetPlayerSlotIndex(p))) continue;
            Console.WriteLine($"  {Seat(p)} options: " + string.Join(" | ", ev.CurrentOptions.Select(o => o.TextKey)));
            // TrashHeap's outcomes are the event's own first draw, whatever came before.
            if (ev.Id.Entry == "TRASH_HEAP")
                Console.WriteLine($"  {Seat(p)} TRASH_HEAP.pages.INITIAL.options.DIVE_IN gets {TrashHeap(er, p, "Relics")}, " +
                    $"TRASH_HEAP.pages.INITIAL.options.GRAB gets {TrashHeap(er, p, "Cards")}");
        }
    }

    static string TrashHeap(EventRoom er, Player p, string list)
    {
        var ev = er.CanonicalEvent.ToMutable();
        ev.BeginEvent(p, null, false).GetAwaiter().GetResult();
        var items = ((System.Collections.IEnumerable)ev.GetType().GetProperty(list, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
            .Cast<AbstractModel>().ToArray();
        return ev.Rng.NextItem(items)!.Id.Entry;
    }

    /// <summary>
    /// Replays an event option's draws from the seat's rewards stream, as the option's own code makes them. Options not
    /// listed here are taken to draw nothing; an option known to draw that is not replayed is reported, since the seat's
    /// later rewards no longer hold.
    /// </summary>
    void Choose(Player p, AbstractRoom room, string option)
    {
        string ev = (room as EventRoom)?.CanonicalEvent.Id.Entry ?? "";
        IEnumerable<string>? got = (ev, option) switch
        {
            ("NEOW", "LEAD_PAPERWEIGHT") => [.. CardFactory.CreateForReward(p, 2,
                    new CardCreationOptions([ModelDb.CardPool<ColorlessCardPool>()], CardCreationSource.Other, CardRarityOddsType.RegularEncounter))
                .Select(c => CardName(c.Card))],
            ("BRAIN_LEECH", "SHARE_KNOWLEDGE") => [.. CardFactory.CreateForReward(p, 5,
                    CardCreationOptions.ForNonCombatWithDefaultOdds([p.Character.CardPool]))
                .Select(c => CardName(c.Card))],
            ("BRAIN_LEECH", "RIP") => Custom(p, [new CardReward(
                CardCreationOptions.ForNonCombatWithDefaultOdds([ModelDb.CardPool<ColorlessCardPool>()])
                    .WithFlags(CardCreationFlags.NoRarityModification | CardCreationFlags.NoCardPoolModifications), 3, p)]),
            ("COLORFUL_PHILOSOPHERS", _) => Philosophers(p, option),
            ("POTION_COURIER", "RANSACK") => [p.PlayerRng.Rewards.NextItem(p.Character.PotionPool.GetUnlockedPotions(p.UnlockState)
                .Concat(ModelDb.PotionPool<SharedPotionPool>().GetUnlockedPotions(p.UnlockState))
                .Where(x => x.Rarity == PotionRarity.Uncommon))!.Id.Entry],
            ("NEOW", "NEOWS_BONES") or ("ENDLESS_CONVEYOR", _) => null,
            _ => [],
        };
        if (got == null)
            Console.WriteLine($"  {Seat(p)} {ev} {option}: draws from the rewards stream and is not replayed; this seat's later rewards are unreliable");
        else if (got.Any())
            Console.WriteLine($"  {Seat(p)} {ev} {option}: {string.Join(" | ", got)}");
    }

    // ColorfulPhilosophers.OfferRewards: a common, an uncommon and a rare card reward from the chosen colour.
    static List<string> Philosophers(Player p, string colour)
    {
        var pool = ModelDb.AllCharacters.Select(c => c.CardPool).First(c => c.EnergyColorName.Equals(colour, StringComparison.OrdinalIgnoreCase));
        CardReward Of(CardRarity rarity) => new(
            new CardCreationOptions([pool], CardCreationSource.Other, CardRarityOddsType.Uniform, c => c.Rarity == rarity)
                .WithFlags(CardCreationFlags.NoRarityModification | CardCreationFlags.NoCardPoolModifications), 3, p);
        return Custom(p, [Of(CardRarity.Common), Of(CardRarity.Uncommon), Of(CardRarity.Rare)]);
    }

    static List<string> Custom(Player p, List<Reward> rewards) =>
        [.. RewardsCmd.GenerateCustom(p, rewards).GetAwaiter().GetResult().Rewards.Select(Show)];

    void Obtain(Player p, string id)
    {
        var type = typeof(RelicModel).Assembly.GetTypes().FirstOrDefault(t => typeof(RelicModel).IsAssignableFrom(t) && !t.IsAbstract && StringHelper.SnakeCase(t.Name).Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no relic {id}");
        var relic = (RelicModel)typeof(ModelDb).GetMethod(nameof(ModelDb.Relic))!.MakeGenericMethod(type).Invoke(null, null)!;
        RelicCmd.Obtain(relic.ToMutable(), p).GetAwaiter().GetResult();
        Console.WriteLine($"  {Seat(p)} gets {relic.Id.Entry}");
    }

    static string CardName(CardModel? c) => c == null ? "-" : c.Id.Entry + (c.IsUpgraded ? "+" : "");

    static string Show(Reward r) => r switch
    {
        GoldReward g => $"{g.Amount} gold",
        PotionReward p => $"potion {p.Potion?.Id.Entry}",
        CardReward c => "cards " + string.Join(", ", c.Cards.Select(CardName)),
        RelicReward x => $"relic {x.Relic?.Id.Entry}",
        _ => r.GetType().Name,
    };
}
