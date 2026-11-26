using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.WaterQuality;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-04: the asynchronous scan of the raft form (same permission as creating a raft) through a scripted reader.</summary>
public class RaftFormScanTests
{
    private const string Start = "/api/rafts/form-scans";

    [Fact]
    public async Task A_form_photo_is_accepted_and_the_fields_come_back_trimmed_and_as_written()
    {
        var reader = new ScriptedScanReader();
        using var api = new PhotoScanFactory(reader);
        var z = await api.ZoneAsync();

        var scanId = await z.Owner.StartScanAsync(Start);
        var body = await z.Owner.PollUntilDoneAsync($"{Start}/{scanId}");

        Assert.Equal("Done", body.GetProperty("status").GetString());
        var reading = body.GetProperty("reading");
        Assert.Equal("B-07", reading.GetProperty("code").GetString());
        Assert.Equal("24 m x 10 m", reading.GetProperty("size").GetString());
        Assert.Equal("6", reading.GetProperty("laneCount").GetString());
        Assert.Equal("Gần cửa sông", reading.GetProperty("location").GetString());
        Assert.Equal(JsonValueKind.Null, reading.GetProperty("notes").ValueKind);
        Assert.Equal("10.1234", reading.GetProperty("latitude").GetString());
        Assert.Equal("106,5678", reading.GetProperty("longitude").GetString()); // not converted: the app validates it
        Assert.Equal("image/png", reader.ReceivedMimeType);
    }

    [Fact]
    public async Task A_failed_read_is_Failed_with_its_code()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader { ErrorCode = "BAD_RESPONSE" });
        var z = await api.ZoneAsync();

        var body = await z.Owner.PollUntilDoneAsync($"{Start}/{await z.Owner.StartScanAsync(Start)}");

        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal("BAD_RESPONSE", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Another_account_gets_404_and_an_unknown_id_is_404()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader());
        var z = await api.ZoneAsync();
        var scanId = await z.Owner.StartScanAsync(Start);
        var other = await api.SecondOwnerAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Start}/{scanId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await z.Owner.GetAsync($"{Start}/{Guid.NewGuid()}")).StatusCode);
        // A licence map scan id is not found through the raft form route either.
        var mapScan = await z.Owner.StartScanAsync($"/api/zones/{z.Zone.Id}/boundary-scans");
        Assert.Equal(HttpStatusCode.NotFound, (await z.Owner.GetAsync($"{Start}/{mapScan}")).StatusCode);
    }

    [Fact]
    public async Task Creating_a_raft_is_Owner_only_so_staff_and_anonymous_callers_are_refused()
    {
        using var api = new PhotoScanFactory(new ScriptedScanReader());
        var z = await api.ZoneAsync();
        var (manager, _) = await WaterTestKit.StaffAsync(api, z.Zone);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostPhotoAsync(Start)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{Start}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostPhotoAsync(Start)).StatusCode);
    }

    [Fact]
    public async Task A_missing_photo_is_400_and_a_full_queue_is_503()
    {
        var gate = new TaskCompletionSource();
        var reader = new ScriptedScanReader { Gate = gate.Task };
        using var api = new PhotoScanFactory(reader, queueCapacity: 1, maxConcurrent: 1);
        var z = await api.ZoneAsync();
        var (second, third) = (await api.SecondOwnerAsync(), await api.SecondOwnerAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await z.Owner.PostAsync(Start, new MultipartFormDataContent())).StatusCode);
        await z.Owner.StartScanAsync(Start);
        await reader.Started;
        await second.StartScanAsync(Start);
        var refused = await third.PostPhotoAsync(Start);
        gate.SetResult();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
    }
}
