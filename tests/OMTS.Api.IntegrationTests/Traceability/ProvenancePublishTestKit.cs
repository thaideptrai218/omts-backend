using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-01 calls, the data a test breaks to make a lot miss origin information (BR-TRACE-01), and what a publish leaves behind.</summary>
public static class ProvenancePublishTestKit
{
    public const string PublishedAction = "HarvestLot.Published";
    public const string RepublishedAction = "HarvestLot.Republished";

    public static Task<HttpResponseMessage> GetPublishCheckAsync(this HttpClient client, Guid lotId) => client.GetAsync($"/api/harvest-lots/{lotId}/publish-check");

    public static async Task<JsonElement> PublishCheckAsync(this HttpClient client, Guid lotId) => await (await client.GetPublishCheckAsync(lotId)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> PostPublishAsync(this HttpClient client, Guid lotId) => client.PostAsync($"/api/harvest-lots/{lotId}/publish", null);

    public static Task<HttpResponseMessage> GetInvoicePublishCheckAsync(this HttpClient client, Guid invoiceId) => client.GetAsync($"/api/sales-invoices/{invoiceId}/publish-check");

    /// <summary>
    /// Removes lane <paramref name="laneNumber"/> of the raft from every seeding session, as a record changed outside the rules would: the lots holding the lane then miss its seed lot.
    /// </summary>
    public static Task LeaveLaneWithoutSeedLotAsync(this WebApplicationFactory<Program> api, Guid raftId, int laneNumber) => api.DbAsync(async db =>
    {
        db.SeedingRecordLanes.RemoveRange(await db.SeedingRecordLanes.Where(s => s.Lane.RaftId == raftId && s.Lane.LaneNumber == laneNumber).ToListAsync());
        return await db.SaveChangesAsync();
    });

    /// <summary>Marks the receipt of the raft's seed lot Cancelled, as no screen allows once it is seeded (GBR-04), and answers the seed lot code.</summary>
    public static Task<string> CancelSeedReceiptAsync(this WebApplicationFactory<Program> api, Guid raftId) => api.DbAsync(async db =>
    {
        var lineId = await db.SeedingRecords.Where(s => s.Cycle.RaftId == raftId).Select(s => s.SeedLotLineId).FirstAsync();
        var line = await db.PurchaseReceiptLines.Include(l => l.Receipt).SingleAsync(l => l.Id == lineId);
        line.Receipt.StatusId = StatusIds.PurchaseReceipt.Cancelled;
        await db.SaveChangesAsync();
        return $"{line.Receipt.Code}-{line.LineNumber}";
    });

    /// <summary>Cancels the invoice in the database, as SALE-08 will.</summary>
    public static Task CancelInvoiceAsync(this WebApplicationFactory<Program> api, Guid invoiceId) => api.DbAsync(async db =>
    {
        var invoice = await db.SalesInvoices.SingleAsync(i => i.Id == invoiceId);
        invoice.StatusId = StatusIds.SalesInvoice.Cancelled;
        return await db.SaveChangesAsync();
    });

    /// <summary>The publish audit rows of the lot (Published and Republished), oldest first.</summary>
    public static Task<List<AuditEvent>> PublishAuditAsync(this WebApplicationFactory<Program> api, Guid lotId) => api.DbAsync(db => db.AuditEvents.AsNoTracking()
        .Where(e => e.RecordId == lotId && (e.Action == PublishedAction || e.Action == RepublishedAction)).OrderBy(e => e.OccurredAt).ToListAsync());

    /// <summary>The lots of an answer (finish or invoice check) by lot id.</summary>
    public static Dictionary<Guid, JsonElement> LotsById(this JsonElement answer) =>
        answer.GetProperty("lots").EnumerateArray().ToDictionary(l => l.GetProperty("harvestLotId").GetGuid());

    /// <summary>The missing items (MSG-TRACE-01) in short: kind, then the lane or the zone, then the seed lots, e.g. "NoSeedingSession 2".</summary>
    public static List<string> Missing(this JsonElement element, string property = "missing") => element.GetProperty(property).EnumerateArray()
        .Select(e => string.Join(' ', new[]
        {
            e.GetProperty("kind").GetString(),
            e.GetProperty("laneNumber").ValueKind == JsonValueKind.Null ? e.GetProperty("zoneCode").GetString() : e.GetProperty("laneNumber").GetInt32().ToString(),
        }.Concat(e.GetProperty("seedLotCodes").EnumerateArray().Select(c => c.GetString()))))
        .ToList();
}
