using OMTS.Application.Features.Purchasing.Items;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>GBR-03: the proposed input item code is VT + the highest number of the pattern + 1, with at least three digits.</summary>
public sealed class FarmingInputItemCodesTests
{
    [Theory]
    [InlineData(new string[0], "VT001")]
    [InlineData(new[] { "VT001", "VT002" }, "VT003")]
    [InlineData(new[] { "VT002", "VT001", "VT010" }, "VT011")]
    [InlineData(new[] { "vt009" }, "VT010")]
    [InlineData(new[] { " VT004 " }, "VT005")]
    [InlineData(new[] { "VT0099" }, "VT100")]
    [InlineData(new[] { "VT999" }, "VT1000")]
    [InlineData(new[] { "VT", "VTX1", "VT-5", "VT1A", "ABC12", "PHAO-01" }, "VT001")]
    [InlineData(new[] { "VT2147483646", "VT2147483647" }, "VT2147483648")]
    [InlineData(new[] { "VT99999999999", "VT007" }, "VT100000000000")]
    [InlineData(new[] { "VT9223372036854775807" }, "VT9223372036854775808")]
    public void Next_is_one_above_the_highest_code_of_the_pattern(string[] existing, string expected) =>
        Assert.Equal(expected, FarmingInputItemCodes.Next(existing));
}
