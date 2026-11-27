using OMTS.Domain.Constants;

namespace OMTS.Domain.UnitTests;

/// <summary>The codes of a cultivation log type and how a sent value is read.</summary>
public class CultivationLogTypesTests
{
    [Theory]
    [InlineData("FEEDING")]
    [InlineData("CLEANING")]
    [InlineData("INSPECTION")]
    [InlineData("DISEASE")]
    [InlineData("REPAIR")]
    [InlineData("OTHER")]
    public void Every_code_is_known_and_fits_the_column(string code)
    {
        Assert.Contains(code, CultivationLogTypes.All);
        Assert.Equal(code, CultivationLogTypes.Normalize(code));
        Assert.True(code.Length <= CultivationLogTypes.MaxLength);
    }

    [Fact]
    public void There_are_exactly_six_types()
    {
        Assert.Equal(6, CultivationLogTypes.All.Count);
        Assert.Equal(CultivationLogTypes.All.Count, CultivationLogTypes.All.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_value_is_other(string? value) =>
        Assert.Equal(CultivationLogTypes.Other, CultivationLogTypes.Normalize(value));

    [Theory]
    [InlineData(" feeding ", "FEEDING")]
    [InlineData("Repair", "REPAIR")]
    public void Spacing_and_letter_case_are_normalized(string value, string expected) =>
        Assert.Equal(expected, CultivationLogTypes.Normalize(value));

    [Theory]
    [InlineData("FEED")]
    [InlineData("Cho ăn")]
    [InlineData("FEEDING;")]
    public void An_unknown_value_is_null(string value) =>
        Assert.Null(CultivationLogTypes.Normalize(value));
}
