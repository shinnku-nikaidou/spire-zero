using Sts2Ai.SimWorker;

namespace Sts2Ai.Tests.SimWorker;

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
    public void AnEnchantmentNeedsItsAmount() => Assert.Throws<FormatException>(() => CardSpec.Parse("BASH@SHARP"));
}
