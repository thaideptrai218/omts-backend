using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01 OCR and meter photo (QA red team): impossible readings are never used, the uncertainty threshold is strict,
/// JPEG/PNG up to exactly 10 MB pass, anything else is refused before it is stored, and a recognition saves no row.
/// </summary>
public class WaterOcrPhotoRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0];

    [Fact]
    public async Task Impossible_or_future_readings_are_not_used_and_the_suggested_time_is_now()
    {
        var z = await api.ZoneAsync();
        var sentFrom = DateTimeOffset.UtcNow;

        var response = await z.RecognizeAsync("ph99_sal150_tmp80 at2099-01-01T08:00");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sentTo = DateTimeOffset.UtcNow;

        var ocr = body.GetProperty("ocr");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var field in new[] { "ph", "salinity", "temperature", "measuredAt" })
        {
            Assert.Equal(JsonValueKind.Null, ocr.GetProperty(field).ValueKind);
        }

        Assert.Equal(["PH", "SALINITY", "TEMPERATURE"], ocr.GetProperty("unread").EnumerateArray().Select(v => v.GetString()));
        Assert.StartsWith("OMTS could not read pH, salinity and temperature from the photo.", ocr.GetProperty("warning").Text("text"));
        Assert.InRange(body.GetProperty("measuredAt").GetDateTimeOffset(), sentFrom.AddSeconds(-1), sentTo.AddSeconds(1));
    }

    [Theory]
    [InlineData("ph7.2_sal20_tmp46", "temperature", "TEMPERATURE")]
    [InlineData("ph7.2_sal51_tmp28", "salinity", "SALINITY")]
    [InlineData("ph15_sal51_tmp28", "pH and salinity", "PH,SALINITY")]
    public async Task A_value_above_its_allowed_range_counts_as_not_read(string marker, string indicators, string unread)
    {
        var z = await api.ZoneAsync();

        var ocr = (await (await z.RecognizeAsync(marker)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ocr");

        Assert.Equal("Success", ocr.Text("status"));
        Assert.Equal(unread.Split(','), ocr.GetProperty("unread").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal($"OMTS could not read {indicators} from the photo. Please look at the meter and type in the missing values.", ocr.GetProperty("warning").Text("text"));
    }

    [Theory]
    [InlineData("conf0.8", false)]
    [InlineData("conf0.79", true)]
    public async Task Field_is_uncertain_only_below_the_confidence_threshold(string confidence, bool uncertain)
    {
        var z = await api.ZoneAsync();

        var body = await (await z.RecognizeAsync($"ph7.8_sal25.5_tmp28 {confidence}")).Content.ReadFromJsonAsync<JsonElement>();

        foreach (var field in new[] { "ph", "salinity", "temperature" })
        {
            Assert.Equal(uncertain, body.GetProperty("ocr").GetProperty(field).GetProperty("uncertain").GetBoolean());
        }
    }

    [Fact]
    public async Task Jpeg_and_a_photo_of_exactly_10_mb_are_accepted_webp_is_refused()
    {
        var z = await api.ZoneAsync();
        byte[] jpeg = [.. JpegSignature, .. Encoding.ASCII.GetBytes("ph7.2_sal19")];
        var exactly10Mb = new byte[10 * 1024 * 1024];
        WaterTestKit.PngSignature.CopyTo(exactly10Mb, 0);
        byte[] webp = [.. "RIFF"u8.ToArray(), 0x24, 0, 0, 0, .. "WEBPVP8 "u8.ToArray(), .. new byte[16]];

        var jpegResponse = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm(jpeg, contentType: "image/jpeg"));
        var largest = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm(exactly10Mb));
        var webpResponse = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm(webp, contentType: "image/webp"));

        Assert.Equal(HttpStatusCode.OK, jpegResponse.StatusCode);
        Assert.Equal(7.2m, (await jpegResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ocr").GetProperty("ph").GetProperty("value").GetDecimal());
        Assert.True(largest.StatusCode == HttpStatusCode.OK, await largest.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, webpResponse.StatusCode);
        Assert.Equal("Photo", (await webpResponse.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Photo_missing_not_jpeg_or_png_or_over_10_mb_is_refused_and_nothing_is_stored()
    {
        await using var own = new WaterApiFactory();
        var z = await own.ZoneAsync();
        var missing = await z.Owner.PostAsync($"{z.Base}/recognize", new MultipartFormDataContent { { new StringContent("x"), "other" } });
        var pdf = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm("%PDF-1.7 renamed"u8.ToArray(), contentType: "image/png"));
        var gif = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm("GIF89a not a meter photo"u8.ToArray(), contentType: "image/png"));
        var huge = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm([.. WaterTestKit.PngSignature, .. new byte[10 * 1024 * 1024]]));

        foreach (var response in new[] { missing, pdf, gif, huge })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
            Assert.Equal("Photo", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        Assert.False(Directory.Exists(own.StorageRoot) && Directory.EnumerateFiles(own.StorageRoot, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_photo_gone_from_the_storage_is_refused_when_recording()
    {
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        File.Delete(Path.Combine(api.StorageRoot, key));

        var response = await z.RecordAsync(7m, 20m, photoKey: key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Photo", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }
}
