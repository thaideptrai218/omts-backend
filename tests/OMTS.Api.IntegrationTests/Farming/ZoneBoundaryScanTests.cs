using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-02: the asynchronous scan of the licence map corner tables (202, then poll) through a scripted reader.</summary>
public class ZoneBoundaryScanTests
{
    private static string Start(WaterZone z) => $"/api/zones/{z.Zone.Id}/boundary-scans";

    private static string Poll(WaterZone z, Guid scanId) => $"{Start(z)}/{scanId}";

    [Fact]
    public async Task A_photo_is_accepted_with_202_and_the_poll_ends_Done_with_the_normalised_reading()
    {
        var reader = new ScriptedScanReader();
        using var api = new PhotoScanFactory(reader);
        var z = await api.ZoneAsync();
        var photo = WaterTestKit.Photo("licence-map");

        var scanId = await z.Owner.StartScanAsync(Start(z), photo);
        var body = await z.Owner.PollUntilDoneAsync(Poll(z, scanId));

        Assert.Equal("Done", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("errorCode").ValueKind);
        var reading = body.GetProperty("reading");
        Assert.Equal(107.75, reading.GetProperty("centralMeridian").GetDouble());
        Assert.Equal("VN-2000", reading.GetProperty("coordinateSystem").GetString());
        var zone = reading.GetProperty("zones")[0];
        Assert.Equal("Khu 1", zone.GetProperty("name").GetString());
        Assert.Equal(1.0, zone.GetProperty("writtenAreaHa").GetDouble());
        Assert.Equal(1.0, zone.GetProperty("gridAreaHa").GetDouble(), 6);
        Assert.True(zone.GetProperty("areaMatches").GetBoolean());
        var corner = zone.GetProperty("corners")[0];
        Assert.Equal("K1", corner.GetProperty("label").GetString());
        Assert.Equal(2315000.0, corner.GetProperty("x").GetDouble());
        Assert.Equal(438000.0, corner.GetProperty("y").GetDouble());
        Assert.Equal(JsonValueKind.Null, corner.GetProperty("lat").ValueKind);
        Assert.Equal(JsonValueKind.Null, corner.GetProperty("lon").ValueKind);
        // The reader got a copy of the photo as uploaded and its checked content type.
        Assert.Equal(photo, reader.ReceivedPhoto);
        Assert.Equal("image/png", reader.ReceivedMimeType);
    }

