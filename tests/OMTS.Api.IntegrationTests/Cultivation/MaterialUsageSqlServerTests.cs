using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-03 on SQL Server: real row versions and the constraints. The races are in <see cref="MaterialUsageRaceTests"/>.</summary>
public class MaterialUsageSqlServerTests
{
    private static Task<JsonElement> CycleAsync(HttpClient client, Guid cycleId) => client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}");

    private static Task<JsonElement> RaftAsync(HttpClient client, Guid raftId) => client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}");

    [SqlServerFact]
    public async Task A_write_moves_the_row_of_the_cycle_on_its_date_or_else_of_the_raft_and_an_edit_with_no_change_moves_nothing()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycle = await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)));
        var raftBefore = (await RaftAsync(owner, raftId)).Text("rowVersion");

        // Inside the Open cycle: the cycle row, not the raft row, so an open raft form stays valid.
        var inCycle = await owner.RecordUsageAsync(raftId, item, Today.AddDays(-1), 1.125m);
        Assert.Equal(8, Convert.FromBase64String(inCycle.Text("rowVersion")).Length);
        Assert.Equal(1.125m, inCycle.GetProperty("quantity").GetDecimal());
        var cycleAfter = (await CycleAsync(owner, cycle.Id())).Text("rowVersion");
        Assert.NotEqual(cycle.Text("rowVersion"), cycleAfter);
        Assert.Equal(raftBefore, (await RaftAsync(owner, raftId)).Text("rowVersion"));

        // Before the cycle: the raft row.
        await owner.RecordUsageAsync(raftId, item, Today.AddDays(-20));
        Assert.NotEqual(raftBefore, (await RaftAsync(owner, raftId)).Text("rowVersion"));
        Assert.Equal(cycleAfter, (await CycleAsync(owner, cycle.Id())).Text("rowVersion"));

        // The same values with a reason: nothing moves.
        var raftNow = (await RaftAsync(owner, raftId)).Text("rowVersion");
        var same = (await (await owner.PutUsageAsync(inCycle.Id(), inCycle.AsEdit())).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.Equal(inCycle.Text("rowVersion"), same.Text("rowVersion"));
        Assert.Equal(cycleAfter, (await CycleAsync(owner, cycle.Id())).Text("rowVersion"));
        Assert.Equal(raftNow, (await RaftAsync(owner, raftId)).Text("rowVersion"));

        // A real edit moves the record and the cycle.
        var changed = (await (await owner.PutUsageAsync(inCycle.Id(), inCycle.AsEdit() with { Quantity = 2m })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.NotEqual(inCycle.Text("rowVersion"), changed.Text("rowVersion"));
        Assert.NotEqual(cycleAfter, (await CycleAsync(owner, cycle.Id())).Text("rowVersion"));

        // Two tabs on the old version.
        var stale = await (await owner.PutUsageAsync(inCycle.Id(), inCycle.AsEdit() with { Quantity = 3m })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG06", stale.Text("code"));
    }

    [SqlServerFact]
    public async Task The_database_refuses_a_quantity_of_zero_and_an_item_with_usage_cannot_be_deleted()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var record = await owner.RecordUsageAsync(raftId, item);

        var zero = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.MaterialUsages.Add(new MaterialUsage
        {
            RaftId = raftId, ItemId = item, UsageDate = Today, RepairReason = "x", Quantity = 0m, RecordedByAccountId = record.GetProperty("recordedByAccountId").GetGuid(),
        })));
        Assert.Contains("CK_MaterialUsage_Quantity", zero.InnerException!.Message);

        var items = await Assert.ThrowsAsync<SqlException>(() => host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == item).ExecuteDeleteAsync()));
        Assert.Contains("FK_MaterialUsages_FarmingInputItems_ItemId", items.Message);
    }
}
