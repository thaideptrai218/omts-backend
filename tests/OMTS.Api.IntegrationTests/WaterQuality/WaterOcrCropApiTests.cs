using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01 recognize with the display crop of the Mini App, and the Paddle provider's start-up checks: a configuration
/// mistake stops the host instead of leaving every meter photo unread.
/// </summary>
public class WaterOcrCropApiTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_valid_crop_is_accepted_and_the_whole_photo_is_still_stored()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync(z.Base + "/recognize", CropForm("ph7.4_sal21_tmp27", "0.1,0.2,0.9,0.8"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("photoKey").GetString()));
        Assert.Equal(7.4m, body.GetProperty("ocr").GetProperty("ph").GetProperty("value").GetDecimal());
    }

    [Theory]
    [InlineData("0.1,0.2,0.9")]
    [InlineData("0.9,0.2,0.1,0.8")]
    [InlineData("0,1;0,2;0,9;0,8")]
    public async Task A_malformed_crop_is_MSG_GEN_01_and_no_photo_is_stored(string crop)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync(z.Base + "/recognize", CropForm("ph7.4", crop));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Contains("Crop", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("", "")] // no sidecar URL
    [InlineData("not a url", "")]
    [InlineData("ocr:8000", "")] // parses as a URI with the scheme "ocr"
    [InlineData("http://ocr:8000", "Ph,Oxygen")] // typo in the meter layout
    public void The_Paddle_provider_with_a_bad_setting_stops_the_host(string baseUrl, string layout)
    {
        using var factory = new PaddleFactory(baseUrl, layout);

        Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
    }

    private static MultipartFormDataContent CropForm(string marker, string crop)
    {
        var form = WaterTestKit.PhotoForm(WaterTestKit.Photo(marker));
        form.Add(new StringContent(crop), "crop");
        return form;
    }

    /// <summary>The API as Production with the Paddle provider (the base factory always forces Fake).</summary>
    private sealed class PaddleFactory(string baseUrl, string layout) : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.UseSetting("Ocr:Provider", "Paddle");
            builder.UseSetting("Ocr:Paddle:BaseUrl", baseUrl);
            builder.UseSetting("Ocr:Paddle:Layout", layout);
        }
    }
}
