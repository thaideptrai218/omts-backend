using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// ENV-01 OCR and meter photo (QA red team): an OCR failure never blocks or loses the draft, unreadable or
/// impossible readings are never used, the uncertainty threshold is strict, JPEG/PNG up to exactly 10 MB pass,
/// anything else is refused before it is stored, and a lost photo is MSG05 without touching the draft.
/// </summary>
public class WaterOcrPhotoRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0];

    [Fact]
    public async Task Re_recognition_while_ocr_still_fails_keeps_the_draft_and_confirming_without_values_is_MSG01()
    {
        var z = await api.ZoneAsync();
        var draftId = (await (await z.RecognizeAsync("ocr-fail")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measurement").Id();

        var again = await z.Owner.PostAsync($"/api/water-measurements/{draftId}/recognize", null);
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        var confirm = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{draftId}/confirm", new { });
        var problem = await confirm.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Failed", againBody.GetProperty("ocr").Text("status"));
        Assert.Equal("MSG04", againBody.GetProperty("ocr").GetProperty("warning").Text("code"));
        Assert.Equal("Draft", againBody.GetProperty("measurement").Text("status"));
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal("MSG01", problem.Text("code"));
        Assert.Equal(["pH", "Salinity"], problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == draftId));
        Assert.Equal(StatusIds.WaterMeasurement.Draft, stored.StatusId);
        Assert.Null(stored.OcrPh);
        Assert.NotNull(stored.PhotoUrl);
        Assert.Empty(await api.AlertsOfAsync(draftId));
    }

    [Fact]
    public async Task Impossible_or_future_readings_are_not_used_and_the_draft_takes_the_time_the_photo_was_sent()
    {
        var z = await api.ZoneAsync();
        var sentFrom = DateTimeOffset.UtcNow;

        var response = await z.RecognizeAsync("ph99_sal150 at2099-01-01T08:00");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sentTo = DateTimeOffset.UtcNow;

        var ocr = body.GetProperty("ocr");
        var draft = body.GetProperty("measurement");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var field in new[] { "ph", "salinity", "measuredAt" })
        {
            Assert.Equal(JsonValueKind.Null, ocr.GetProperty(field).ValueKind);
        }

        Assert.Equal(JsonValueKind.Null, draft.GetProperty("ph").ValueKind);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("ocrDraft").GetProperty("salinity").ValueKind);
        Assert.Equal("Manual", draft.Text("inputSource"));
        var measuredAt = draft.GetProperty("measuredAt").GetDateTimeOffset();
        Assert.InRange(measuredAt, sentFrom.AddSeconds(-1), sentTo.AddSeconds(1));
    }

    [Theory]
    [InlineData("conf0.8", false)]
    [InlineData("conf0.79", true)]
    public async Task Field_is_uncertain_only_below_the_confidence_threshold(string confidence, bool uncertain)
    {
        var z = await api.ZoneAsync();

        var body = await (await z.RecognizeAsync($"ph7.8_sal25.5 {confidence}")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(uncertain, body.GetProperty("ocr").GetProperty("ph").GetProperty("uncertain").GetBoolean());
        Assert.Equal(uncertain, body.GetProperty("ocr").GetProperty("salinity").GetProperty("uncertain").GetBoolean());
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
        Assert.Equal(7.2m, (await jpegResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measurement").GetProperty("ph").GetDecimal());
        Assert.True(largest.StatusCode == HttpStatusCode.OK, await largest.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, webpResponse.StatusCode);
        Assert.Equal("Photo", (await webpResponse.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(2, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Manual_entry_with_a_photo_that_is_not_jpeg_or_png_saves_and_stores_nothing()
    {
        await using var own = new WaterApiFactory();
        var z = await own.ZoneAsync();
        var form = WaterTestKit.PhotoForm("GIF89a not a meter photo"u8.ToArray(), contentType: "image/png");
        form.Add(new StringContent(Guid.NewGuid().ToString()), "clientOperationId");
        form.Add(new StringContent("7.1"), "ph");
        form.Add(new StringContent("20"), "salinity");
        form.Add(new StringContent(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")), "measuredAt");
        form.Add(new StringContent("true"), "confirm");

        var response = await z.Owner.PostAsync($"{z.Base}/with-photo", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
        Assert.Equal("Photo", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(0, await own.QueryAsync(db => db.WaterMeasurements.CountAsync()));
        Assert.False(Directory.Exists(own.StorageRoot) && Directory.EnumerateFiles(own.StorageRoot, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Re_recognition_of_a_draft_whose_photo_is_gone_is_MSG05_and_leaves_the_draft_unchanged()
    {
        var z = await api.ZoneAsync();
        var draftId = (await (await z.RecognizeAsync("ocr-fail")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measurement").Id();
        var key = await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == draftId).Select(m => m.PhotoUrl!).SingleAsync());
        File.Delete(Path.Combine(api.StorageRoot, key));

        var response = await z.Owner.PostAsync($"/api/water-measurements/{draftId}/recognize", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("MSG05", await response.ErrorCodeAsync());
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == draftId));
        Assert.Equal(StatusIds.WaterMeasurement.Draft, stored.StatusId);
        Assert.Equal(key, stored.PhotoUrl);
        Assert.Null(stored.OcrPh);
    }
}
