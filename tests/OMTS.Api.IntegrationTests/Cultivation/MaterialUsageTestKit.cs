using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>One item of a repair.</summary>
public sealed record UsageLine(Guid? ItemId, decimal? Quantity = 2m);

/// <summary>The body of a recorded repair (CYCLE-03.0).</summary>
public sealed record UsageBody(DateOnly? UsageDate, string? RepairReason, IReadOnlyList<UsageLine>? Items);

/// <summary>The body of an edit (CYCLE-03.1).</summary>
public sealed record UsageEdit(Guid? RaftId, DateOnly? UsageDate, Guid? ItemId, decimal? Quantity, string? RepairReason, string? Reason, string? RowVersion);

/// <summary>CYCLE-03 calls. Items go straight into the table: the item rules and the group names of the catalogue are not under test here.</summary>
public static class MaterialUsageTestKit
{
    public const string BrokenBuoy = "broken buoy";

    public static Task<HttpResponseMessage> PostUsageAsync(this HttpClient client, Guid raftId, UsageBody body) =>
        client.PostAsJsonAsync($"/api/rafts/{raftId}/material-usages", body);

    /// <summary>Records one repair of one item and returns its record.</summary>
    public static async Task<JsonElement> RecordUsageAsync(this HttpClient client, Guid raftId, Guid itemId, DateOnly? date = null, decimal quantity = 2m, string reason = BrokenBuoy)
    {
        var body = new UsageBody(date ?? Today, reason, [new UsageLine(itemId, quantity)]);
        var saved = await (await client.PostUsageAsync(raftId, body)).ReadAsync(HttpStatusCode.Created);
        return saved.GetProperty("records")[0];
    }

    public static Task<HttpResponseMessage> PutUsageAsync(this HttpClient client, Guid usageId, UsageEdit edit) =>
        client.PutAsJsonAsync($"/api/material-usages/{usageId}", edit);

    /// <summary>An edit that changes nothing: what the record holds, with a reason.</summary>
    public static UsageEdit AsEdit(this JsonElement record, string? reason = "wrong entry") => new(
        record.GetProperty("raftId").GetGuid(), record.Date("usageDate"), record.GetProperty("itemId").GetGuid(), record.GetProperty("quantity").GetDecimal(),
        record.Text("repairReason"), reason, record.Text("rowVersion"));

    public static Task<JsonElement> UsageListAsync(this HttpClient client, Guid raftId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/material-usages");

    public static Task<JsonElement> GetUsageAsync(this HttpClient client, Guid usageId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/material-usages/{usageId}");

    /// <summary>The groups of the list, each as its cycle id (null for no cycle) with the ids of its records.</summary>
    public static List<(Guid? Cycle, List<Guid> Records)> GroupsOf(this JsonElement list) => list.GetProperty("groups").EnumerateArray()
        .Select(g => (
            g.GetProperty("cycle").ValueKind == JsonValueKind.Null ? (Guid?)null : g.GetProperty("cycle").Id(),
            g.GetProperty("repairs").EnumerateArray().SelectMany(r => r.GetProperty("records").EnumerateArray()).Select(r => r.Id()).ToList()))
        .ToList();

    public static FarmingInputItem NewItem(InputItemGroup group = MaterialUsage.ItemGroup, int statusId = StatusIds.Usage.InUse) => new()
    {
        Code = NewCode(), Name = $"Item {NewCode()}", Group = group, Unit = "piece", StatusId = statusId,
    };

    /// <summary>A new item in the database; by default In Use, in the group of materials.</summary>
    public static async Task<Guid> ItemAsync(this AuthApiFactory factory, InputItemGroup group = MaterialUsage.ItemGroup, int statusId = StatusIds.Usage.InUse)
    {
        var item = NewItem(group, statusId);
        await factory.UseDbAsync(db => db.FarmingInputItems.Add(item));
        return item.Id;
    }

    public static async Task<Guid> ItemAsync(this FarmingSqlServerHost host)
    {
        var item = NewItem();
        await host.UseDbAsync(db => db.FarmingInputItems.Add(item));
        return item.Id;
    }

    /// <summary>A raft with no cycle, by a new Owner.</summary>
    public static async Task<CycleSetup> RaftAsync(this AuthApiFactory factory)
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        return new CycleSetup(owner, zoneId, (await owner.Client.CreateRaftAsync(zoneId)).Id(), Guid.Empty);
    }
}
