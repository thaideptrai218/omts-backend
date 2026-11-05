using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.Common;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// OMTS-214: a time sent without a UTC offset is Vietnam time (UTC+7, <see cref="BusinessClock"/>), never the server's
/// time zone or UTC; "Z" and "+07:00" keep their offset. The test host cannot run in another time zone, so each test
/// asserts the stored instant (and the +07:00 offset of an offset-less value) directly.
/// </summary>
public class WaterBusinessTimeTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);
    private static readonly DateOnly Yesterday = TimeProvider.System.Today().AddDays(-1);
    private static readonly string Wall = $"{Yesterday:yyyy-MM-dd}T08:00:00";

    public static TheoryData<string, int> Forms => new()
    {
        // (suffix after the wall-clock text, expected UTC hour of the stored instant)
        { string.Empty, 1 },
        { "Z", 8 },
        { "+07:00", 1 },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Manual_entry_reads_a_time_without_offset_as_vietnam_time_and_keeps_an_explicit_offset(string suffix, int utcHour)
    {
        var z = await api.ZoneAsync();

        var response = await z.RecordAtAsync(Wall + suffix);

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var expected = new DateTimeOffset(Yesterday.ToDateTime(new TimeOnly(utcHour, 0)), TimeSpan.Zero);
        Assert.Equal(expected, body.GetProperty("measuredAt").GetDateTimeOffset());
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.ZoneId == z.Zone.Id));
        Assert.Equal(expected, stored.MeasuredAt);
        if (suffix.Length == 0)
        {
            Assert.Equal(Vietnam, stored.MeasuredAt.Offset);
        }
    }

    [Fact]
    public async Task A_correction_reads_a_time_without_offset_as_vietnam_time()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordAtAsync(Wall);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>()).Id();

        var corrected = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{id}/corrections", new
        {
            raftId = z.Raft.Id,
            measuredAt = $"{Yesterday:yyyy-MM-dd}T09:30:00",
            ph = 7.3m,
            salinity = 21m,
            temperature = 25m,
            reason = "Đọc nhầm giờ đo",
        });

        Assert.True(corrected.StatusCode == HttpStatusCode.OK, await corrected.Content.ReadAsStringAsync());
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().Where(m => m.Id == id).Select(m => m.MeasuredAt).SingleAsync());
        Assert.Equal(new DateTimeOffset(Yesterday.ToDateTime(new TimeOnly(9, 30)), Vietnam), stored);
        Assert.Equal(Vietnam, stored.Offset);
    }

    [Theory]
    [InlineData("\"20/10/2026 08:00\"")]
    [InlineData("\"2026-10-20T08:00:00 +07:00\"")]
    [InlineData("1729411200")]
    public async Task Time_that_is_not_iso_8601_is_MSG01_and_nothing_is_saved(string measuredAt)
    {
        var z = await api.ZoneAsync();
        var json = $"{{\"ph\":7.1,\"salinity\":20,\"temperature\":25,\"measuredAt\":{measuredAt}}}";

        var response = await z.Owner.PostAsync(z.Base, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public void Controllers_read_json_times_with_the_business_time_converter()
    {
        var json = api.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        Assert.Contains(json.Converters, c => c is BusinessTimeJsonConverter);
        var probe = JsonSerializer.Deserialize<TimeProbe>(
            "{\"at\":\"2026-10-20T08:00:00.5\",\"maybe\":null,\"utc\":\"2026-10-20T08:00:00Z\",\"west\":\"2026-10-20T08:00:00-05:00\"}", json)!;
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, 500, Vietnam), probe.At);
        Assert.Equal(Vietnam, probe.At.Offset);
        Assert.Null(probe.Maybe);
        Assert.Equal(TimeSpan.Zero, probe.Utc!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.Zero), probe.West);
        Assert.Equal(TimeSpan.FromHours(-5), probe.West!.Value.Offset);
        // Responses keep the stored offset.
        Assert.Contains("\"2026-10-20T08:00:00.5+07:00\"", JsonSerializer.Serialize(probe, json));
    }

    [Fact]
    public async Task Swagger_documents_the_rule_on_json_times()
    {
        var swagger = await api.CreateClient().GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        var json = swagger.GetProperty("components").GetProperty("schemas")
            .GetProperty("RecordWaterMeasurementRequest").GetProperty("properties").GetProperty("measuredAt");
        Assert.Equal("date-time", json.GetProperty("format").GetString());
        Assert.Contains("Vietnam time (UTC+7)", json.GetProperty("description").GetString());
    }

    private sealed record TimeProbe(DateTimeOffset At, DateTimeOffset? Maybe, DateTimeOffset? Utc, DateTimeOffset? West);
}
