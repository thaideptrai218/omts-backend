using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>WATER-03 list and detail: voided measurements stay in the list with their reason, the filters, paging, MSG-GEN-10 and access.</summary>
public class WaterHistoryTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private const string VoidReason = "Đo nhầm cọc";

    [Fact]
    public async Task The_default_list_is_newest_first_with_the_voided_ones_marked_with_their_reason()
    {
        var z = await api.ZoneAsync();
        var now = DateTimeOffset.UtcNow;
        var old = await z.RecordedAsync(9.1m, 20m, measuredAt: now.AddDays(-20), temperature: 26m);
        var voided = await z.RecordedAsync(7.0m, 22m, measuredAt: now.AddDays(-5));
        var recent = await z.RecordedAsync(7.6m, 21m, measuredAt: now.AddDays(-1));
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(voided.Id(), VoidReason)).StatusCode);

        var list = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);

        var rows = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, list.GetProperty("total").GetInt32());
        Assert.Equal([recent.Id(), voided.Id(), old.Id()], rows.Select(r => r.Id()));
        Assert.Equal(["Active", "Voided", "Active"], rows.Select(r => r.Text("status")));
        Assert.Equal([null, VoidReason, null], rows.Select(r => r.Text("voidReason")));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("message").ValueKind);
        // Time, raft, values and who recorded it.
        Assert.Equal((z.Raft.Id, z.Raft.Code, 26m), (rows[2].GetProperty("raftId").GetGuid(), rows[2].Text("raftCode"), rows[2].GetProperty("temperature").GetDecimal()));
        Assert.Equal(z.OwnerAccount.Id, rows[2].GetProperty("measuredBy").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_value_outside_is_marked_against_the_thresholds_of_the_measurement_also_when_it_is_voided()
    {
        var z = await api.ZoneAsync();
        var voided = await z.RecordedAsync(9.1m, 20m);
        var withinNow = await z.RecordedAsync(7m, 12m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(voided.Id(), VoidReason)).StatusCode);
        var lowered = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6.5m, phMax = 9.5m, salinityMin = 10m, salinityMax = 30m });
        Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);

        var rows = (await z.Owner.GetFromJsonAsync<JsonElement>(z.Base)).GetProperty("items").EnumerateArray().ToList();

        var active = rows.Single(r => r.Id() == withinNow.Id());
        Assert.Equal("SALINITY", active.GetProperty("valuesOutside")[0].Text("indicator"));
        Assert.Equal(15m, active.GetProperty("valuesOutside")[0].GetProperty("min").GetDecimal());
        Assert.Equal(15m, active.GetProperty("thresholdsUsed").GetProperty("salinityMin").GetDecimal());
        Assert.Equal("Active", active.Text("alertState"));
        var marked = rows.Single(r => r.Id() == voided.Id());
        Assert.Equal("PH", marked.GetProperty("valuesOutside")[0].Text("indicator"));
        Assert.Equal("Voided", marked.Text("alertState"));
    }

    [Theory]
    [InlineData("?status=Active", new[] { "Active", "Active" })]
    [InlineData("?status=Voided", new[] { "Voided" })]
    [InlineData("?status=All", new[] { "Active", "Voided", "Active" })]
    [InlineData("?status=Active,Voided", new[] { "Active", "Voided", "Active" })]
    [InlineData("?status=Active&status=Voided", new[] { "Active", "Voided", "Active" })]
    public async Task The_status_filter_gives_what_it_names(string query, string[] statuses)
    {
        var z = await api.ZoneAsync();
        await SeedAsync(z);

        var list = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}{query}");

        Assert.Equal(statuses, list.GetProperty("items").EnumerateArray().Select(r => r.Text("status")));
    }

    [Fact]
    public async Task The_period_limits_the_list_and_comes_back_with_it()
    {
        var z = await api.ZoneAsync();
        var local = TimeSpan.FromHours(7);
        await z.RecordedAsync(7.1m, 20m, measuredAt: new DateTimeOffset(2026, 3, 10, 8, 0, 0, local));
        await z.RecordedAsync(7.2m, 20m, measuredAt: new DateTimeOffset(2026, 4, 10, 8, 0, 0, local));
        await z.RecordedAsync(7.3m, 20m, measuredAt: new DateTimeOffset(2026, 5, 10, 8, 0, 0, local));

        var list = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?from=2026-04-01&to=2026-04-30");

        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(7.2m, list.GetProperty("items")[0].GetProperty("ph").GetDecimal());
        Assert.Equal(("2026-04-01", "2026-04-30"), (list.Text("from"), list.Text("to")));
    }

    [Fact]
    public async Task The_list_pages_with_the_total_of_the_filter_and_a_page_past_the_end_is_no_empty_list_message()
    {
        var z = await api.ZoneAsync();
        await SeedAsync(z);

        var first = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?pageSize=2&page=1");
        var second = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?pageSize=2&page=2");
        var past = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?pageSize=2&page=3");
        var byDefault = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);

        Assert.Equal((2, 3, 1), (first.GetProperty("items").GetArrayLength(), first.GetProperty("total").GetInt32(), first.GetProperty("page").GetInt32()));
        Assert.Equal((1, 3, 2), (second.GetProperty("items").GetArrayLength(), second.GetProperty("total").GetInt32(), second.GetProperty("page").GetInt32()));
        Assert.Empty(first.GetProperty("items").EnumerateArray().Select(r => r.Id()).Intersect(second.GetProperty("items").EnumerateArray().Select(r => r.Id())));
        Assert.Equal((0, JsonValueKind.Null), (past.GetProperty("items").GetArrayLength(), past.GetProperty("message").ValueKind));
        Assert.Equal(20, byDefault.GetProperty("pageSize").GetInt32());
    }

    // WATER-03.4.
    [Fact]
    public async Task An_empty_list_says_MSG_GEN_10_in_message()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m);

        var none = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?from=2026-01-01&to=2026-01-31");
        var noneVoided = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?status=Voided");

        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Equal("MSG-GEN-10", none.Text("messageCode"));
        Assert.Equal("There is no data for the zone and period you chose. Please choose again.", none.Text("message"));
        Assert.Equal("There is no data for the zone, period and status you chose. Please choose again.", noneVoided.Text("message"));
    }

    [Theory]
    [InlineData("chart?from=2026-10-02&to=2026-10-01", "From")]
    [InlineData("?from=2026-10-02&to=2026-10-01", "From")]
    [InlineData("?from=2026-13-01", "From")]
    [InlineData("?status=Deleted", "Status")]
    [InlineData("?status=Draft", "Status")]
    [InlineData("?status=Confirmed", "Status")]
    [InlineData("export?from=2026-10-05&to=2026-10-01", "From")]
    [InlineData("export?status=Deleted", "Status")]
    // Extreme dates used to overflow DateTimeOffset (500).
    [InlineData("?from=0001-01-01", "From")]
    [InlineData("?to=9999-12-31", "To")]
    [InlineData("chart?from=0001-01-01", "From")]
    [InlineData("chart?to=9999-12-31", "To")]
    [InlineData("export?from=1999-12-31", "From")]
    public async Task Invalid_filters_are_MSG_GEN_01_with_the_field(string query, string field)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.GetAsync($"{z.Base}{(query.StartsWith('?') ? "" : "/")}{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(21474837)]
    public async Task A_huge_page_is_clamped_instead_of_overflowing_the_offset(int page)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.GetAsync($"{z.Base}?status=All&page={page}&from=2000-01-01");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("page").GetInt32() <= int.MaxValue / 100);
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    // §I.4.4: WATER-03 is Full for the Owner, Full* for a Zone Manager in the zones of their pairs, No for the others.
    [Fact]
    public async Task Zone_managers_of_the_zone_and_the_owner_read_it_others_get_MSG_GEN_08()
    {
        var z = await api.ZoneAsync();
        var measurement = await z.RecordedAsync(7m, 20m);
        var (otherZoneManager, _) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        var (accountant, _) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var (manager, _) = await api.StaffAsync(z.Zone);
        var urls = new[] { z.Base, $"{z.Base}/chart", $"{z.Base}/export", $"/api/water-measurements/{measurement.Id()}" };

        foreach (var url in urls)
        {
            foreach (var refused in new[] { otherZoneManager, accountant })
            {
                var response = await refused.GetAsync(url);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }

            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await z.Owner.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Detail_has_the_thresholds_of_the_measurement_and_a_short_lived_photo_url()
    {
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id, "ph7.7_sal23");
        var id = (await (await z.RecordAsync(7.5m, 23m, photoKey: key)).Content.ReadFromJsonAsync<JsonElement>()).Id();

        var detail = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{id}");
        var photo = await z.Owner.GetAsync(detail.Text("photoUrl"));

        Assert.Equal(7.5m, detail.GetProperty("ph").GetDecimal());
        Assert.Equal(6.5m, detail.GetProperty("thresholdsUsed").GetProperty("phMin").GetDecimal());
        Assert.Equal(10m, detail.GetProperty("thresholdsUsed").GetProperty("temperatureMin").GetDecimal());
        Assert.Equal((z.Zone.Code, z.Raft.Code), (detail.Text("zoneCode"), detail.Text("raftCode")));
        Assert.False(detail.TryGetProperty("ocrDraft", out _));
        Assert.False(detail.TryGetProperty("recordVersion", out _));
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal(WaterTestKit.Photo("ph7.7_sal23"), await photo.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Newest first: Active, Voided, Active.</summary>
    private static async Task SeedAsync(WaterZone z)
    {
        var now = DateTimeOffset.UtcNow;
        await z.RecordedAsync(7.1m, 20m, measuredAt: now.AddDays(-20));
        var voided = await z.RecordedAsync(7.2m, 20m, measuredAt: now.AddDays(-10));
        await z.RecordedAsync(7.3m, 20m, measuredAt: now.AddDays(-5));
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(voided.Id(), VoidReason)).StatusCode);
    }
}
