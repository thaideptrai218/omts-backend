using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The multipart fields of a nursery start (NURSERY-01); by default 1000 pieces today and one JPEG.</summary>
public sealed record NurseryFields
{
    public Guid? SeedLotId { get; init; }
    public string? Quantity { get; init; } = "1000";
    public string? StartDate { get; init; }
    public string? Note { get; init; }
    public string? RowVersion { get; init; }
    public IReadOnlyList<Guid>? KeepPhotoIds { get; init; }
    public IReadOnlyList<byte[]> Photos { get; init; } = [FarmingTestKit.Jpeg];

    public MultipartFormDataContent ToForm()
    {
        var form = new MultipartFormDataContent();
        void Add(string? value, string name)
        {
            if (value is not null)
            {
                form.Add(new StringContent(value), name);
            }
        }

        Add(SeedLotId?.ToString(), "seedLotId");
        Add(Quantity, "quantity");
        Add(StartDate, "startDate");
        Add(Note, "note");
        Add(RowVersion, "rowVersion");
        foreach (var id in KeepPhotoIds ?? [])
        {
            form.Add(new StringContent(id.ToString()), "keepPhotoIds");
        }

        foreach (var photo in Photos)
        {
            form.Add(FarmingTestKit.FileContent(photo, "image/jpeg"), "photos", "nursery.jpg");
        }

        return form;
    }
}

/// <summary>A share of the survivors as the completion sends it.</summary>
public sealed record Share(Guid RaftId, decimal Quantity, decimal? TargetYieldKg = null);

/// <summary>A zone with two rafts of five lanes and a seed lot of 10 000 pieces, by a new Owner (NURSERY-01 to NURSERY-04).</summary>
public sealed record NurseryWorld(Actor Owner, Guid ZoneId, Guid Raft1, Guid Raft2, SeedLotMade Lot);

