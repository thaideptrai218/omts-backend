using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>WATER-01 display crop sent by the Mini App: "left,top,right,bottom" fractions of the photo.</summary>
public class OcrCropTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_crop_is_valid_and_means_the_whole_photo(string? text)
    {
        Assert.True(OcrCrop.TryParse(text, out var crop));
        Assert.Null(crop);
    }

    [Fact]
    public void A_crop_is_read_with_invariant_decimals_and_written_back_the_same_way()
    {
        Assert.True(OcrCrop.TryParse(" 0.1, 0.25 ,0.9,1 ", out var crop));

        Assert.Equal(new OcrCrop(0.1, 0.25, 0.9, 1), crop);
        Assert.Equal("0.1,0.25,0.9,1", crop!.ToString());
    }

    [Theory]
    [InlineData("0.1,0.2,0.9")] // three values
    [InlineData("0.1,0.2,0.9,0.8,0.5")] // five values
    [InlineData("a,0.2,0.9,0.8")] // not a number
    [InlineData("0,1;0,2;0,9;0,8")] // decimal comma
    [InlineData("-0.1,0.2,0.9,0.8")] // outside the photo
    [InlineData("0.1,0.2,1.1,0.8")]
    [InlineData("0.9,0.2,0.1,0.8")] // right before left
    [InlineData("0.1,0.8,0.9,0.2")] // bottom above top
    [InlineData("0.5,0.2,0.53,0.8")] // narrower than 5 %
    [InlineData("0.1,0.5,0.9,0.52")] // lower than 5 %
    [InlineData("NaN,0.2,0.9,0.8")]
    [InlineData("0.1,0.2,Infinity,0.8")]
    public void A_malformed_crop_is_invalid(string text)
    {
        Assert.False(OcrCrop.TryParse(text, out var crop));
        Assert.Null(crop);
    }
}
