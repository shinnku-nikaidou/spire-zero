using System.Reflection;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2Ai.GameBridge;

namespace Sts2Ai.SimWorker.Tests;

/// <summary>
/// The export's walk, on a schema written here for one of the game's plain classes and objects made here: no game is
/// booted. The real schema is made from the user's copy of the game and is not in git; `sim parity --world engine`
/// writes every decision of its combats along it.
/// </summary>
public sealed class EngineExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sts2ai-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A schema of the game's CardPile alone: its cards and its type as state, its other fields dropped.</summary>
    /// <param name="pileField">The CLR name the schema gives the field that holds the cards.</param>
    /// <param name="forgotten">A field of the game the schema says nothing of.</param>
    /// <param name="delegates">Whether the pile's ContentsChanged, a delegate, is state too.</param>
    private EngineSchema Schema(string pileField = "_cards", string? forgotten = null, bool delegates = false)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var dropped = typeof(CardPile).GetFields(Declared).Select(f => f.Name)
            .Where(f => f is not ("_cards" or "<Type>k__BackingField") && f != forgotten && !(delegates && f == "ContentsChanged"));
        string path = Path.Combine(_dir, "schema.json");
        File.WriteAllText(path, """
            {"format":1,"game":"v-test","roots":[],"arenas":["temporary_card_cost","card_pile"],
             "classes":[
              {"name":"CardPile","cs":"MegaCrit.Sts2.Core.Entities.Cards.CardPile","base":null,"arena":"card_pile","open":false,"abstract":false,"type_parameters":[],
               "fields":[{"name":"_cards","cs":"PILE_FIELD","type":{"kind":"list","of":{"kind":"ref","class":"CardModel"}}},
                         {"name":"type_","cs":"<Type>k__BackingField","type":{"kind":"enum","name":"PileType","rust":"i32"}}DELEGATE],
               "dropped":DROPPED}],
             "structs":[],"enums":[],"hash":"abc"}
            """.Replace("PILE_FIELD", pileField).Replace("DROPPED", JsonSerializer.Serialize(dropped))
            .Replace("DELEGATE", delegates ? """,{"name":"contents_changed","cs":"ContentsChanged","type":{"kind":"delegate"}}""" : ""));
        return EngineSchema.Load(path);
    }

    private static void Heard() { }

    /// <summary>The one object of a pile's arena as the export wrote it: its delegate field.</summary>
    private string Changed(CardPile pile, params CardPile[] before)
    {
        var written = new EngineExport(Schema(delegates: true)).Write([.. before.Append(pile).Select((p, i) => ($"p{i}", (object)p))], "v-test", new(0));
        using var doc = JsonDocument.Parse(written.Bytes);
        return doc.RootElement.GetProperty("arenas").GetProperty("card_pile")[before.Length].GetProperty("CardPile").GetProperty("contents_changed").GetRawText();
    }

    [Fact]
    public void ADelegateIsItsMethodAndWhatItIsBoundTo()
    {
        var (hand, draw) = (new CardPile(PileType.Hand), new CardPile(PileType.Draw));
        Assert.Equal("null", Changed(hand));

        // A method of an object of the export: bound to that object, by its place.
        hand.ContentsChanged += draw.InvokeCardAddFinished;
        Assert.Equal("""{"method":"MegaCrit.Sts2.Core.Entities.Cards.CardPile::InvokeCardAddFinished","target":["card_pile",0]}""", Changed(hand, draw));

        // A static method and a lambda that captures nothing are bound to nothing.
        var pile = new CardPile(PileType.Discard);
        pile.ContentsChanged += Heard;
        Assert.Equal("{\"method\":\"Sts2Ai.SimWorker.Tests.EngineExportTests::Heard\",\"target\":null}", Changed(pile));
        pile = new CardPile(PileType.Discard);
        pile.ContentsChanged += () => { };
        using (var lambda = JsonDocument.Parse(Changed(pile)))
        {
            Assert.StartsWith("Sts2Ai.SimWorker.Tests.EngineExportTests+<>c::<ADelegateIsItsMethodAndWhatItIsBoundTo>b__", lambda.RootElement.GetProperty("method").GetString());
            Assert.Equal(JsonValueKind.Null, lambda.RootElement.GetProperty("target").ValueKind);
        }

        // A lambda that captures is bound to the object the compiler made for what it captures: one of the walk, here
        // of a class the schema does not hold.
        int heard = 0;
        pile = new CardPile(PileType.Discard);
        pile.ContentsChanged += () => heard++;
        using (var closure = JsonDocument.Parse(Changed(pile)))
        {
            var target = closure.RootElement.GetProperty("target");
            Assert.Equal("outside", target[0].GetString());
            Assert.StartsWith("Sts2Ai.SimWorker.Tests.EngineExportTests+<>c__DisplayClass", target[1].GetString());
        }
    }

    [Fact]
    public void ADelegateOfTwoMethodsIsRefused()
    {
        var pile = new CardPile(PileType.Hand);
        pile.ContentsChanged += Heard;
        pile.ContentsChanged += Heard;
        Assert.Contains("a delegate of 2 methods", Assert.Throws<InvalidOperationException>(() => Changed(pile)).Message);
    }

    [Fact]
    public void AnExportHoldsEveryObjectOnceByArenaAndIndex()
    {
        var (hand, draw) = (new CardPile(PileType.Hand), new CardPile(PileType.Draw));
        var written = new EngineExport(Schema()).Write([("Hand", hand), ("Draw", draw), ("Again", hand), ("Text", "not of the schema")], "v-test", new(3));
        string Pile(PileType type) => """{"class":"CardPile","CardPile":{"_cards":[],"type_":TYPE}}""".Replace("TYPE", ((int)type).ToString());
        Assert.Equal(
            """{"format":1,"game":"v-test","schema":"abc","sim":{"prompts":3},"roots":{"Hand":["card_pile",0],"Draw":["card_pile",1],"Again":["card_pile",0],"Text":["outside","System.String"]},"""
            + $"\"arenas\":{{\"card_pile\":[{Pile(PileType.Hand)},{Pile(PileType.Draw)}]}},\"outside\":{{\"System.String\":1}}}}",
            Encoding.UTF8.GetString(written.Bytes));
        Assert.Equal((2, 1), (written.Objects, written.Outside["System.String from Text"]));
        Assert.Empty(written.Freed);
    }

    [Fact]
    public void ASchemaThatNamesWhatTheGameLacksIsRefused()
    {
        var refused = Assert.Throws<InvalidDataException>(() => Schema(pileField: "_noSuchField"));
        Assert.Contains("no field MegaCrit.Sts2.Core.Entities.Cards.CardPile._noSuchField", refused.Message);
    }

    [Fact]
    public void ASchemaThatSaysNothingOfAFieldOfTheGameIsRefused()
    {
        string forgotten = typeof(CardPile).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(f => f.Name).First(f => f is not ("_cards" or "<Type>k__BackingField"));
        var refused = Assert.Throws<InvalidDataException>(() => Schema(forgotten: forgotten));
        Assert.Contains($"the schema neither lists nor drops MegaCrit.Sts2.Core.Entities.Cards.CardPile.{forgotten}", refused.Message);
    }
}
