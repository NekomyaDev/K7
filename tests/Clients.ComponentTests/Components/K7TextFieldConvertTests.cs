using K7.Clients.Shared.UI.Components;

namespace K7.Clients.ComponentTests.Components;

[TestFixture]
public class K7TextFieldConvertTests
{
    [Test]
    public void To_ShouldParseInt_WhenTargetIsNullableInt()
    {
        K7TextFieldConvert.To<int?>("7").Should().Be(7);
    }

    [Test]
    public void To_ShouldParseLong_WhenTargetIsNullableLong()
    {
        K7TextFieldConvert.To<long?>("42").Should().Be(42L);
    }

    [Test]
    public void To_ShouldReturnNull_WhenRawIsEmpty()
    {
        K7TextFieldConvert.To<int?>("").Should().BeNull();
        K7TextFieldConvert.To<int?>(null).Should().BeNull();
    }
}
