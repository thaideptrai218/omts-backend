using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01 – the fake OCR invents readings, so it is opt-in (Development and test settings only). A host with the
/// provider unset or unknown reads nothing: the photo is kept with MSG-WATER-01 and the user enters the values.
/// </summary>
public class WaterOcrNotConfiguredTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Azure")]
    public async Task Without_the_fake_provider_a_photo_gives_no_values_a_Failed_status_and_a_key_to_record_with(string provider)
    {
        await using var api = new WaterApiFactory(new Dictionary<string, string> { ["Ocr:Provider"] = provider });
        var z = await api.ZoneAsync();

        // The marker would make the fake answer pH 7.2 / salinity 20; the unconfigured adapter ignores the photo.
        var response = await z.RecognizeAsync("ph7.2_sal20");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ocr = body.GetProperty("ocr");
        Assert.Equal("Failed", ocr.Text("status"));
        Assert.Equal("MSG-WATER-01", ocr.GetProperty("warning").Text("code"));
        foreach (var field in new[] { "ph", "salinity", "temperature", "measuredAt" })
        {
            Assert.Equal(JsonValueKind.Null, ocr.GetProperty(field).ValueKind);
        }

        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync()));
        Assert.Equal(HttpStatusCode.Created, (await z.RecordAsync(7.2m, 20m, photoKey: body.Text("photoKey"))).StatusCode);
    }

    [Fact]
    public async Task The_fake_provider_still_reads_the_markers()
    {
        await using var api = new WaterApiFactory(new Dictionary<string, string> { ["Ocr:Provider"] = "Fake" });
        var z = await api.ZoneAsync();

        var body = await (await z.RecognizeAsync("ph7.2_sal20_tmp31.5")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(7.2m, body.GetProperty("ocr").GetProperty("ph").GetProperty("value").GetDecimal());
        Assert.Equal(31.5m, body.GetProperty("ocr").GetProperty("temperature").GetProperty("value").GetDecimal());
    }

    [Fact]
    public void Only_the_Development_settings_select_the_fake_provider()
    {
        IConfiguration Load(params string[] files)
        {
            var builder = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory);
            foreach (var file in files)
            {
                builder.AddJsonFile(file, optional: false);
            }

            return builder.Build();
        }

        Assert.True(string.IsNullOrEmpty(Load("appsettings.json")["Ocr:Provider"]));
        Assert.Equal("Fake", Load("appsettings.json", "appsettings.Development.json")["Ocr:Provider"]);
    }
}
