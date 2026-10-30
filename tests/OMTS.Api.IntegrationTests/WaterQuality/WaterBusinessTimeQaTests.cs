using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of OMTS-214 item 2 (times without a UTC offset are Vietnam time). The test machine runs in UTC+7, so it cannot
/// tell "server local" from "Vietnam time" by the wall clock: every check asserts the stored offset (+07:00) and the
/// UTC instant of an offset-less value, and that "Z", "+00:00" and "+09:00" keep both their instant and their offset.
/// Also: the multipart path, invalid values (400 MSG01 naming the field, never 500), the API-wide reach of the global
/// converter (no other date-time inputs, responses unchanged).
/// </summary>
public class WaterBusinessTimeQaTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    // Two business days back: every wall-clock time below, in any offset used, is in the past.
    private static readonly DateOnly Day = TimeProvider.System.Today().AddDays(-2);

    public static TheoryData<string, int, int> Offsets => new()
    {
        // (suffix sent after "<day>T08:00:00", expected UTC hour on <day> (or the day before when negative), expected offset hours)
        { string.Empty, 1, 7 },
        { "Z", 8, 0 },
        { "+00:00", 8, 0 },
        { "+07:00", 1, 7 },
        { "+09:00", -1, 9 },
        { "-05:00", 13, -5 },
    };

    [Theory]
    [MemberData(nameof(Offsets))]
    public async Task Json_manual_entry_stores_the_instant_and_offset_and_the_detail_returns_them(string suffix, int utcHour, int offsetHours)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsJsonAsync(z.Base, new { clientOperationId = Guid.NewGuid(), measuredAt = $"{Day:yyyy-MM-dd}T08:00:00{suffix}", ph = 7.2m, salinity = 20m, confirm = true });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.ZoneId == z.Zone.Id));
        Assert.Equal(ExpectedUtc(utcHour), stored.MeasuredAt.UtcDateTime);
        Assert.Equal(TimeSpan.FromHours(offsetHours), stored.MeasuredAt.Offset);

        // The detail serializes the stored value with its own offset, as before the converter.
        var detail = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{stored.Id}");
        var text = detail.GetProperty("measuredAt").GetString()!;
        Assert.Equal(JsonSerializer.Serialize(stored.MeasuredAt).Trim('"'), text);
        Assert.Equal(TimeSpan.FromHours(offsetHours), DateTimeOffset.Parse(text).Offset);
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public async Task Multipart_entry_stores_the_same_instant_and_offset_as_json(string suffix, int utcHour, int offsetHours)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync($"{z.Base}/with-photo", Form($"{Day:yyyy-MM-dd}T08:00:00{suffix}"));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.ZoneId == z.Zone.Id));
        Assert.Equal(ExpectedUtc(utcHour), stored.MeasuredAt.UtcDateTime);
        Assert.Equal(TimeSpan.FromHours(offsetHours), stored.MeasuredAt.Offset);
    }

    // A date-only value and a 7-digit fraction are ISO 8601 too: midnight / the exact tick, in Vietnam time.
    [Fact]
    public void Json_reads_date_only_and_seven_digit_fractions_as_vietnam_time()
    {
        var json = MvcJson();

        var probe = JsonSerializer.Deserialize<TimeProbe>(
            "{\"at\":\"2026-10-20\",\"maybe\":\"2026-10-20T08:00:00.1234567\",\"many\":[\"2026-10-20T08:00:00\",\"2026-10-20T08:00:00Z\",null]}", json)!;

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, Vietnam), probe.At);
        Assert.Equal(Vietnam, probe.At.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, Vietnam).AddTicks(1234567), probe.Maybe);
        Assert.Equal(Vietnam, probe.Maybe!.Value.Offset);
        // Arrays and nullable elements use the same rule.
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero), probe.Many![0]);
        Assert.Equal(Vietnam, probe.Many[0]!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, probe.Many[1]!.Value.Offset);
        Assert.Null(probe.Many[2]);
    }

    // Responses must not change format: the converter writes exactly what System.Text.Json wrote before.
    [Fact]
    public void Json_writes_date_times_exactly_like_the_default_serializer()
    {
        var mvc = MvcJson();
        var plain = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        DateTimeOffset[] values =
        [
            new(2026, 10, 20, 8, 0, 0, Vietnam),
            new DateTimeOffset(2026, 10, 20, 8, 0, 0, Vietnam).AddTicks(1234567),
            new(2026, 10, 20, 8, 0, 0, 500, TimeSpan.Zero),
            new(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(-5)),
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
        ];

        foreach (var value in values)
        {
            Assert.Equal(JsonSerializer.Serialize(value, plain), JsonSerializer.Serialize(value, mvc));
            Assert.Equal(JsonSerializer.Serialize<DateTimeOffset?>(value, plain), JsonSerializer.Serialize<DateTimeOffset?>(value, mvc));
        }

        Assert.Equal("null", JsonSerializer.Serialize<DateTimeOffset?>(null, mvc));
    }

    // The default serializer answered 400 MSG01 for these; the converter must too, and name the field exactly as
    // for any other unreadable JSON value (an unreadable pH names "request" too: pre-existing, see the QA report).
    [Theory]
    [InlineData("\"2026-02-30T08:00:00\"")]
    [InlineData("\"2026-10-20T25:00:00\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("{}")]
    public async Task Json_unreadable_time_is_400_MSG01_like_any_unreadable_value(string measuredAt)
    {
        var z = await api.ZoneAsync();

        var response = await PostRawAsync(z, measuredAt);
        var unreadableNumber = await z.Owner.PostAsync(z.Base, new StringContent(
            $"{{\"clientOperationId\":\"{Guid.NewGuid()}\",\"ph\":\"seven\",\"salinity\":20,\"confirm\":true}}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
        Assert.Equal(await FieldAsync(unreadableNumber), await FieldAsync(response));
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    // BUG: "0001-01-01T00:00:00" (the default DateTime of many clients) has no UTC instant once read as UTC+7
    // (0000-12-31T17:00Z), BusinessClock.FromLocal throws ArgumentOutOfRangeException and the request answers 500.
    [Theory]
    [InlineData("\"0001-01-01T00:00:00\"")]
    [InlineData("\"0001-01-01T06:59:59\"")]
    public async Task Json_offset_less_time_before_the_first_utc_instant_is_400_MSG01_not_500(string measuredAt)
    {
        var z = await api.ZoneAsync();

        var response = await PostRawAsync(z, measuredAt);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
    }

    // Same root cause on the multipart path (InvariantFormValues → BusinessClock.FromLocal).
    [Fact]
    public async Task Multipart_offset_less_time_before_the_first_utc_instant_is_400_MSG01_not_500()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync($"{z.Base}/with-photo", Form("0001-01-01T00:00:00"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
    }

    // The multipart field is documented as ISO 8601 (Swagger, README) and the JSON body refuses other formats, but
    // DateTime.TryParse(InvariantCulture) reads "03/04/2026 08:00" as 4 March (US order); a Vietnamese user means
    // 3 April. Pre-existing leniency, now inconsistent with the JSON rule this PR documents.
    [Theory]
    [InlineData("03/04/2026 08:00")]
    [InlineData("Oct 20 2026 08:00")]
    public async Task Multipart_time_that_is_not_iso_8601_is_400_MSG01_like_json(string measuredAt)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync($"{z.Base}/with-photo", Form(measuredAt));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
    }

    // BusinessTimeSetup claims no endpoint takes a date and time from the query, the route, a header or a bound form
    // field (those would be read as server-local time by MVC model binding, outside the converter). Guard it.
    [Fact]
    public void No_endpoint_binds_a_date_time_outside_the_json_body()
    {
        var descriptions = api.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .ToList();
        Assert.NotEmpty(descriptions);

        var outsideBody = descriptions
            .SelectMany(d => d.ParameterDescriptions.Select(p => (d.HttpMethod, d.RelativePath, p.Name, p.Type, p.Source)))
            .Where(p => p.Source != BindingSource.Body && IsDateTime(p.Type))
            .Select(p => $"{p.HttpMethod} {p.RelativePath}: {p.Name} ({p.Source.Id})")
            .ToList();

        Assert.Empty(outsideBody);
    }

    // The converter handles DateTimeOffset only: a DateTime in a request body would still be read without the rule.
    [Fact]
    public void No_json_request_body_carries_a_date_time_the_converter_does_not_handle()
    {
        var bodies = api.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .SelectMany(d => d.ParameterDescriptions)
            .Where(p => p.Source == BindingSource.Body && p.Type is not null)
            .Select(p => p.Type!)
            .Distinct()
            .ToList();
        Assert.NotEmpty(bodies);

        var found = new List<string>();
        foreach (var body in bodies)
        {
            Walk(body, body.Name, found, new HashSet<Type>());
        }

        Assert.Empty(found);
    }

    private static void Walk(Type type, string path, List<string> found, HashSet<Type> seen)
    {
        var plain = Nullable.GetUnderlyingType(type) ?? type;
        if (plain == typeof(DateTime))
        {
            found.Add(path);
            return;
        }

        if (plain.IsPrimitive || plain.IsEnum || plain == typeof(string) || plain == typeof(decimal) || plain == typeof(Guid)
            || plain == typeof(DateTimeOffset) || plain == typeof(DateOnly) || plain == typeof(TimeOnly) || !seen.Add(plain))
        {
            return;
        }

        if (plain.IsArray)
        {
            Walk(plain.GetElementType()!, $"{path}[]", found, seen);
            return;
        }

        if (plain.IsGenericType)
        {
            foreach (var argument in plain.GetGenericArguments())
            {
                Walk(argument, $"{path}<{argument.Name}>", found, seen);
            }

            if (plain.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                return;
            }
        }

        if (plain.Namespace?.StartsWith("OMTS", StringComparison.Ordinal) != true)
        {
            return;
        }

        foreach (var property in plain.GetProperties())
        {
            Walk(property.PropertyType, $"{path}.{property.Name}", found, seen);
        }
    }

    private static bool IsDateTime(Type? type)
    {
        var plain = type is null ? null : Nullable.GetUnderlyingType(type) ?? type;
        return plain == typeof(DateTime) || plain == typeof(DateTimeOffset);
    }

    private static DateTime ExpectedUtc(int utcHour) =>
        utcHour >= 0
            ? Day.ToDateTime(new TimeOnly(utcHour, 0), DateTimeKind.Utc)
            : Day.AddDays(-1).ToDateTime(new TimeOnly(24 + utcHour, 0), DateTimeKind.Utc);

    private JsonSerializerOptions MvcJson() => api.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

    private static Task<HttpResponseMessage> PostRawAsync(WaterZone z, string measuredAt) =>
        z.Owner.PostAsync(z.Base, new StringContent(
            $"{{\"clientOperationId\":\"{Guid.NewGuid()}\",\"ph\":7.1,\"salinity\":20,\"measuredAt\":{measuredAt},\"confirm\":true}}",
            System.Text.Encoding.UTF8,
            "application/json"));

    private static async Task<string?> FieldAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("field", out var field) ? field.GetString() : null;
    }

    private static MultipartFormDataContent Form(string measuredAt) => new()
    {
        { new StringContent(Guid.NewGuid().ToString()), "clientOperationId" },
        { new StringContent(measuredAt), "measuredAt" },
        { new StringContent("7.2"), "ph" },
        { new StringContent("20"), "salinity" },
        { new StringContent("true"), "confirm" },
    };

    private sealed record TimeProbe(DateTimeOffset At, DateTimeOffset? Maybe, DateTimeOffset?[]? Many);
}
