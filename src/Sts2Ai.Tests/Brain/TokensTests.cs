using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

/// <summary>
/// What the nets read of a state (docs: macro-brain/network §2, combat-brain/network §2), as the JSON the trainers get:
/// the one reading there is, so these are its tests.
/// </summary>
public class TokensTests
{
    // Start (0,0) -> (0,1) Monster or (1,1) Elite; both -> (0,2) RestSite -> (0,3) Boss; (2,1) leads nowhere from here.
    private const string Points = """
        [{"col":0,"row":0,"type":"Ancient","next":[{"col":0,"row":1},{"col":1,"row":1}]},
         {"col":0,"row":1,"type":"Monster","next":[{"col":0,"row":2}]},
         {"col":1,"row":1,"type":"Elite","quest":true,"next":[{"col":0,"row":2}]},
         {"col":2,"row":1,"type":"Shop","next":[{"col":0,"row":2}]},
         {"col":0,"row":2,"type":"RestSite","next":[{"col":0,"row":3}]},
         {"col":0,"row":3,"type":"Boss","next":[]}]
        """;

    private const string Deck = """[{"id":"STRIKE","up":5,"enchant":{"id":"SHARP","amount":2}},{"id":"NEUTRALIZE","affliction":{"id":"HEX","amount":1}}]""";
    private const string Relics = """[{"id":"RING","counter":3},{"id":"VASE","used_up":true}]""";

    private static RunView Run(string deck = Deck, string relics = Relics, string more = "") =>
        ProtocolJson.Deserialize<RunView>("""
            {"character":"SILENT","ascension":3,"act":0,"act_id":"OVERGROWTH","floor":1,"hp":50,"max_hp":70,"gold":99,
             "deck":DECK,"relics":RELICS,"potions":["FIRE",null,null]MORE,
             "map":{"points":POINTS,"visited":[{"col":0,"row":0}],"boss":"B","ancient":"NEOW"}}
            """.Replace("DECK", deck).Replace("RELICS", relics).Replace("MORE", more).Replace("POINTS", Points));

