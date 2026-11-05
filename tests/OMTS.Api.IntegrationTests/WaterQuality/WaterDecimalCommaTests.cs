using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// The with-photo form and its invariant parsing are gone: values now arrive as JSON only. A salinity of "2,5", typed the
/// Vietnamese way, must be refused on the record and on the correction, on a server running in vi-VN too. It must never
/// be saved as 25, which would sit inside the thresholds and raise no alert.
/// </summary>
public class WaterDecimalCommaTests(WaterDecimalCommaTests.VietnameseServerFactory api) : IClassFixture<WaterDecimalCommaTests.VietnameseServerFactory>
{
    public static TheoryData<string> CommaSalinities => new()
    {
        "\"2,5\"",   // a JSON string
        "2,5",       // the bare value, which is not JSON at all
    };

    [Fact]
    public async Task The_server_of_these_tests_runs_in_vi_VN()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.GetAsync(z.Base);

        Assert.Equal("vi-VN", response.Headers.GetValues(VietnameseCultureFilter.Header).Single());
    }

    [Theory]
    [MemberData(nameof(CommaSalinities))]
    public async Task Json_record_with_a_comma_is_refused_and_nothing_is_saved(string salinity)
    {
        var z = await api.ZoneAsync();
        var photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id);

        var response = await z.Owner.PostAsync(z.Base, Json(RecordBody(z, photoKey, salinity)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingSavedAsync(z);
    }

    [Fact]
    public async Task Form_with_a_comma_is_refused_and_nothing_is_saved()
    {
        var z = await api.ZoneAsync();
        var photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var fields = new Dictionary<string, string>
        {
            ["raftId"] = z.Raft.Id.ToString(),
            ["measuredAt"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O", CultureInfo.InvariantCulture),
            ["ph"] = "7.5",
            ["salinity"] = "2,5",
            ["temperature"] = "25",
            ["photoKey"] = photoKey,
        };

        var urlEncoded = await z.Owner.PostAsync(z.Base, new FormUrlEncodedContent(fields));
        using var multipart = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            multipart.Add(new StringContent(value), name);
        }

        var multipartResponse = await z.Owner.PostAsync(z.Base, multipart);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, urlEncoded.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, multipartResponse.StatusCode);
        await AssertNothingSavedAsync(z);
    }

    [Fact]
    public async Task The_with_photo_route_no_longer_takes_a_comma_either()
    {
        var z = await api.ZoneAsync();
        using var form = WaterTestKit.PhotoForm(WaterTestKit.Photo());
        form.Add(new StringContent("7.5"), "ph");
        form.Add(new StringContent("2,5"), "salinity");

        var response = await z.Owner.PostAsync($"{z.Base}/with-photo", form);

        Assert.False(response.IsSuccessStatusCode, $"with-photo answered {(int)response.StatusCode}");
        await AssertNothingSavedAsync(z);
    }

    [Theory]
    [MemberData(nameof(CommaSalinities))]
    public async Task Correction_with_a_comma_is_refused_and_the_value_stays(string salinity)
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7.5m, 20m);
        var id = saved.GetProperty("id").GetGuid();
        var body = $$"""{"measuredAt":"{{DateTimeOffset.UtcNow.AddMinutes(-5):O}}","ph":7.5,"salinity":{{salinity}},"temperature":25,"reason":"typed the Vietnamese way"}""";

        var response = await z.Owner.PostAsync($"/api/water-measurements/{id}/corrections", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(20m, await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == id).Select(m => m.Salinity).SingleAsync()));
    }

    [Fact]
    public async Task A_string_with_a_dot_is_read_as_the_decimal_it_shows()
    {
        var z = await api.ZoneAsync(thresholds: false);
        var photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id);

        var response = await z.Owner.PostAsync(z.Base, Json(RecordBody(z, photoKey, "\"2.5\"")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2.5m, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("salinity").GetDecimal());
    }

    private static string RecordBody(WaterZone z, string photoKey, string salinity) =>
        $$"""{"raftId":"{{z.Raft.Id}}","measuredAt":"{{DateTimeOffset.UtcNow.AddMinutes(-10):O}}","ph":7.5,"salinity":{{salinity}},"temperature":25,"photoKey":"{{photoKey}}"}""";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task AssertNothingSavedAsync(WaterZone z) =>
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));

    /// <summary>The real API, every request of it running in vi-VN, where the comma is the decimal separator.</summary>
    public sealed class VietnameseServerFactory : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, VietnameseCultureFilter>());
        }
    }

    private sealed class VietnameseCultureFilter : IStartupFilter
    {
        public const string Header = "X-Test-Culture";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextStep) =>
            {
                CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
                context.Response.Headers[Header] = CultureInfo.CurrentCulture.Name;
                return nextStep();
            });
            next(app);
        };
    }
}
