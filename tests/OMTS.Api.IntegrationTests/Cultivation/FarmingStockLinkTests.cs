using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// PM decision 11/11/2026: stock is the single source of the quantity on hand. A seeding session and a material usage post a linked use issue (Xuất dùng) in the same save, are checked against the
/// stock (MSG-INPUT-01), follow their correction and removal, and cannot be doubled by a manual issue. Each test starts its own host, as it changes the cooperative's stock setting.
/// </summary>
public sealed class FarmingStockLinkTests
{
    private const string Items = "/api/farming-input-items";
    private const string Issues = "/api/input-issues";

    private static async Task AllowNegativeAsync(HttpClient owner, bool allow)
    {
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/input-inventory-settings");
        await (await owner.PutAsJsonAsync("/api/input-inventory-settings", new { costMethod = "Fixed", allowNegativeQuantity = allow, rowVersion = settings.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
    }

    private static async Task<decimal> LotOnHandAsync(HttpClient owner, Guid itemId, Guid lotLineId) =>
        (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{itemId}/seed-lots")).GetProperty("lots").EnumerateArray().Single(l => l.GetProperty("seedLotLineId").GetGuid() == lotLineId).Money("onHand");

    [Fact]
    public async Task A_seeding_session_posts_a_linked_issue_and_the_seed_lot_shows_what_is_left()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(100m);

        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1, 2], quantity: 30m));

        var issueId = record.GetProperty("issueId").GetGuid();
        Assert.StartsWith("XD", record.Text("issueCode"));
        var issue = await api.QueryAsync(db => db.InputIssues.Include(i => i.Lines).SingleAsync(i => i.Id == issueId));
        Assert.Equal((InputIssueKind.Use, InputIssueSource.Seeding, record.Id(), setup.CycleId, setup.RaftId, StatusIds.InputIssue.Completed), (issue.Kind, issue.SourceKind, issue.SourceRecordId, issue.CycleId, issue.RaftId, issue.StatusId));
        var line = Assert.Single(issue.Lines);
        Assert.Equal((lot.ItemId, lot.LineId, 30m), (line.ItemId, line.SeedLotLineId, line.Quantity));

        Assert.Equal(70m, await LotOnHandAsync(owner, lot.ItemId, lot.LineId));
        var offered = (await owner.SeedLotsAsync(setup.RaftId)).GetProperty("lots").EnumerateArray().Single(l => l.Id() == lot.LineId);
        Assert.Equal((100m, 0m, 30m, 70m, "kg"), (offered.Money("quantityReceived"), offered.Money("quantityReturned"), offered.Money("quantityConsumed"), offered.Money("onHand"), offered.Text("unit")));

        var movement = (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{lot.ItemId}/movements")).GetProperty("items")[0];
        Assert.Equal(("Issue", "Seeding", record.Id(), setup.CycleId, setup.RaftId), (movement.Text("kind"), movement.Text("sourceKind"), movement.GetProperty("sourceRecordId").GetGuid(), movement.GetProperty("cycleId").GetGuid(), movement.GetProperty("raftId").GetGuid()));
    }

    [Fact]
    public async Task A_quantity_above_what_is_on_hand_is_refused_with_msg_input_01_and_saves_nothing()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(100m);
        await AllowNegativeAsync(owner, false);

        var refused = await (await owner.PostSeedingAsync(setup.CycleId, lot.Session(quantity: 101m))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-INPUT-01", 100m, -1m), (refused.Text("code"), refused.Money("onHand"), refused.Money("balanceAfter")));
        Assert.Contains("-1", refused.Text("detail"));
        Assert.Equal(0, await api.QueryAsync(db => db.SeedingRecords.CountAsync()));
        Assert.Equal(0, await api.QueryAsync(db => db.InputIssues.CountAsync(i => i.SourceKind == InputIssueSource.Seeding)));

        await owner.RecordSeedingAsync(setup.CycleId, lot.Session(quantity: 100m));
        Assert.Equal(0m, await LotOnHandAsync(owner, lot.ItemId, lot.LineId));
        await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([3], quantity: 1m))).ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-01");
    }

    [Fact]
    public async Task Correcting_a_session_adjusts_its_issue_and_removing_it_returns_the_seed_and_cancels_the_issue()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(100m);
        await AllowNegativeAsync(owner, false);
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session(quantity: 30m));
        var issueId = record.GetProperty("issueId").GetGuid();

        var corrected = record.AsCorrection() with { Quantity = 50m, RowVersion = (await owner.GetSeedingAsync(record.Id())).Text("rowVersion") };
        await (await owner.PutSeedingAsync(record.Id(), corrected)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(50m, await LotOnHandAsync(owner, lot.ItemId, lot.LineId));
        var issue = await api.QueryAsync(db => db.InputIssues.Include(i => i.Lines).SingleAsync(i => i.Id == issueId));
        Assert.Equal(50m, Assert.Single(issue.Lines).Quantity);

        // The correction may use the quantity the session already holds, and no more than what is on hand.
        var tooMuch = corrected with { Quantity = 101m, RowVersion = (await owner.GetSeedingAsync(record.Id())).Text("rowVersion") };
        await (await owner.PutSeedingAsync(record.Id(), tooMuch)).ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-01");

        var removed = await owner.RemoveSeedingAsync(record.Id(), "Recorded by mistake", (await owner.GetSeedingAsync(record.Id())).Text("rowVersion"));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(100m, await LotOnHandAsync(owner, lot.ItemId, lot.LineId));
        Assert.Equal(StatusIds.InputIssue.Cancelled, await api.QueryAsync(db => db.InputIssues.Where(i => i.Id == issueId).Select(i => i.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task A_manual_use_of_seed_or_material_on_a_raft_with_an_open_cycle_is_refused_and_a_linked_issue_cannot_be_changed_there()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(100m);
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session(quantity: 10m));
        var typeId = (await owner.GetFromJsonAsync<JsonElement>("/api/input-issue-types")).EnumerateArray().First().Id();

        var manual = await (await owner.PostAsJsonAsync(
            Issues, new { kind = "Use", zoneId = setup.ZoneId, raftId = setup.RaftId, issueTypeId = typeId, complete = true, lines = new[] { new { itemId = lot.ItemId, quantity = 1, seedLotLineId = (Guid?)lot.LineId } } }))
            .ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Lines"), (manual.Text("code"), manual.Text("field")));

        var issueId = record.GetProperty("issueId").GetGuid();
        var issue = await owner.GetFromJsonAsync<JsonElement>($"{Issues}/{issueId}");
        await (await owner.PutAsJsonAsync($"{Issues}/{issueId}", new { kind = "Use", zoneId = setup.ZoneId, raftId = setup.RaftId, issueTypeId = typeId, rowVersion = issue.Text("rowVersion"), lines = Array.Empty<object>() }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await owner.PostAsJsonAsync($"{Issues}/{issueId}/cancel", new { reason = "x" })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_material_usage_posts_a_linked_issue_follows_its_edit_and_is_checked_against_the_stock()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var item = (await (await owner.PostAsJsonAsync(Items, new { name = "Rope", group = "Material", unit = "m", costPrice = 100m, openingQuantity = 10m })).ReadAsync(HttpStatusCode.Created)).Id();

        var usage = (await owner.RecordUsageAsync(setup.RaftId, item, quantity: 4m)).GetProperty("records")[0];

        var issueId = usage.GetProperty("issueId").GetGuid();
        var issue = await api.QueryAsync(db => db.InputIssues.SingleAsync(i => i.Id == issueId));
        Assert.Equal((InputIssueSource.MaterialUsage, usage.Id(), setup.RaftId, setup.CycleId), (issue.SourceKind, issue.SourceRecordId, issue.RaftId, issue.CycleId));
        Assert.Equal(6m, (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}")).Money("onHand"));

        await AllowNegativeAsync(owner, false);
        var edit = usage.AsEdit() with { Quantity = 11m };
        await (await owner.PutUsageAsync(usage.Id(), edit)).ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-01");
        await (await owner.PutUsageAsync(usage.Id(), edit with { Quantity = 7m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(3m, (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}")).Money("onHand"));
    }

    [Fact]
    public async Task A_return_is_limited_by_what_is_on_hand_and_a_receipt_with_consumed_seed_cannot_be_cancelled()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(100m);
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session(quantity: 30m));

        Assert.Equal("MSG-PURCHASE-03", await (await Purchasing.PurchasingTestKit.PostReturnAsync(owner, lot.ReceiptId, new Purchasing.ReturnBody { Lines = [new Purchasing.ReturnLineBody(lot.LineId, 71m)] })).ErrorCodeAsync());
        await owner.ReturnLotAsync(lot, 70m);
        Assert.Equal(0m, await LotOnHandAsync(owner, lot.ItemId, lot.LineId));
    }
}

file static class StockLinkKit
{
    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static async Task ProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ErrorCodeAsync());
    }
}
