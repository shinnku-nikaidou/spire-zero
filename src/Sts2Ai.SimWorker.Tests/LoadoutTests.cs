using Sts2Ai.SimWorker;

namespace Sts2Ai.SimWorker.Tests;

public class LoadoutTests
{
    [Fact]
    public void ADeckGivesUpgradesAndEnchantments()
    {
        var deck = Loadout.Parse("BASH,BASH+,BASH+2,LEADING_STRIKE@SHARP:2,LEADING_STRIKE+3@SHARP:4*2", null, null, null, null).Deck!;
        Assert.Equal(
            [
                new CardSpec("BASH"),
                new CardSpec("BASH", 1),
                new CardSpec("BASH", 2),
                new CardSpec("LEADING_STRIKE", 0, "SHARP", 2),
                new CardSpec("LEADING_STRIKE", 3, "SHARP", 4),
                new CardSpec("LEADING_STRIKE", 3, "SHARP", 4),
            ],
            deck);
    }

    [Fact]
    public void ACardAndARelicCanBeGivenWhatTheyKeepInASave()
    {
        var card = CardSpec.Parse("MAD_SCIENCE[TinkerTimeType=Power;TinkerTimeRider=Improvement]+");
        Assert.Equal(new CardSpec("MAD_SCIENCE", 1, null, 0, "TinkerTimeType=Power;TinkerTimeRider=Improvement"), card);
        Assert.Equal("MAD_SCIENCE[TinkerTimeType=Power;TinkerTimeRider=Improvement]+", card.Text);
        Assert.Equal(new RelicSpec("PAELS_TOOTH", "SerializableCards=BASH|ANGER+"), RelicSpec.Parse("PAELS_TOOTH[SerializableCards=BASH|ANGER+]"));
        Assert.Equal(new RelicSpec("ANCHOR"), RelicSpec.Parse("ANCHOR"));
        Assert.Throws<FormatException>(() => RelicSpec.Parse("ANCHOR[IsWax=true"));
        Assert.Equal(["ANCHOR[IsWax=true;IsMelted=true]", "VAJRA", "VAJRA"], Loadout.Parse(null, "ANCHOR[IsWax=true;IsMelted=true],VAJRA*2", null, null, null).Relics!);
    }

    [Fact]
    public void AnEnchantmentNeedsItsAmount() => Assert.Throws<FormatException>(() => CardSpec.Parse("BASH@SHARP"));
}
