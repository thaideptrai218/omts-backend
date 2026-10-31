using OMTS.Application.Features.Purchasing.Items;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>QA of the GBR-03 proposal (OMTS-218): the proposal is never a code already used, discontinued ones included.</summary>
public sealed class FarmingInputItemCodesQaTests
{
    [Theory]
    [InlineData(new[] { "VT001", "vt001", "VT0001" }, "VT002")]
    [InlineData(new[] { "VT998", "VT999" }, "VT1000")]
    [InlineData(new[] { "VT1000", "VT0999" }, "VT1001")]
    [InlineData(new[] { "VT 5", "VT５", "VT-7", "AVT9" }, "VT001")]
    public void The_proposal_is_one_above_the_highest_code_of_the_pattern_and_never_one_of_them(string[] existing, string expected)
    {
        var proposal = FarmingInputItemCodes.Next(existing);

        Assert.Equal(expected, proposal);
        Assert.DoesNotContain(existing, code => string.Equals(code.Trim(), proposal, StringComparison.OrdinalIgnoreCase));
    }

    // QA finding F3 (Low, fixed): NumberOf ignored VT2147483647, so once the Owner had saved the proposal after
    // VT2147483646 the same proposal came back (and failed with MSG-GEN-02).
    [Fact]
    public void The_proposal_at_the_top_of_the_number_range_is_not_a_code_already_used()
    {
        string[] existing = ["VT2147483646", "VT2147483647"];

        Assert.DoesNotContain(FarmingInputItemCodes.Next(existing), existing);
    }
}
