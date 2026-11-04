using System.Text;
using OMTS.Application.Common.Validation;

namespace OMTS.Application.UnitTests.Common;

/// <summary>One name on the screen has one spelling (BR-SALE-05); a name that shows nothing is empty (BR-SALE-04).</summary>
public class DisplayNamesTests
{
    [Theory]
    [InlineData("  Hàu sữa\t", "Hàu sữa")]
    [InlineData("Hàu  sữa", "Hàu sữa")]
    [InlineData("Hàu sữa", "Hàu sữa")]
    [InlineData("Hàu\tsữa", "Hàu sữa")]
    [InlineData("Hàu　\n sữa", "Hàu sữa")]
    [InlineData("Hàu​ sữa", "Hàu sữa")]
    [InlineData("H​àu s﻿ữa⁠", "Hàu sữa")]
    [InlineData("Hàu ​ sữa", "Hàu sữa")]
    [InlineData("Hàu­sữa", "Hàusữa")]
    public void Spaces_and_what_shows_nothing_collapse_into_one_spelling(string typed, string expected) =>
        Assert.Equal(expected, DisplayNames.Normalize(typed));

    [Theory]
    [InlineData("Hàu sữa")]
    [InlineData("Ốc hương đỏ")]
    [InlineData("Hàu thịt")]
    public void A_decomposed_name_is_the_composed_one(string name)
    {
        Assert.Equal(name, DisplayNames.Normalize(name.Normalize(NormalizationForm.FormD)));
        Assert.True(DisplayNames.Normalize(name).IsNormalized(NormalizationForm.FormC));
    }

    // Removed first: a removed character between a letter and its mark must not keep them apart.
    [Fact]
    public void A_mark_after_a_removed_character_still_composes() =>
        Assert.Equal("ữ", DisplayNames.Normalize("ư​̃"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t　")]
    [InlineData("​")]
    [InlineData("﻿")]
    [InlineData("⁠")]
    [InlineData("‌‍­")]
    [InlineData("‎‮")]
    [InlineData("͏")]
    [InlineData("ㅤ")]
    [InlineData("️")]
    [InlineData("\0")]
    public void A_name_that_shows_nothing_is_empty(string? typed) =>
        Assert.Equal(string.Empty, DisplayNames.Normalize(typed));

    // string.Normalize throws on an unpaired surrogate: it comes out as the replacement character, never an exception.
    [Fact]
    public void An_unpaired_surrogate_does_not_throw() =>
        Assert.Equal("a�b", DisplayNames.Normalize("a\uD83Db"));

    [Fact]
    public void A_character_outside_the_basic_plane_is_kept() =>
        Assert.Equal("Hàu 🦪", DisplayNames.Normalize("Hàu  🦪"));
}
