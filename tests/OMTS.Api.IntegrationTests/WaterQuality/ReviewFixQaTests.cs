using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of the PR #11 review fixes: OCR fails closed when the provider is unset in Production, extreme dates are MSG01 on
/// every water endpoint that takes dates, the latest accepted date is one year ahead and the month chart stops at 10 years.
/// </summary>
public class ReviewFixQaTests
{
    /// <summary>The API running as Production with the given <c>Ocr:Provider</c> (the base factory always forces Fake).</summary>
    private sealed class ProductionWaterFactory(string provider) : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.UseSetting("Ocr:Provider", provider);
        }
    }

    [Fact]
    public async Task Production_with_the_provider_unset_reads_no_values_and_keeps_the_draft()
    {
        await using var api = new ProductionWaterFactory(string.Empty);
        var z = await api.ZoneAsync();

        // The marker would make the fake answer pH 7.2 / salinity 20 in Development.
        var response = await z.RecognizeAsync("ph7.2_sal20");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ocr = body.GetProperty("ocr");
        Assert.Equal("Failed", ocr.Text("status"));
        Assert.Equal("MSG04", ocr.GetProperty("warning").Text("code"));
        Assert.Equal(JsonValueKind.Null, ocr.GetProperty("ph").ValueKind);
        Assert.Equal(JsonValueKind.Null, ocr.GetProperty("salinity").ValueKind);
        var draft = body.GetProperty("measurement");
        Assert.Equal("Draft", draft.Text("status"));
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("ph").ValueKind);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("salinity").ValueKind);
    }

    [Theory]
    [InlineData("export?to=9999-12-31", "To")]
    [InlineData("export?from=0001-01-01", "From")]
    [InlineData("chart?to=9999-12-31", "To")]
    [InlineData("chart?from=9999-12-30&to=9999-12-31&groupBy=month", "To")]
    [InlineData("chart?from=0001-01-01&to=0001-01-31&groupBy=day", "To")]
    [InlineData("?from=2026-10-01&to=9999-12-31", "To")]
    [InlineData("?from=0001-01-01&to=9999-12-31", "To")]
    [InlineData("?from=0001-01-01&to=2026-10-01", "From")]
    [InlineData("chart?from=9999-12-30&groupBy=month&to=2026-10-01", "From")]
    public async Task Extreme_dates_are_MSG01_on_every_water_endpoint_that_takes_dates(string query, string field)
    {
        await using var api = new AuthApiFactory();
        var z = await api.ZoneAsync();

        var response = await z.Owner.GetAsync($"{z.Base}{(query.StartsWith('?') ? string.Empty : "/")}{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Fact]
    public async Task The_latest_accepted_date_is_one_year_ahead_and_one_day_later_is_MSG01()
    {
        await using var api = new AuthApiFactory();
        var z = await api.ZoneAsync();
        // Server and test both run on Asia/Saigon (UTC+7), the zone the water dates are read in.
        var latest = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddYears(1);

        var accepted = await z.Owner.GetAsync($"{z.Base}?from={latest:yyyy-MM-dd}&to={latest:yyyy-MM-dd}");
        var refused = await z.Owner.GetAsync($"{z.Base}?to={latest.AddDays(1):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("To", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Fact]
    public async Task The_month_chart_accepts_exactly_ten_years_and_refuses_one_day_more()
    {
        await using var api = new AuthApiFactory();
        var z = await api.ZoneAsync();

        var exact = await z.Owner.GetAsync($"{z.Base}/chart?from=2000-01-01&to=2010-01-01&groupBy=month");
        var over = await z.Owner.GetAsync($"{z.Base}/chart?from=2000-01-01&to=2010-01-02&groupBy=month");

        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal("Group by", (await over.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Fact]
    public async Task Clients_that_omit_from_and_to_still_get_the_defaults_on_history_chart_and_export()
    {
        await using var api = new AuthApiFactory();
        var z = await api.ZoneAsync();

        var history = await z.Owner.GetAsync(z.Base);
        var monthChart = await z.Owner.GetAsync($"{z.Base}/chart");
        var dayChart = await z.Owner.GetAsync($"{z.Base}/chart?groupBy=day");
        var export = await z.Owner.GetAsync($"{z.Base}/export");

        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(HttpStatusCode.OK, monthChart.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dayChart.StatusCode);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }
}