    private static JsonNode Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream)) write(json);
        return JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))!;
    }

    private static JsonNode Tokens(RunView run) => Json(RunNet.Tokens(run).Write);

    private static void Same(string expected, JsonNode? actual) => Assert.Equal(JsonNode.Parse(expected)!.ToJsonString(), actual!.ToJsonString());

    [Fact]
    public void ARunsCardsAreIdsAndTheirModifiersAmountsAndItsRelicsPotionsAndContextIdsOnly()
    {
        var t = Tokens(Run());
        Same("""[["STRIKE",3,["enchant:SHARP"],[0.2],[0,0]],["NEUTRALIZE",0,["affliction:HEX"],[0.1],[0,0]]]""", t["cards"]);
        Same("""["RING","VASE:used"]""", t["relics"]);
        Same("""["FIRE"]""", t["potions"]);
        Same("""["character:SILENT","act:OVERGROWTH","boss:B","ancient:NEOW"]""", t["context"]);
        Same("""["Monster+1","Elite+1:quest","RestSite+2","Boss+3"]""", t["map"]);
    }

    [Fact]
    public void RelicsReadTheirCountersStatusesAndTheCardsTheyKeepAsTheCombatNetReadsThem()
    {
        var t = Tokens(Run(relics: """
            [{"id":"RING","counter":25,"status":"Disabled"},{"id":"VASE","used_up":true,"melted":true},
             {"id":"PAELS_TOOTH","counter":0,"cards":["STRIKE","DASH"]},{"id":"PUZZLE","spent":true}]
            """));
        Same("""["RING","VASE:used","PAELS_TOOTH","PUZZLE"]""", t["relics"]);
        Same("""[[2.5,1,0],[0,0,1],[0,1,0],[0,0,0]]""", t["relic_values"]);
        Same("""["Disabled","Normal","Normal","Spent"]""", t["relic_status"]);
        Same("""["STRIKE","DASH"]""", t["held"]);
    }

    [Fact]
    public void ARunsCardReadsItsKeywordsWhatTinkerTimeMadeOfItAndTheNumbersARunGrowsOnIt()
    {
        var t = Tokens(Run(deck: """
            [{"id":"MAD_SCIENCE","keywords":["Ethereal"],"tinker":{"type":"Skill","rider":"Wisdom"}},
             {"id":"DEFEND_SILENT","enchant":{"id":"GOOPY","amount":5},"keywords":["Exhaust"],"vars":{"Block":5}},
             {"id":"THE_SCYTHE","vars":{"Damage":23,"Increase":5}},
             {"id":"GENETIC_ALGORITHM","up":1,"vars":{"Block":260}},
             {"id":"FETCH","vars":{"OstyDamage":6}}]
            """));
        Same("""
            [["MAD_SCIENCE",0,["keyword:Ethereal","tinker:Skill","rider:Wisdom"],[],[0,0]],
             ["DEFEND_SILENT",0,["enchant:GOOPY","keyword:Exhaust"],[0.5],[0,0.25]],
             ["THE_SCYTHE",0,[],[],[1.15,0]],
             ["GENETIC_ALGORITHM",1,[],[],[0,10]],
             ["FETCH",0,[],[],[0.3,0]]]
            """, t["cards"]);
    }

    [Fact]
    public void ARunsNumbersAreRawByName()
    {
        Same("""{"floor":1,"hp":50,"max_hp":70,"gold":99,"ascension":3,"potion_slots":3,"auto_win_0":0,"auto_win_1":0,"auto_win_2":0}""",
            Json(json => RunNet.Write(json, Run()))["state"]);
        // The game's rule: the acts whose plain monster fights are won at their start.
        var ruled = Json(json => RunNet.Write(json, Run(more: ""","auto_win":[0,1]""")))["state"]!;
        Assert.Equal([1, 1, 0], new[] { "auto_win_0", "auto_win_1", "auto_win_2" }.Select(name => (double)ruled[name]!));
    }

    private const string Enemies = """
        [{"uid":1,"id":"M","hp":12,"max_hp":20,"block":0,"powers":[],"intents":[{"type":"Attack","damage":6,"hits":1}]},
         {"uid":2,"id":"M","hp":0,"max_hp":20,"block":0,"powers":[],"intents":[{"type":"Attack","damage":6,"hits":1}]}]
        """;

    private const string Hand = """[{"id":"STRIKE","up":5,"cost":1,"vars":{"Damage":6},"enchant":{"id":"SHARP","amount":2}}]""";

    private static CombatObservation Obs(int round = 1, string hand = Hand, string enemies = Enemies, string relics = Relics) =>
        ProtocolJson.Deserialize<CombatObservation>("""
            {"encounter":"E","room":"Monster","act":0,"ascension":0,"round":ROUND,
             "player":{"uid":0,"character":"SILENT","hp":50,"max_hp":70,"block":0,"energy":3,"max_energy":3,"stars":0,"gold":0,
                       "powers":[{"id":"STRENGTH_POWER","amount":2}],"orb_slots":0,"orbs":[]},
             "hand":HAND,"draw":[{"id":"DEFEND","cost":1,"vars":{"Block":5}}],"discard":[],"exhaust":[],
             "potions":["FIRE",null,null],"relics":RELICS,"allies":[],"enemies":ENEMIES}
            """.Replace("ROUND", $"{round}").Replace("HAND", hand).Replace("RELICS", relics).Replace("ENEMIES", enemies));

    private static JsonNode Tokens(CombatObservation obs, params EnemyTurns[] history) => Json(CombatTokens.Of(obs, history, 3).Write);

    [Fact]
    public void ACombatsCardsAreIdsUpgradesPilesModifiersCostsAndNumbers()
    {
        var t = Tokens(Obs());
        Same("""
            [["STRIKE",3,0,["enchant:SHARP"],0.3333333333333333,[0.2],0.3,[0,0,0,0,0]],
             ["DEFEND",0,1,[],0.3333333333333333,[],0,[0.25,0,0,0,0]]]
            """, t["cards"]);
        Same("""["RING","VASE:used"]""", t["relics"]);
        Same("""["Normal","Normal"]""", t["relic_status"]);
        Same("""["FIRE"]""", t["potions"]);
        Same("""[["STRENGTH_POWER",0.2,0,null,0]]""", t["powers"]);
        Same("""["character:SILENT","encounter:E","room:Monster","act:0"]""", t["context"]);
    }

    [Fact]
    public void ACombatsCardReadsItsKeywordsTinkerSpentEnchantmentAndGatheredNumbers()
    {
        var cards = Tokens(Obs(hand: """
            [{"id":"MAD_SCIENCE","cost":1,"stars":2,"keywords":["Exhaust","Retain"],"calc":12,"replay":1,
              "tinker":{"type":"Attack","rider":"Violence"},"affliction":{"id":"HEXED","amount":1}},
             {"id":"PUNCH_DAGGER","cost":1,"enchant":{"id":"MOMENTUM","amount":2,"extra":4},"spent":true,"this_turn":["Sly"]},
             {"id":"BONE_SHARDS","cost":1,"vars":{"OstyDamage":9}}]
            """))["cards"]!;
        Same("""["affliction:HEXED","keyword:Exhaust","keyword:Retain","tinker:Attack","rider:Violence"]""", cards[0]![3]);
        Same("[0.1]", cards[0]![5]);
        Same("[0,0.6,0.5,0.4,0]", cards[0]![7]);
        Same("""["enchant:MOMENTUM","keyword:Sly","spent"]""", cards[1]![3]);
        Same("[0,0,0,0,0.2]", cards[1]![7]);
        Assert.Equal(0.45, (double)cards[2]![6]!);
    }

    [Fact]
    public void APowerReadsItsMovedNumberAndTheCardItStoleAndARelicItsEffectSpent()
    {
        var t = Tokens(Obs(
            enemies: """[{"uid":1,"id":"M","hp":12,"max_hp":20,"block":0,"intents":[],"powers":[{"id":"SWIPE_POWER","amount":1,"stolen":{"id":"DEFEND"},"detail":10}]}]""",
            relics: """[{"id":"CENTENNIAL_PUZZLE","spent":true},{"id":"RING","status":"Active"}]"""));
        Same("""[["SWIPE_POWER",0.1,0,"DEFEND",0.5]]""", t["enemies"]![0]!["powers"]);
        Same("""["Spent","Active"]""", t["relic_status"]);
    }

    [Fact]
    public void ThePlayerAndEachEnemyAreTheirRawStateScaled()
    {
        var t = Tokens(Obs(round: 4));
        Same("[0.7142857142857143,0.5,0.7,0,1,1,0,0.4]", t["player"]);
        var enemies = t["enemies"]!.AsArray();
        // One lying dead is read: it may revive.
        Assert.Equal(2, enemies.Count);
        Assert.Equal("M", (string)enemies[0]!["id"]!);
        Same("[0.6,0.12,0.2,0,0]", enemies[0]!["scalars"]);
        Same("""[[["Attack",0.6,0.3]]]""", enemies[0]!["slots"]);
        Same("[0,0,0.2,0,0]", enemies[1]!["scalars"]);
    }

    [Fact]
    public void AnEnemyReadsItsLastRoundsIntentsMostRecentFirstAndNoneOfThisRoundOn()
    {
        // The record as Harness keeps it holds this round's too (round 6): only the rounds before it are read.
        var turns = Enumerable.Range(1, 6).Select(r => new EnemyTurn(r, [new IntentView { Type = $"T{r}" }])).ToList();
        var enemy = Tokens(Obs(round: 6), new EnemyTurns(1, "M", turns))["enemies"]![0]!;
        Assert.Equal(["Attack", "T5", "T4", "T3"], enemy["slots"]!.AsArray().Select(s => (string)s![0]![0]!));
        Assert.Equal(0.5, (double)enemy["scalars"]![4]!);
    }
}
