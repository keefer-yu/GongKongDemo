using Scada.Core.Tags;

namespace Scada.Tests.Core;

public class TagValueCodecTests
{
    private static TagDefinition UInt16() => new()
    {
        Id = "level",
        Name = "液位",
        Address = "HR:0",
        DataType = TagDataType.UInt16,
        Scale = 0.1,
        Decimals = 1,
    };

    private static TagDefinition Float32(ByteOrder order = ByteOrder.ABCD) => new()
    {
        Id = "temp",
        Name = "温度",
        Address = "HR:1",
        DataType = TagDataType.Float32,
        ByteOrder = order,
        Decimals = 1,
    };

    [Fact]
    public void UInt16_applies_scale_and_offset()
    {
        var tag = new TagDefinition
        {
            Id = "level", Name = "液位", Address = "HR:0",
            DataType = TagDataType.UInt16, Scale = 0.1, Offset = 5
        };

        var value = TagValueCodec.Decode(new ushort[] { 245 }, tag);

        Assert.Equal(29.5, value, 3);
    }

    [Theory]
    [InlineData(ByteOrder.ABCD)]
    [InlineData(ByteOrder.DCBA)]
    [InlineData(ByteOrder.BADC)]
    [InlineData(ByteOrder.CDAB)]
    public void Float32_roundtrips_for_every_byte_order(ByteOrder order)
    {
        var tag = Float32(order);

        var registers = TagValueCodec.Encode(72.5, tag);
        var value = TagValueCodec.Decode(registers, tag);

        Assert.Equal(72.5, value, 3);
        Assert.Equal(2, registers.Length);
    }

    [Fact]
    public void Different_byte_orders_produce_different_register_images()
    {
        var abcd = TagValueCodec.Encode(72.5, Float32(ByteOrder.ABCD));
        var dcba = TagValueCodec.Encode(72.5, Float32(ByteOrder.DCBA));

        Assert.NotEqual(abcd, dcba);
        Assert.Equal(72.5, TagValueCodec.Decode(dcba, Float32(ByteOrder.DCBA)), 3);
    }

    [Fact]
    public void Int16_supports_negative_values()
    {
        var tag = new TagDefinition
        {
            Id = "offset", Name = "偏差", Address = "HR:2",
            DataType = TagDataType.Int16, Scale = 0.1
        };

        var registers = TagValueCodec.Encode(-12.3, tag);

        Assert.Equal(-12.3, TagValueCodec.Decode(registers, tag), 3);
    }

    [Fact]
    public void Bool_encodes_as_zero_or_one()
    {
        var tag = new TagDefinition
        {
            Id = "pump", Name = "泵", Address = "C:0", DataType = TagDataType.Bool
        };

        Assert.Equal(1, TagValueCodec.Encode(1, tag)[0]);
        Assert.Equal(0, TagValueCodec.Encode(0, tag)[0]);
        Assert.Equal(1, TagValueCodec.Decode(new ushort[] { 7 }, tag));
    }

    [Fact]
    public void UInt16_Encode_clamps_out_of_range_values()
    {
        var tag = UInt16();

        Assert.Equal(ushort.MaxValue, TagValueCodec.Encode(999999, tag)[0]);
        Assert.Equal((ushort)0, TagValueCodec.Encode(-10, tag)[0]);
    }
}
