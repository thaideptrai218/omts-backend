using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>A raft whose lanes 1 to 5 have seed since yesterday, one lot of lane 1 today and the Zone Manager of the zone; the Owner recorded everything.</summary>
public sealed record TracedRaft(Actor Owner, Actor Manager, Guid ZoneId, Guid RaftId, Guid CycleId, SeedLotMade Lot, JsonElement Session, JsonElement HarvestLot)
{
    public Guid SessionId => Session.Id();
    public Guid LotId => HarvestLot.Id();
    public string LotCode => HarvestLot.Text("lotCode");
}

/// <summary>TRACE-03 calls and what they should have left behind.</summary>
public static class TraceCorrectionTestKit
{
    public const string Reason = "The Owner checked the receipt: wrong entry";
    public const string SeedingAction = "SeedingRecord.TraceCorrected";
    public const string LogAction = "CultivationLog.TraceCorrected";
    public const string LotAction = "HarvestLot.TraceCorrected";

    /// <summary>A session of lanes 1 to 5 yesterday, and lane 1 harvested today, so the session can be corrected only in TRACE-03 (BR-CYCLE-02).</summary>
    public static async Task<TracedRaft> TracedRaftAsync(this AuthApiFactory factory)
    {
        var raft = await factory.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var manager = await factory.ManagerAsync(raft.ZoneId);
        return new TracedRaft(raft.Owner, manager, raft.ZoneId, raft.RaftId, raft.CycleId, raft.Lot, raft.Session, lot);
    }

    /// <summary>The session as a trace correction: the test changes what it corrects.</summary>
    public static object SeedingCorrection(
        this JsonElement session, DateOnly? date = null, IReadOnlyList<int>? lanes = null, decimal? quantity = null, Guid? seedLotId = null, string? reason = Reason, bool ownerApproved = false,
        string? rowVersion = null) => new
    {
        seedingDate = date ?? session.Date("seedingDate"),
        seedLotId = seedLotId ?? session.GetProperty("seedLot").Id(),
        laneNumbers = lanes ?? session.Lanes(),
        quantity = quantity ?? session.GetProperty("quantity").GetDecimal(),
        reason,
        rowVersion = rowVersion ?? session.Text("rowVersion"),
        ownerApproved,
    };

    public static Task<HttpResponseMessage> PreviewSeedingAsync(this HttpClient client, Guid recordId, object body) =>
        client.PostAsJsonAsync($"/api/seeding-records/{recordId}/trace-correction/preview", body);

    public static Task<HttpResponseMessage> PutSeedingTraceAsync(this HttpClient client, Guid recordId, object body) =>
        client.PutAsJsonAsync($"/api/seeding-records/{recordId}/trace-correction", body);

    public static Task<HttpResponseMessage> PreviewLogAsync(this HttpClient client, Guid logId) => client.GetAsync($"/api/cultivation-logs/{logId}/trace-correction/preview");

    public static Task<HttpResponseMessage> PutLogTraceAsync(this HttpClient client, Guid logId, LogFields fields, bool ownerApproved = false)
    {
        var form = fields.ToForm();
        form.Add(new StringContent(ownerApproved ? "true" : "false"), "ownerApproved");
        return client.PutAsync($"/api/cultivation-logs/{logId}/trace-correction", form);
    }

    public static Task<HttpResponseMessage> PreviewLotAsync(this HttpClient client, Guid lotId, object body) =>
        client.PostAsJsonAsync($"/api/harvest-lots/{lotId}/trace-correction/preview", body);

    public static Task<HttpResponseMessage> PutLotTraceAsync(this HttpClient client, Guid lotId, HarvestCorrection correction, bool ownerApproved = false)
    {
        var form = correction.ToForm();
        form.Add(new StringContent(ownerApproved ? "true" : "false"), "ownerApproved");
        return client.PutAsync($"/api/harvest-lots/{lotId}/trace-correction", form);
    }

    /// <summary>Sends a lot correction with a version issued now, as the in-memory database issues none.</summary>
    public static async Task<HttpResponseMessage> SendLotTraceAsync(
        this AuthApiFactory factory, HttpClient client, Guid lotId, HarvestFields fields, bool ownerApproved = false, IReadOnlyList<string>? keep = null, string? reason = Reason)
    {
        // Every lot a test records has a photo (a new lot needs 1 to 3): a correction that names none keeps what the lot has; an explicit empty list removes them all.
        keep ??= await HarvestLotCorrectionTestKit.KeysOfAsync(client, lotId);
        return await client.PutLotTraceAsync(lotId, new HarvestCorrection(fields, reason, await factory.LotVersionAsync(lotId), keep), ownerApproved);
    }

    public static Task<JsonElement> TraceCyclesAsync(this HttpClient client, Guid raftId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/trace-corrections");

    public static Task<JsonElement> TraceRecordsAsync(this HttpClient client, Guid cycleId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}/trace-corrections");

    /// <summary>The lot as the Trace lot screen will have published it (T1 builds the publishing): a publish time, and a later withdrawal time when <paramref name="withdrawn"/>.</summary>
    public static Task PublishLotAsync(this AuthApiFactory factory, Guid lotId, bool withdrawn = false) => factory.UpdateAsync<HarvestLot>(lotId, lot =>
    {
        lot.PublishedAt = DateTimeOffset.UtcNow.AddHours(-2);
        lot.WithdrawnAt = withdrawn ? DateTimeOffset.UtcNow.AddHours(-1) : null;
        lot.WithdrawalReason = withdrawn ? "Checked with the trader" : null;
    });

    public static Task<HarvestLot> LotRowAsync(this AuthApiFactory factory, Guid lotId) =>
        factory.QueryAsync(db => db.HarvestLots.AsNoTracking().Include(l => l.Lanes).Include(l => l.Photos).SingleAsync(l => l.Id == lotId));

    public static Task<List<AuditEvent>> AuditRowsAsync(this AuthApiFactory factory, string action, Guid recordId) =>
        factory.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.Action == action && e.RecordId == recordId).ToListAsync());

    public static List<string> Strings(this JsonElement element, string property) => element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToList();

    /// <summary>The state of a lane on the progress view of a cycle (on its close date when it is Closed).</summary>
    public static async Task<string> LaneStateAsync(this HttpClient client, Guid raftId, Guid cycleId, int laneNumber) =>
        (await client.ProgressAsync(raftId, $"?cycleId={cycleId}")).GetProperty("lanes").EnumerateArray()
            .Single(l => l.GetProperty("laneNumber").GetInt32() == laneNumber).Text("state");

    /// <summary>Asserts a 409 MSG-TRACE-04 asking for the Owner's agreement, and returns it.</summary>
    public static async Task<JsonElement> AgreementRefusalAsync(this HttpResponseMessage answer)
    {
        var refused = await answer.ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-TRACE-04", "ownerApproved"), (refused.Text("code"), refused.Text("confirmFlag")));
        return refused;
    }
}
