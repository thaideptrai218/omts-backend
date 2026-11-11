using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;

/// <summary>A session as a client sends it; a correction also has the reason and the version.</summary>
public sealed record SeedingBody(DateOnly? SeedingDate, Guid? SeedLotId, IReadOnlyList<int>? LaneNumbers, decimal? Quantity, string? Reason = null, string? RowVersion = null);

/// <summary>A seed lot made through the API: a Seed line of a Completed receipt.</summary>
public sealed record SeedLotMade(Guid LineId, Guid ReceiptId, string Code, Guid ItemId, Guid SupplierId, JsonElement Receipt);

/// <summary>CYCLE-02 calls and the seed lots they need.</summary>
public static class SeedingTestKit
{
    /// <summary>A Seed line of a Completed receipt, by the caller, received ten days ago unless the test says otherwise. A cycle of <see cref="CultivationLogTestKit.CycleAsync"/> starts on that day too.</summary>
    public static async Task<SeedLotMade> SeedLotAsync(this HttpClient client, decimal quantity = 100m, DateOnly? received = null, Guid? raftId = null, bool complete = true)
    {
        var supplierId = await client.CreateSupplierAsync();
        var itemId = await client.CreateItemAsync("Seed", "kg");
        var receipt = await client.CreateReceiptAsync(new ReceiptBody
        {
            SupplierId = supplierId,
            ReceiptDate = received ?? FarmingTestKit.Today.AddDays(-10),
            Lines = [new LineBody(itemId, quantity, 1_000m, RaftId: raftId)],
            Complete = complete,
        });
        return new SeedLotMade(receipt.LineOf(itemId), receipt.Id(), $"{receipt.Text("code")}-1", itemId, supplierId, receipt);
    }

    /// <summary>A session yesterday of ten of the lot on lanes 1 and 2, unless the test says otherwise.</summary>
    public static SeedingBody Session(this SeedLotMade lot, IReadOnlyList<int>? lanes = null, DateOnly? date = null, decimal quantity = 10m) =>
        new(date ?? FarmingTestKit.Today.AddDays(-1), lot.LineId, lanes ?? [1, 2], quantity);

    public static Task<HttpResponseMessage> PostSeedingAsync(this HttpClient client, Guid cycleId, SeedingBody body) =>
        client.PostAsJsonAsync($"/api/cultivation-cycles/{cycleId}/seeding-records", body);

    /// <summary>Records a session and returns the whole answer.</summary>
    public static async Task<JsonElement> RecordSeedingAsync(this HttpClient client, Guid cycleId, SeedingBody body) =>
        await (await client.PostSeedingAsync(cycleId, body)).ReadAsync(HttpStatusCode.Created);

    /// <summary>Records a session and returns the saved session, the <c>record</c> of the answer.</summary>
    public static async Task<JsonElement> SeedingRecordAsync(this HttpClient client, Guid cycleId, SeedingBody body) =>
        (await client.RecordSeedingAsync(cycleId, body)).GetProperty("record");

    public static Task<HttpResponseMessage> PutSeedingAsync(this HttpClient client, Guid recordId, SeedingBody body) =>
        client.PutAsJsonAsync($"/api/seeding-records/{recordId}", body);

    /// <summary>The saved session as a correction with the reason: the test changes what it corrects.</summary>
    public static SeedingBody AsCorrection(this JsonElement record, string? reason = "Wrong entry") => new(
        record.Date("seedingDate"),
        record.GetProperty("seedLot").Id(),
        record.Lanes(),
        record.GetProperty("quantity").GetDecimal(),
        reason,
        record.Text("rowVersion"));

    public static Task<HttpResponseMessage> RemoveSeedingAsync(this HttpClient client, Guid recordId, string? reason = "Recorded by mistake", string? rowVersion = "") =>
        client.PostAsJsonAsync($"/api/seeding-records/{recordId}/remove", new { reason, rowVersion });

    public static Task<JsonElement> SeedingListAsync(this HttpClient client, Guid cycleId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}/seeding-records");

    public static Task<JsonElement> GetSeedingAsync(this HttpClient client, Guid recordId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/seeding-records/{recordId}");

    public static Task<JsonElement> SeedLotsAsync(this HttpClient client, Guid raftId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/seed-lots");

    public static Task<HttpResponseMessage> CompletionPreviewAsync(this HttpClient client, Guid cycleId) =>
        client.GetAsync($"/api/cultivation-cycles/{cycleId}/seeding-complete/preview");

    public static Task<HttpResponseMessage> PostSeedingCompleteAsync(this HttpClient client, Guid cycleId, string? rowVersion = "") =>
        client.PostAsJsonAsync($"/api/cultivation-cycles/{cycleId}/seeding-complete", new { rowVersion });

    /// <summary>Marks the seeding complete with the current version of the cycle and returns the answer.</summary>
    public static async Task<JsonElement> MarkSeedingCompleteAsync(this HttpClient client, Guid cycleId)
    {
        var list = await client.SeedingListAsync(cycleId);
        // A raft with no seeded lane needs the confirmation of the owner decision of 11/11/2026; a seeded one ignores the flag.
        return await (await client.PostAsJsonAsync($"/api/cultivation-cycles/{cycleId}/seeding-complete", new { rowVersion = list.Text("cycleRowVersion"), confirmEmpty = true })).ReadAsync(HttpStatusCode.OK);
    }

    /// <summary>A return of part of the lot, by the Owner; answers the receipt.</summary>
    public static async Task<JsonElement> ReturnLotAsync(this HttpClient owner, SeedLotMade lot, decimal quantity) =>
        await (await owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, quantity)] })).ReadAsync(HttpStatusCode.Created);

    public static List<int> Lanes(this JsonElement record) => record.GetProperty("laneNumbers").EnumerateArray().Select(n => n.GetInt32()).ToList();

    public static List<int> Numbers(this JsonElement element, string property) => element.GetProperty(property).EnumerateArray().Select(n => n.GetInt32()).ToList();

    /// <summary>The state of a lane on the Seeding screen.</summary>
    public static string StateOf(this JsonElement list, int laneNumber) =>
        list.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == laneNumber).Text("state");

    public static Task<List<Notification>> SeedingNoticesAsync(this AuthApiFactory factory, Guid cycleId) =>
        factory.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == cycleId && n.Type == "CYCLE_SEEDING_COMPLETED").ToListAsync());
}
