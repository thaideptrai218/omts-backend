using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of OMTS-216 PR 0.2 on WATER-01, WATER-02 and WATER-04 (SRS 27/10 Part II §3): the OCR warning object keeps its
/// legacy code, MSG-WATER-05 names the indicator to fix, and status refusals carry MSG61 as their legacy code.
/// </summary>
public class WaterMessageCodeQaTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private const string NotRead = "from the photo. Please look at the meter and type in the missing values.";

    // WATER-01.2: the UserMessage inside the response carries legacyCode MSG04 (OCR "service is not responding" before).
    [Fact]
    public async Task Failed_ocr_warning_is_msg_water_01_with_legacy_msg04_inside_the_response()
    {
        var z = await api.ZoneAsync();

        var body = await (await z.RecognizeAsync("ocr-fail")).BodyAsync(HttpStatusCode.OK);
        var warning = body.GetProperty("ocr").GetProperty("warning");

        Assert.Equal("Failed", body.GetProperty("ocr").Str("status"));
        Assert.Equal("MSG-WATER-01", warning.Str("code"));
        Assert.Equal($"OMTS could not read pH and salinity {NotRead}", warning.Str("text"));
        Assert.Equal("MSG04", warning.Str("legacyCode"));
    }

    // WATER-04.0.E2: "shows which indicator to fix" – pH, salinity, or both (MSG28 before, without the indicator).
    [Theory]
    [InlineData(7.0, 8.5, 30.0, 15.0, "salinity")]
    [InlineData(8.5, 6.5, 30.0, 30.0, "pH and salinity")]
    public async Task Minimum_not_less_than_maximum_names_the_indicator_with_legacy_msg28(double phMin, double phMax, double salinityMin, double salinityMax, string indicator)
    {
        var z = await api.ZoneAsync();

        var refused = await (await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin, phMax, salinityMin, salinityMax }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-WATER-05", "MSG28");

        Assert.Equal($"The minimum of {indicator} must be less than the maximum. The thresholds were not saved. Please correct the values.", refused.Detail());
    }

    // GBR-01 on a final measurement: MSG-GEN-09 with the status display name (MSG61 "Cannot change … : reason" before).
    [Fact]
    public async Task Change_of_a_final_measurement_is_msg_gen_09_with_legacy_msg61()
    {
        var z = await api.ZoneAsync();
        var original = await z.RecordedAsync(7m, 20m);
        Assert.Equal(HttpStatusCode.Created, (await z.Owner.CorrectAsync(original.Id(), 7.3m, 20m, recordVersion: 1)).StatusCode);

        var corrected = await (await z.Owner.CorrectAsync(original.Id(), 7.4m, 20m, recordVersion: 2))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        Assert.Equal("Water measurement is Corrected and cannot be used for this action.", corrected.Detail());
    }

    // SRS WATER-01.2 / MSG-WATER-01: "could not read one or more values … {indicator_list}". A reading without salinity
    // (here out of range, so treated as not read) answers Success with no warning today; the placeholder is fixed to
    // "pH and salinity" and only used when the OCR Service fails.
    [Fact(Skip = "QA finding OMTS-216 PR 0.2 #5: a partial OCR read shows no MSG-WATER-01 naming the value not read.")]
    public async Task Partial_ocr_read_warns_with_msg_water_01_naming_the_value_not_read()
    {
        var z = await api.ZoneAsync();

        var body = await (await z.RecognizeAsync("ph7.2_sal999")).BodyAsync(HttpStatusCode.OK);
        var ocr = body.GetProperty("ocr");

        Assert.Equal(JsonValueKind.Null, ocr.GetProperty("salinity").ValueKind);
        Assert.Equal("MSG-WATER-01", ocr.GetProperty("warning").Str("code"));
        Assert.Equal($"OMTS could not read salinity {NotRead}", ocr.GetProperty("warning").Str("text"));
    }
}
