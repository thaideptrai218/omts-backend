using OMTS.Application.Common.Constants;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>The default code of an input item follows its type (GI000001 seed, VT000001 material, HK000001 other) and the default code of a unit is the item code and a letter.</summary>
public sealed class FarmingInputItemCodesTests
{
    [Theory]
    [InlineData(InputItemGroup.Seed, 1, "GI000001")]
    [InlineData(InputItemGroup.Material, 25, "VT000025")]
    [InlineData(InputItemGroup.Other, 999999, "HK999999")]
    [InlineData(InputItemGroup.Seed, 1000000, "GI1000000")]
    public void The_code_is_the_prefix_of_the_type_and_at_least_six_digits(InputItemGroup group, long number, string expected) =>
        Assert.Equal(expected, FarmingInputItemCodes.Format(group, number));

    [Fact]
    public void Each_type_has_a_sequence_of_its_own()
    {
        Assert.Equal(
            [DocumentSequenceKeys.InputItemSeed, DocumentSequenceKeys.InputItemMaterial, DocumentSequenceKeys.InputItemOther],
            new[] { InputItemGroup.Seed, InputItemGroup.Material, InputItemGroup.Other }.Select(FarmingInputItemCodes.SequenceKeyOf));
        Assert.Equal(3, new[] { "GI", "VT", "HK" }.Distinct().Count());
    }

    [Theory]
    [InlineData("thùng", 'T')]
    [InlineData("Cái", 'C')]
    [InlineData("đôi", 'D')]
    [InlineData("Đoạn", 'D')]
    [InlineData("ống", 'O')]
    [InlineData("  kg", 'K')]
    [InlineData("%", 'U')]
    [InlineData("", 'U')]
    public void The_letter_of_a_unit_is_its_first_letter_without_diacritics(string unit, char expected) =>
        Assert.Equal(expected, InputItemUnitRules.LetterOf(unit));

    [Fact]
    public void The_default_unit_code_is_the_item_code_a_dash_and_the_letter() =>
        Assert.Equal("001-T", InputItemUnitRules.DefaultCode("001", "thùng"));

    [Fact]
    public async Task A_clash_appends_a_digit_until_the_code_is_free()
    {
        var taken = new HashSet<string> { "001-C", "001-C1", "001-C2" };

        Assert.Equal("001-C3", await InputItemUnitRules.FreeCodeAsync("001-C", code => Task.FromResult(taken.Contains(code))));
        Assert.Equal("001-T", await InputItemUnitRules.FreeCodeAsync("001-T", code => Task.FromResult(taken.Contains(code))));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("12", true)]
    [InlineData("0.5", true)]
    [InlineData("0.001", true)]
    [InlineData("1000000", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("0.0001", false)]
    [InlineData("1000000.001", false)]
    public void A_factor_is_above_zero_with_three_decimals_at_most(string factor, bool allowed) =>
        Assert.Equal(allowed, InputItemUnitRules.FactorAllowed(decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture)));
}