/// <summary>Nursery calls and what they should have left behind.</summary>
public static class NurseryTestKit
{
    public static async Task<NurseryWorld> NurseryWorldAsync(this AuthApiFactory factory, decimal lotQuantity = 10_000m)
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raft1 = (await owner.Client.CreateRaftAsync(zoneId)).Id();
        var raft2 = (await owner.Client.CreateRaftAsync(zoneId)).Id();
        return new NurseryWorld(owner, zoneId, raft1, raft2, await owner.Client.SeedLotAsync(lotQuantity));
    }

    public static Task<HttpResponseMessage> PostNurseryAsync(this HttpClient client, Guid zoneId, NurseryFields fields) =>
        client.PostAsync($"/api/zones/{zoneId}/nursery-batches", fields.ToForm());

    /// <summary>Starts a nursery of the lot (default 1000 pieces) and returns the saved batch.</summary>
    public static async Task<JsonElement> StartNurseryAsync(this HttpClient client, Guid zoneId, SeedLotMade lot, string quantity = "1000") =>
        (await (await client.PostNurseryAsync(zoneId, new NurseryFields { SeedLotId = lot.LineId, Quantity = quantity })).ReadAsync(HttpStatusCode.Created)).GetProperty("batch");

    public static Task<HttpResponseMessage> PutNurseryAsync(this HttpClient client, Guid batchId, NurseryFields fields) =>
        client.PutAsync($"/api/nursery-batches/{batchId}", fields.ToForm());

    public static async Task<HttpResponseMessage> CancelNurseryAsync(this HttpClient client, AuthApiFactory factory, Guid batchId, string? reason) =>
        await client.PostAsJsonAsync($"/api/nursery-batches/{batchId}/cancel", new { reason, rowVersion = await factory.VersionOfAsync(batchId) });

    /// <summary>The completion form; the version is the one the test issued on the row, as the in-memory database issues none.</summary>
    public static async Task<HttpResponseMessage> PostCompleteAsync(
        this HttpClient client, AuthApiFactory factory, Guid batchId, string? survivors, IReadOnlyList<Share>? shares, IReadOnlyList<byte[]>? photos = null, string? completedOn = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(await factory.IssueRowVersionAsync<NurseryBatch>(batchId)), "rowVersion" },
        };
        if (survivors is not null)
        {
            form.Add(new StringContent(survivors), "survivors");
        }

        if (completedOn is not null)
        {
            form.Add(new StringContent(completedOn), "completedOn");
        }

        if (shares is not null)
        {
            form.Add(new StringContent(JsonSerializer.Serialize(shares.Select(s => new { raftId = s.RaftId, quantity = s.Quantity, targetYieldKg = s.TargetYieldKg }))), "allocations");
        }

        foreach (var photo in photos ?? [FarmingTestKit.Jpeg])
        {
            form.Add(FarmingTestKit.FileContent(photo, "image/jpeg"), "photos", "done.jpg");
        }

        return await client.PostAsync($"/api/nursery-batches/{batchId}/complete", form);
    }

    /// <summary>Completes a batch and returns the saved batch.</summary>
    public static async Task<JsonElement> CompleteNurseryAsync(this HttpClient client, AuthApiFactory factory, Guid batchId, string survivors, params Share[] shares) =>
        (await (await client.PostCompleteAsync(factory, batchId, survivors, shares)).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

    /// <summary>The version stored on the row now: the one a completion form issued, or none (empty) when no form did, as the in-memory database issues none itself.</summary>
    public static async Task<string> VersionOfAsync(this AuthApiFactory factory, Guid batchId) =>
        Convert.ToBase64String(await factory.QueryAsync(db => db.NurseryBatches.Where(b => b.Id == batchId).Select(b => b.RowVersion).SingleAsync()));

    public static async Task<HttpResponseMessage> PostSurvivorsAsync(
        this HttpClient client, AuthApiFactory factory, Guid batchId, decimal? survivors, string? reason, IReadOnlyList<Share>? shares = null) =>
        await client.PostAsJsonAsync(
            $"/api/nursery-batches/{batchId}/survivors",
            new { survivors, reason, rowVersion = await factory.VersionOfAsync(batchId), allocations = shares?.Select(s => new { raftId = s.RaftId, quantity = s.Quantity, targetYieldKg = s.TargetYieldKg }) });

    public static async Task<HttpResponseMessage> PostAllocationsAsync(
        this HttpClient client, AuthApiFactory factory, Guid batchId, IReadOnlyList<Share> shares, string? reason = "Moved") =>
        await client.PostAsJsonAsync(
            $"/api/nursery-batches/{batchId}/allocations",
            new { allocations = shares.Select(s => new { raftId = s.RaftId, quantity = s.Quantity, targetYieldKg = s.TargetYieldKg }), reason, rowVersion = await factory.VersionOfAsync(batchId) });

    public static Task<JsonElement> GetNurseryAsync(this HttpClient client, Guid batchId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches/{batchId}");

    public static Task<JsonElement> NurseryHistoryAsync(this HttpClient client, Guid batchId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches/{batchId}/history");

    public static Task<JsonElement> CycleViewAsync(this HttpClient client, Guid cycleId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}");

    /// <summary>A number of an answer (pieces or percentages), read as a decimal.</summary>
    public static decimal Pieces(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    /// <summary>The allocation of a raft in a batch answer.</summary>
    public static JsonElement AllocationOf(this JsonElement batch, Guid raftId) =>
        batch.GetProperty("allocations").EnumerateArray().Single(a => a.GetProperty("raftId").GetGuid() == raftId);

    public static Task<List<NurseryBatch>> NurseryBatchesAsync(this AuthApiFactory factory, Guid zoneId) =>
        factory.QueryAsync(db => db.NurseryBatches.Where(b => b.ZoneId == zoneId).ToListAsync());

    public static Task<List<CultivationCycle>> NurseryCyclesAsync(this AuthApiFactory factory, Guid batchId) =>
        factory.QueryAsync(db => db.CultivationCycles.Where(c => c.NurseryBatchId == batchId).OrderBy(c => c.RaftId).ToListAsync());

    public static Task<List<Notification>> NurseryNoticesAsync(this AuthApiFactory factory, Guid batchId) =>
        factory.QueryAsync(db => db.Notifications.Where(n => n.Type == "NURSERY_RECORDED" && n.RelatedRecordId == batchId).ToListAsync());

    /// <summary>A session from the nursery cycle: no seed lot is named, and no quantity unless the test sends one (the session hangs the whole share, owner decision of 13/11/2026).</summary>
    public static SeedingBody NurserySession(IReadOnlyList<int> lanes, decimal? quantity = null) => new(FarmingTestKit.Today, null, lanes, quantity);
}
