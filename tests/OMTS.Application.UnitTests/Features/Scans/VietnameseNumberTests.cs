using OMTS.Application.Features.Scans;

namespace OMTS.Application.UnitTests.Features.Scans;

/// <summary>Numbers as a Vietnamese document writes them, and the plain and English forms a model may give instead.</summary>
public class VietnameseNumberTests
{
    [Theory]
    [InlineData("2.315.876,23", 2315876.23)]
    [InlineData("438.224,18", 438224.18)]
    [InlineData("2315876.23", 2315876.23)]
    [InlineData("2315876,23", 2315876.23)]
    [InlineData("2,315,876.23", 2315876.23)]
    [InlineData(" 2.315.876,23 m ", 2315876.23)]
    [InlineData("12,50", 12.5)]
    [InlineData("12.5", 12.5)]
    [InlineData("0.125", 0.125)]
    [InlineData("4,00", 4.0)]
    [InlineData("438224", 438224.0)]
    public void Reads_the_number_in_every_accepted_form(string text, double expected)
    {
        Assert.Equal(expected, VietnameseNumber.Parse(text)!.Value, 6);
    }

    [Theory]
    [InlineData("438.224", 438224.0)] // one dot and exactly three digits after a one-to-three digit integer: thousands
    [InlineData("2.315.876", 2315876.0)]
    [InlineData("1.500", 1500.0)]
    [InlineData("2,315,876", 2315876.0)]
    public void A_single_dot_with_three_digits_after_it_is_a_thousands_separator(string text, double expected)
    {
        Assert.Equal(expected, VietnameseNumber.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-2315876.23")]
    [InlineData("1.2.3")]
    [InlineData("2.315.87,23")] // a thousands group of two digits
    [InlineData("1.234,56,7")]
    [InlineData("12,50 ha")]
    [InlineData(",5")]
    [InlineData("1e5")]
    public void Anything_else_is_not_a_number(string? text)
    {
        Assert.Null(VietnameseNumber.Parse(text));
    }
}
