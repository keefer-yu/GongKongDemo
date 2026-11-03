using Scada.Core.Tags;

namespace Scada.Tests.Core;

public class TagAddressTests
{
    [Theory]
    [InlineData("HR:0", TagArea.HoldingRegister, 0)]
    [InlineData("C:3", TagArea.Coil, 3)]
    [InlineData("IR:5", TagArea.InputRegister, 5)]
    [InlineData("DI:2", TagArea.DiscreteInput, 2)]
    [InlineData("40001", TagArea.HoldingRegister, 0)]
    [InlineData("40003", TagArea.HoldingRegister, 2)]
    [InlineData("30001", TagArea.InputRegister, 0)]
    [InlineData("10001", TagArea.DiscreteInput, 0)]
    [InlineData("00001", TagArea.Coil, 0)]
    public void Parses_prefixed_and_legacy_addresses(string text, TagArea area, int offset)
    {
        var address = TagAddress.Parse(text);

        Assert.Equal(area, address.Area);
        Assert.Equal(offset, address.Offset);
    }

    [Fact]
    public void Invalid_address_throws_format_exception()
    {
        Assert.Throws<FormatException>(() => TagAddress.Parse("XX:0"));
    }
}
