using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// Decision of 12/11/2026: a seeding session and a material usage are removed with a reason while their cycle is Open (the linked stock issue is cancelled and the quantity returns to the stock; a
/// closed cycle or a harvest lot refuses with 409), and the history of a harvest lot, session, usage, log and cycle reads who, when, which field went from which value to which, and the reason.
/// </summary>
public sealed class FarmingRemovalAndHistoryTests
{
    private const string Items = "/api/farming-input-items";

    private static async Task<Guid> MaterialAsync(HttpClient owner, decimal opening) =>
        (await (await owner.PostAsJsonAsync(Items, new { name = "Rope", group = "Material", unit = "m", costPrice = 100m, openingQuantity = opening })).ReadAsync(HttpStatusCode.Created)).Id();

    private static async Task<decimal> OnHandAsync(HttpClient owner, Guid item) => (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}")).Money("onHand");

    private static Task<HttpResponseMessage> RemoveUsageAsync(HttpClient client, Guid usageId, string? reason, string? rowVersion) =>
        client.PostAsJsonAsync($"/api/material-usages/{usageId}/remove", new { reason, rowVersion });

    [Fact]
    public async Task A_material_usage_is_removed_with_a_reason_its_issue_is_cancelled_and_the_quantity_returns_to_the_stock()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await MaterialAsync(owner, 10m);
        var usage = await owner.RecordUsageAsync(setup.RaftId, item, quantity: 4m);
        var issueId = usage.GetProperty("issueId").GetGuid();
        Assert.Equal(6m, await OnHandAsync(owner, item));

        Assert.Equal("Reason", (await (await RemoveUsageAsync(owner, usage.Id(), " ", usage.Text("rowVersion"))).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));

        var removed = await RemoveUsageAsync(owner, usage.Id(), "Entered twice", usage.Text("rowVersion"));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(10m, await OnHandAsync(owner, item));
        Assert.Equal(StatusIds.InputIssue.Cancelled, await api.QueryAsync(db => db.InputIssues.Where(i => i.Id == issueId).Select(i => i.StatusId).SingleAsync()));
        Assert.Equal(0, await api.QueryAsync(db => db.MaterialUsages.CountAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/material-usages/{usage.Id()}")).StatusCode);

        // The record is gone but its history stays, with the reason.
        var history = await owner.GetFromJsonAsync<JsonElement>($"/api/material-usages/{usage.Id()}/history");
        var last = history.GetProperty("entries").EnumerateArray().Last();
        Assert.Equal(("Removed", "Entered twice"), (last.Text("action"), last.Text("reason")));
        Assert.Contains(last.GetProperty("changes").EnumerateArray(), c => c.Text("field") == "Quantity" && c.Text("label") == "Số lượng" && c.Text("from") == "4" && (!c.TryGetProperty("to", out var to) || to.ValueKind == JsonValueKind.Null));
    }

    [Fact]
    public async Task A_material_usage_dated_in_a_closed_cycle_cannot_be_removed()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await MaterialAsync(owner, 10m);
        var usage = await owner.RecordUsageAsync(setup.RaftId, item, quantity: 1m);
        await api.UpdateAsync<CultivationCycle>(setup.CycleId, c => (c.StatusId, c.CloseDate) = (StatusIds.CultivationCycle.Closed, FarmingTestKit.Today));

        await (await RemoveUsageAsync(owner, usage.Id(), "Wrong", usage.Text("rowVersion"))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal(1, await api.QueryAsync(db => db.MaterialUsages.CountAsync()));
    }

    [Fact]
    public async Task A_seeding_session_is_removed_while_the_cycle_is_open_and_refused_with_409_once_a_lane_is_in_a_harvest_lot()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        var second = await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([3], quantity: 5m));
        var onHand = (await owner.SeedLotsAsync(raft.RaftId)).GetProperty("lots").EnumerateArray().Single(l => l.Id() == raft.Lot.LineId).Money("onHand");

        // Lane 3 has no harvest lot: its session is removed and its seed returns to the lot.
        var removed = await owner.RemoveSeedingAsync(second.Id(), "Wrong lane", second.Text("rowVersion"));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(onHand + 5m, (await owner.SeedLotsAsync(raft.RaftId)).GetProperty("lots").EnumerateArray().Single(l => l.Id() == raft.Lot.LineId).Money("onHand"));

        // Lane 1 is in a harvest lot: its session is locked.
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var locked = await owner.RemoveSeedingAsync(raft.Session.Id(), "Too late", raft.Session.Text("rowVersion"));
        await locked.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal(1, await api.QueryAsync(db => db.SeedingRecords.CountAsync()));
    }

    [Fact]
    public async Task The_history_of_a_session_and_a_harvest_lot_lists_who_when_what_changed_and_why_with_labels_and_units()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;

        var corrected = raft.Session.AsCorrection("Counted again") with { Quantity = 12m };
        await (await owner.PutSeedingAsync(raft.Session.Id(), corrected)).ReadAsync(HttpStatusCode.OK);

        var history = await owner.GetFromJsonAsync<JsonElement>($"/api/seeding-records/{raft.Session.Id()}/history");
        var entries = history.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["Created", "Edited"], entries.Select(e => e.Text("action")).ToList());
        var edit = entries[1];
        Assert.Equal(("Counted again", "Chủ hợp tác xã", "CooperativeOwner"), (edit.Text("reason"), edit.Text("byRole"), edit.Text("byRoleCode")));
        Assert.False(string.IsNullOrWhiteSpace(edit.Text("byName")));
        var change = Assert.Single(edit.GetProperty("changes").EnumerateArray());
        Assert.Equal(("Quantity", "Số lượng giống", "10", "12"), (change.Text("field"), change.Text("label"), change.Text("from"), change.Text("to")));

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "2" });
        await (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/1/finish-harvest", null)).ReadAsync(HttpStatusCode.OK);
        var lotHistory = await owner.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/{lot.Id()}/history");
        var lotEntries = lotHistory.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["Created", "LaneFinished"], lotEntries.Select(e => e.Text("action")).ToList());
        var cages = lotEntries[0].GetProperty("changes").EnumerateArray().Single(c => c.Text("field") == "CageCount");
        Assert.Equal(("Số lồng", "lồng", "2"), (cages.Text("label"), cages.Text("unit"), cages.Text("to")));
    }

    [Fact]
    public async Task The_history_of_a_cycle_and_a_log_is_read_by_who_may_view_them_and_an_unknown_record_is_404()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;

        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}/history");
        var started = cycle.GetProperty("entries")[0];
        Assert.Equal("Started", started.Text("action"));
        Assert.Contains(started.GetProperty("changes").EnumerateArray(), c => c.Text("field") == "StartDate" && c.Text("label") == "Ngày bắt đầu vụ");

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/harvest-lots/{Guid.NewGuid()}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/cultivation-logs/{Guid.NewGuid()}/history")).StatusCode);

        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/history")).StatusCode);
    }
}

file static class FarmingPlanKit
{
    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static async Task ProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ErrorCodeAsync());
    }
}
