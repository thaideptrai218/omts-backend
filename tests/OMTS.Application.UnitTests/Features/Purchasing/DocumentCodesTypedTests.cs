using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>Codes typed by the user on purchase receipts, returns and payments: trimmed, 1–30 ASCII letters, digits, "-", "_" or ".".</summary>
public class DocumentCodesTypedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PN000001")]
    [InlineData(" kv1-2024_a.b ")]
    [InlineData("A")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123")]
    public void Empty_or_a_valid_code_is_allowed(string? input) => Assert.True(DocumentCodes.TypedAllowed(input));

    [Theory]
    [InlineData("PN 01")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ01234")]
    [InlineData("PN/01")]
    [InlineData("Đ01")]
    public void Spaces_other_signs_letters_outside_ASCII_and_a_31st_character_are_refused(string input) =>
        Assert.False(DocumentCodes.TypedAllowed(input));

    [Fact]
    public void A_typed_code_is_trimmed_and_keeps_its_case() => Assert.Equal("Kv1-a", DocumentCodes.Typed("  Kv1-a "));

    [Theory]
    [InlineData(null, true)]
    [InlineData(" ", true)]
    [InlineData("pn000007", true)]
    [InlineData("PN000008", false)]
    public void A_fixed_code_may_be_sent_only_as_stored(string? input, bool allowed) =>
        Assert.Equal(allowed, DocumentCodes.SameOrEmpty(input, "PN000007"));
}
