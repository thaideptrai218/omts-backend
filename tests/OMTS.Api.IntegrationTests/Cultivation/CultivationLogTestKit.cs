using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The multipart fields of a cultivation log; by default one JPEG visited today.</summary>
public sealed record LogFields
{
    public DateOnly? VisitDate { get; init; } = FarmingTestKit.Today;
    public string? VisitDateText { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> KeepPhotoKeys { get; init; } = [];
    public string? Reason { get; init; }
    public string? RowVersion { get; init; }
    public IReadOnlyList<byte[]> Photos { get; init; } = [FarmingTestKit.Jpeg];
    public string PhotoField { get; init; } = "photos";

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

        Add(VisitDateText ?? VisitDate?.ToString("yyyy-MM-dd"), "visitDate");
        Add(Notes, "notes");
        Add(Reason, "reason");
        Add(RowVersion, "rowVersion");
        foreach (var key in KeepPhotoKeys)
        {
            form.Add(new StringContent(key), "keepPhotoKeys");
        }

        foreach (var photo in Photos)
        {
            form.Add(FarmingTestKit.FileContent(photo, "image/jpeg"), PhotoField, "visit.jpg");
        }

        return form;
    }
}

/// <summary>CYCLE-04 calls and what they should have left behind.</summary>
public static class CultivationLogTestKit
{
    public static Task<HttpResponseMessage> PostLogAsync(this HttpClient client, Guid cycleId, LogFields? fields = null) =>
        client.PostAsync($"/api/cultivation-cycles/{cycleId}/cultivation-logs", (fields ?? new LogFields()).ToForm());

    /// <summary>Records a log and returns the saved log, the <c>log</c> of the answer.</summary>
    public static async Task<JsonElement> RecordLogAsync(this HttpClient client, Guid cycleId, LogFields? fields = null) =>
        (await (await client.PostLogAsync(cycleId, fields)).ReadAsync(HttpStatusCode.Created)).GetProperty("log");

    public static Task<HttpResponseMessage> PutLogAsync(this HttpClient client, Guid logId, LogFields fields) =>
        client.PutAsync($"/api/cultivation-logs/{logId}", fields.ToForm());

    public static Task<HttpResponseMessage> RemoveLogAsync(this HttpClient client, Guid logId, string? reason, string? rowVersion) =>
        client.PostAsJsonAsync($"/api/cultivation-logs/{logId}/remove", new { reason, rowVersion });

    public static Task<JsonElement> GetLogAsync(this HttpClient client, Guid logId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/cultivation-logs/{logId}");

    public static async Task<List<JsonElement>> ListLogsAsync(this HttpClient client, Guid cycleId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}/cultivation-logs")).EnumerateArray().ToList();

    public static List<string> PhotoKeys(this JsonElement log) => log.GetProperty("photos").EnumerateArray().Select(p => p.Text("key")).ToList();

    public static List<int> PhotoSlots(this JsonElement log) => log.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("sortOrder").GetInt32()).ToList();

    /// <summary>A raft with an Open cycle started ten days ago, by a new Owner.</summary>
    public static async Task<CycleSetup> CycleAsync(this AuthApiFactory factory)
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raftId = (await owner.Client.CreateRaftAsync(zoneId)).Id();
        var cycle = await owner.Client.StartCycleAsync(raftId, new CycleBody(FarmingTestKit.Today.AddDays(-10)));
        return new CycleSetup(owner, zoneId, raftId, cycle.Id());
    }

    /// <summary>A logged-in Zone Manager of the zone.</summary>
    public static async Task<Actor> ManagerAsync(this AuthApiFactory factory, Guid zoneId) =>
        await factory.StaffAsync(await factory.ZoneEntityAsync(zoneId));

    /// <summary>The Open cycle becomes Closed today, as C5 will do it.</summary>
    public static Task CloseCycleAsync(this AuthApiFactory factory, Guid cycleId) => factory.UpdateAsync<CultivationCycle>(cycleId, c =>
    {
        c.StatusId = StatusIds.CultivationCycle.Closed;
        c.CloseDate = FarmingTestKit.Today;
    });

    public static Task DiscontinueRaftAsync(this AuthApiFactory factory, Guid raftId) =>
        factory.UpdateAsync<Raft>(raftId, r => r.StatusId = StatusIds.Usage.Discontinued);

    /// <summary>A version for the multipart forms: the in-memory database issues none.</summary>
    public static Task<string> LogVersionAsync(this AuthApiFactory factory, Guid logId) => factory.IssueRowVersionAsync<CultivationLog>(logId);

    public static Task<int> LogCountAsync(this AuthApiFactory factory, Guid cycleId) =>
        factory.QueryAsync(db => db.CultivationLogs.CountAsync(l => l.CycleId == cycleId));

    public static Task<int> AuditCountAsync(this AuthApiFactory factory, string action, string raftCode) =>
        factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == action && e.RecordCode == raftCode));
}

public sealed record CycleSetup(Actor Owner, Guid ZoneId, Guid RaftId, Guid CycleId);