    [Fact]
    public async Task While_the_reader_works_the_poll_is_Pending_without_a_reading()
    {
        var gate = new TaskCompletionSource();
        using var api = new PhotoScanFactory(new ScriptedScanReader { Gate = gate.Task });
        var z = await api.ZoneAsync();

        var scanId = await z.Owner.StartScanAsync(Start(z));
        var pending = await (await z.Owner.GetAsync(Poll(z, scanId))).Content.ReadFromJsonAsync<JsonElement>();
        gate.SetResult();
        var done = await z.Owner.PollUntilDoneAsync(Poll(z, scanId));

        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("reading").ValueKind);
        Assert.Equal("Done", done.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("TIMEOUT")]
    [InlineData("UNAVAILABLE")]
    [InlineData("BAD_RESPONSE")]
    [InlineData("NOT_CONFIGURED")]
    public async Task A_failed_read_is_Failed_with_its_error_code_and_no_reading(string errorCode)
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader { ErrorCode = errorCode });
        var z = await api.ZoneAsync();

        var body = await z.Owner.PollUntilDoneAsync(Poll(z, await z.Owner.StartScanAsync(Start(z))));

        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal(errorCode, body.GetProperty("errorCode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reading").ValueKind);
    }

    [Fact]
    public async Task A_reader_that_throws_only_fails_its_own_scan_and_the_queue_keeps_working()
    {
        var reader = new ScriptedScanReader { Throws = new InvalidOperationException("boom with MODEL-TEXT") };
        using var api = new PhotoScanFactory(reader);
        var z = await api.ZoneAsync();

        var failed = await z.Owner.PollUntilDoneAsync(Poll(z, await z.Owner.StartScanAsync(Start(z))));
        var again = await z.Owner.PollUntilDoneAsync(Poll(z, await z.Owner.StartScanAsync(Start(z))));

        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("UNAVAILABLE", failed.GetProperty("errorCode").GetString());
        Assert.Equal("Failed", again.GetProperty("status").GetString()); // the worker survived to read the second one
        Assert.DoesNotContain(api.Logs.Lines, line => line.Contains("MODEL-TEXT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_account_cannot_read_the_scan_and_gets_404()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader());
        var z = await api.ZoneAsync();
        var scanId = await z.Owner.StartScanAsync(Start(z));
        await z.Owner.PollUntilDoneAsync(Poll(z, scanId));
        var other = await api.SecondOwnerAsync();

        var response = await other.GetAsync(Poll(z, scanId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_scan_is_only_found_through_its_own_zone_and_an_unknown_id_is_404()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader());
        var z = await api.ZoneAsync();
        var otherZone = await api.AddZoneAsync(AcsTestKit.NewCode());
        var scanId = await z.Owner.StartScanAsync(Start(z));

        var wrongZone = await z.Owner.GetAsync($"/api/zones/{otherZone.Id}/boundary-scans/{scanId}");
        var unknown = await z.Owner.GetAsync(Poll(z, Guid.NewGuid()));
        var asRaftForm = await z.Owner.GetAsync($"/api/rafts/form-scans/{scanId}");

        Assert.Equal(HttpStatusCode.NotFound, wrongZone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asRaftForm.StatusCode);
    }

    [Fact]
    public async Task A_scan_expires_after_15_minutes_and_is_then_404()
    {
        var clock = new MovableClock();
        using var api = new PhotoScanFactory(new ScriptedScanReader(), clock: clock);
        var z = await api.ZoneAsync();
        var scanId = await z.Owner.StartScanAsync(Start(z));
        await z.Owner.PollUntilDoneAsync(Poll(z, scanId));

        clock.Advance(TimeSpan.FromMinutes(14));
        var before = await z.Owner.GetAsync(Poll(z, scanId));
        clock.Advance(TimeSpan.FromMinutes(2));
        var after = await z.Owner.GetAsync(Poll(z, scanId));

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Only_the_cooperative_Owner_may_start_or_read_a_scan()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader());
        var z = await api.ZoneAsync();
        var (manager, _) = await WaterTestKit.StaffAsync(api, z.Zone);

        var start = await manager.PostPhotoAsync(Start(z));
        var read = await manager.GetAsync(Poll(z, Guid.NewGuid()));
        var anonymous = await api.CreateClient().PostPhotoAsync(Start(z));

        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_zone_that_is_not_In_Use_is_409_and_a_missing_zone_404()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader());
        var z = await api.ZoneAsync();
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        var discontinued = await z.Owner.PostPhotoAsync(Start(z));
        var missing = await z.Owner.PostPhotoAsync($"/api/zones/{Guid.NewGuid()}/boundary-scans");

        Assert.Equal(HttpStatusCode.Conflict, discontinued.StatusCode);
        Assert.Equal("MSG-GEN-09", await discontinued.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_missing_or_non_image_photo_is_MSG_GEN_01_on_Photo()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader());
        var z = await api.ZoneAsync();

        var none = await z.Owner.PostAsync(Start(z), new MultipartFormDataContent());
        var text = await z.Owner.PostPhotoAsync(Start(z), "not an image"u8.ToArray());
        var wrongPart = await z.Owner.PostPhotoAsync(Start(z), field: "file");

        // A request with no form part at all is refused by the framework before the action runs: 400 MSG-GEN-01 on "Request".
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("MSG-GEN-01", await none.ErrorCodeAsync());
        foreach (var response in new[] { text, wrongPart })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
            Assert.Contains("Photo", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_full_queue_is_503_OCR_BUSY_with_the_Vietnamese_message_and_frees_up_when_the_work_ends()
    {
        var gate = new TaskCompletionSource();
        var reader = new ScriptedScanReader { Gate = gate.Task };
        using var api = new PhotoScanFactory(reader, queueCapacity: 1, maxConcurrent: 1);
        var z = await api.ZoneAsync();
        var (second, third) = (await api.SecondOwnerAsync(), await api.SecondOwnerAsync());

        var running = await z.Owner.StartScanAsync(Start(z));
        await reader.Started; // the worker took it: the queue itself is empty again
        var queued = await second.StartScanAsync(Start(z));
        var refused = await third.PostPhotoAsync(Start(z));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("OCR_BUSY", await refused.ErrorCodeAsync());
        Assert.Equal("Máy chủ đang bận đọc ảnh, vui lòng thử lại sau ít phút.", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        gate.SetResult();
        Assert.Equal("Done", (await z.Owner.PollUntilDoneAsync(Poll(z, running))).GetProperty("status").GetString());
        Assert.Equal("Done", (await second.PollUntilDoneAsync(Poll(z, queued))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await third.PostPhotoAsync(Start(z))).StatusCode);
    }

    [Fact]
    public async Task One_account_has_one_scan_at_a_time_and_may_start_the_next_when_the_first_is_done()
    {
        var gate = new TaskCompletionSource();
        using var api = new PhotoScanFactory(new ScriptedScanReader { Gate = gate.Task });
        var z = await api.ZoneAsync();

        var first = await z.Owner.StartScanAsync(Start(z));
        var second = await z.Owner.PostPhotoAsync(Start(z));
        gate.SetResult();
        await z.Owner.PollUntilDoneAsync(Poll(z, first));
        var third = await z.Owner.PostPhotoAsync(Start(z));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal("OCR_BUSY", await second.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Accepted, third.StatusCode);
    }

    [Fact]
    public async Task A_waiting_scan_never_expires_before_it_runs_and_one_that_waited_over_120_seconds_fails_with_TIMEOUT_without_calling_Gemini()
    {
        var clock = new MovableClock();
        var gate = new TaskCompletionSource();
        var reader = new ScriptedScanReader { Gate = gate.Task };
        using var api = new PhotoScanFactory(reader, queueCapacity: 2, maxConcurrent: 1, clock: clock);
        var z = await api.ZoneAsync();
        var second = await api.SecondOwnerAsync();

        var running = await z.Owner.StartScanAsync(Start(z));
        await reader.Started;
        var waiting = await second.StartScanAsync(Start(z));
        clock.Advance(TimeSpan.FromMinutes(16)); // past the 15 minute result time, counted from the start: a Pending scan still answers
        var pending = await second.GetAsync(Poll(z, waiting));
        gate.SetResult();
        await z.Owner.PollUntilDoneAsync(Poll(z, running));
        var stale = await second.PollUntilDoneAsync(Poll(z, waiting));

        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal("Pending", (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("Failed", stale.GetProperty("status").GetString());
        Assert.Equal("TIMEOUT", stale.GetProperty("errorCode").GetString());
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task More_scans_an_hour_than_the_limit_is_429_and_polling_is_not_counted()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader(), zoneMapScansPerHour: 2);
        var z = await api.ZoneAsync();

        var first = await z.Owner.StartScanAsync(Start(z));
        await z.Owner.PollUntilDoneAsync(Poll(z, first));
        var second = await z.Owner.StartScanAsync(Start(z));
        await z.Owner.PollUntilDoneAsync(Poll(z, second));
        for (var i = 0; i < 5; i++)
        {
            await z.Owner.GetAsync(Poll(z, first));
        }

        var third = await z.Owner.PostPhotoAsync(Start(z));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Without_a_provider_the_scan_ends_Failed_NOT_CONFIGURED()
    {
        using var api = new PhotoScanFactory(provider: "");
        var z = await api.ZoneAsync();

        var body = await z.Owner.PollUntilDoneAsync(Poll(z, await z.Owner.StartScanAsync(Start(z))));

        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal("NOT_CONFIGURED", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task The_Fake_provider_of_Development_answers_a_made_up_map_so_the_app_can_be_tried()
    {
        using var api = new PhotoScanFactory();
        var z = await api.ZoneAsync();

        var body = await z.Owner.PollUntilDoneAsync(Poll(z, await z.Owner.StartScanAsync(Start(z))));

        Assert.Equal("Done", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("reading").GetProperty("zones")[0].GetProperty("areaMatches").GetBoolean());
    }
}
