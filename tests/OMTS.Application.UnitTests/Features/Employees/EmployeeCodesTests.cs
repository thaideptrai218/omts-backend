using OMTS.Application.Features.Employees;

namespace OMTS.Application.UnitTests.Features.Employees;

/// <summary>EMP-API-06: employee codes, NV000001 when generated, otherwise typed with letters, digits and "-" (at most 20, kept upper case).</summary>
public class EmployeeCodesTests
{
    [Theory]
    [InlineData(1, "NV000001")]
    [InlineData(123, "NV000123")]
    [InlineData(999999, "NV999999")]
    [InlineData(1000000, "NV1000000")]
    public void A_generated_code_is_NV_and_at_least_six_digits(long number, string expected) =>
        Assert.Equal(expected, EmployeeCodes.Generated(number));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_code_is_not_typed_and_valid(string? input)
    {
        Assert.Null(EmployeeCodes.Typed(input, out var valid));
        Assert.True(valid);
    }

    [Theory]
    [InlineData("nv-01", "NV-01")]
    [InlineData("  A1 ", "A1")]
    [InlineData("ABCDEFGHIJ0123456789", "ABCDEFGHIJ0123456789")]
    public void A_typed_code_is_trimmed_and_upper_case(string input, string expected)
    {
        Assert.Equal(expected, EmployeeCodes.Typed(input, out var valid));
        Assert.True(valid);
    }

    [Theory]
    [InlineData("NV 01")]
    [InlineData("NV_01")]
    [InlineData("NV.01")]
    [InlineData("Đ01")]
    [InlineData("ABCDEFGHIJ01234567890")]
    public void Spaces_other_signs_accents_and_a_21st_character_are_refused(string input)
    {
        EmployeeCodes.Typed(input, out var valid);
        Assert.False(valid);
    }
}
