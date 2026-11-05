using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-03: the current month by default; filters item group, raft, supplier, status and period; the totals of the
/// Completed receipts (matching lines only), of the returns dated in the period and the net; the raft material cost per line
/// and period (BR-FARM-05); export; MSG-GEN-10 when nothing matches.
/// </summary>
public class PurchaseReceiptSearchTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<JsonElement> SearchAsync(HttpClient client, string query) =>
        await (await client.GetAsync($"/api/purchase-receipts?{query}")).ReadAsync(HttpStatusCode.OK);

    private static IReadOnlyList<Guid> Ids(JsonElement result) =>
        result.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Id()).ToList();

    /// <summary>Moves a receipt to another date received, as if recorded then (the API never accepts a later date).</summary>
    private Task MoveAsync(JsonElement receipt, DateOnly date) => factory.UpdateAsync<PurchaseReceipt>(receipt.Id(), r => r.ReceiptDate = date);

    [Fact]
    public async Task The_list_shows_the_current_month_by_default_each_row_with_code_date_supplier_status_and_total()
    {
        var world = await factory.WorldAsync();
        var now = await world.CompletedAsync(new LineBody(world.Other, 1.5m, 333));
        var old = await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));
        await MoveAsync(old, new DateOnly(Today.Year, Today.Month, 1).AddDays(-1));

        var result = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}");

        var first = new DateOnly(Today.Year, Today.Month, 1);
        Assert.Equal((first.ToString("yyyy-MM-dd"), first.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd")),
            (result.GetProperty("from").GetString(), result.GetProperty("to").GetString()));
        var row = Assert.Single(result.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal((now.Id(), now.GetProperty("code").GetString(), Today.ToString("yyyy-MM-dd"), world.SupplierId, "Completed", 500m),
            (row.Id(), row.GetProperty("code").GetString(), row.GetProperty("receiptDate").GetString(), row.GetProperty("supplier").Id(),
            row.GetProperty("status").GetString(), row.Money("total")));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("message").ValueKind);
        Assert.Contains(old.Id(), Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&from={Today.AddMonths(-2):yyyy-MM-dd}")));
    }

    [Fact]
    public async Task Totals_count_completed_receipts_and_the_returns_dated_in_the_period_and_a_group_or_raft_counts_its_lines_only()
    {
        var world = await factory.WorldAsync();
        var mixed = await world.CompletedAsync(
            new LineBody(world.Seed, 10, 9_000, RaftId: world.Raft.Id), world.MaterialForRaft(4, 50_000), new LineBody(world.Other, 1, 7_000));
        var other = await world.CompletedAsync(new LineBody(world.Other, 2, 10_000));
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Material, 100, 100_000)));
        var cancelled = await world.CompletedAsync(world.MaterialForRaft(1, 1_000_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        // A return dated today of a material line received last month counts this month (BR-FARM-05).
        var lastMonth = await world.CompletedAsync(world.MaterialForRaft(10, 20_000));
        await (await world.Owner.PostReturnAsync(lastMonth.Id(), new ReturnBody { DebtReductionAmount = 40_000, Lines = [new ReturnLineBody(lastMonth.LineOf(world.Material), 2)] }))
            .ReadAsync(HttpStatusCode.Created);
        await MoveAsync(lastMonth, Today.AddMonths(-1));
        var period = $"supplierId={world.SupplierId}&from={new DateOnly(Today.Year, Today.Month, 1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";

        var all = await SearchAsync(world.Owner, period);
        var totals = all.GetProperty("totals");
        Assert.Equal((4, 2), (totals.GetProperty("count").GetInt32(), totals.GetProperty("completedCount").GetInt32()));
        // 90,000 + 200,000 + 7,000 + 20,000 received; 40,000 returned.
        Assert.Equal((317_000m, 40_000m, 277_000m), (totals.Money("receiptsTotal"), totals.Money("returnsTotal"), totals.Money("netTotal")));
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("raftMaterialCost").ValueKind);

        var material = await SearchAsync(world.Owner, $"{period}&itemGroup=Material");
        Assert.Equal(new[] { mixed.Id(), draft.Id(), cancelled.Id() }.Order(), Ids(material).Order());
        Assert.Equal((200_000m, 40_000m, 160_000m), (material.GetProperty("totals").Money("receiptsTotal"), material.GetProperty("totals").Money("returnsTotal"), material.GetProperty("totals").Money("netTotal")));
        var mixedRow = material.GetProperty("page").GetProperty("items").EnumerateArray().Single(r => r.Id() == mixed.Id());
        Assert.Equal((297_000m, 200_000m), (mixedRow.Money("total"), mixedRow.Money("linesTotal")));

        // For a raft: its Seed and Material lines; its material cost is the net of the Material lines only.
        var raft = await SearchAsync(world.Owner, $"{period}&raftId={world.Raft.Id}");
        Assert.DoesNotContain(other.Id(), Ids(raft));
        Assert.Equal((290_000m, 40_000m, 160_000m), (raft.GetProperty("totals").Money("receiptsTotal"), raft.GetProperty("totals").Money("returnsTotal"), raft.GetProperty("totals").Money("raftMaterialCost")));

        var drafts = await SearchAsync(world.Owner, $"{period}&statusId={StatusIds.PurchaseReceipt.Draft}");
        Assert.Equal((1, 0m, 0m), (drafts.GetProperty("totals").GetProperty("count").GetInt32(), drafts.GetProperty("totals").Money("receiptsTotal"), drafts.GetProperty("totals").Money("returnsTotal")));
        Assert.Equal([cancelled.Id()], Ids(await SearchAsync(world.Owner, $"{period}&statusId={StatusIds.PurchaseReceipt.Cancelled}")));
        Assert.Equal(new[] { mixed.Id(), other.Id() }.Order(), Ids(await SearchAsync(world.Owner, $"{period}&itemId={world.Other}&statusId={StatusIds.PurchaseReceipt.Completed}")).Order());
    }

    [Fact]
    public async Task The_raft_material_cost_counts_material_lines_by_date_received_and_returns_by_return_date()
    {
        var world = await factory.WorldAsync();
        var (otherZone, otherRaft) = await factory.ZoneWithRaftAsync();
        var bought = await world.CompletedAsync(world.MaterialForRaft(3, 100_000), new LineBody(world.Seed, 100, 1_000, RaftId: world.Raft.Id),
            new LineBody(world.Material, 1, 70_000, RaftId: otherRaft.Id));
        var older = await world.CompletedAsync(world.MaterialForRaft(5, 10_000));
        await (await world.Owner.PostReturnAsync(older.Id(), new ReturnBody { DebtReductionAmount = 20_000, Lines = [new ReturnLineBody(older.LineOf(world.Material), 2)] }))
            .ReadAsync(HttpStatusCode.Created);
        await MoveAsync(older, Today.AddMonths(-2));
        var month = $"from={new DateOnly(Today.Year, Today.Month, 1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";

        var costs = await (await world.Owner.GetAsync($"/api/purchase-receipts/raft-material-costs?{month}&supplierId={world.SupplierId}")).ReadAsync(HttpStatusCode.OK);

        var rafts = costs.GetProperty("rafts").EnumerateArray().ToDictionary(r => r.GetProperty("raft").Id());
        var own = rafts[world.Raft.Id];
        Assert.Equal((300_000m, 20_000m, 280_000m), (own.Money("purchased"), own.Money("returned"), own.Money("materialCost")));
        var line = Assert.Single(own.GetProperty("lines").EnumerateArray());
        Assert.Equal((bought.Id(), 1, 3m, 100_000m, 300_000m), (line.GetProperty("receiptId").GetGuid(), line.GetProperty("lineNumber").GetInt32(),
            line.Money("quantity"), line.Money("unitPrice"), line.Money("lineAmount")));
        var returned = Assert.Single(own.GetProperty("returns").EnumerateArray());
        Assert.Equal((older.Id(), 2m, 20_000m), (returned.GetProperty("receiptId").GetGuid(), returned.Money("quantity"), returned.Money("returnValue")));
        Assert.Equal((otherZone.Code, 70_000m), (rafts[otherRaft.Id].GetProperty("zone").GetProperty("code").GetString(), rafts[otherRaft.Id].Money("materialCost")));
        Assert.Equal(350_000m, costs.Money("materialCost"));
        // FARM-04 shows the raft's material cost of all time: 300,000 + 50,000 − 20,000; the seed line is no material cost.
        Assert.Equal(330_000m, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));

        var single = await (await world.Owner.GetAsync($"/api/purchase-receipts/raft-material-costs?{month}&raftId={otherRaft.Id}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([otherRaft.Id], single.GetProperty("rafts").EnumerateArray().Select(r => r.GetProperty("raft").Id()));
        var none = await (await world.Owner.GetAsync($"/api/purchase-receipts/raft-material-costs?from=2001-01-01&to=2001-01-31&raftId={world.Raft.Id}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-10", 0), (none.GetProperty("messageCode").GetString(), none.GetProperty("rafts").GetArrayLength()));
    }

    [Fact]
    public async Task An_empty_result_is_MSG_GEN_10_and_a_bad_period_or_raft_is_MSG_GEN_01()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));

        var empty = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&from=2001-01-01&to=2001-12-31");
        Assert.Equal(("MSG-GEN-10", "There is no data for the period and filters you chose. Please choose again."),
            (empty.GetProperty("messageCode").GetString(), empty.GetProperty("message").GetString()));
        Assert.Equal(0, empty.GetProperty("totals").GetProperty("count").GetInt32());

        Assert.Equal("From date", await (await world.Owner.GetAsync("/api/purchase-receipts?from=2026-02-01&to=2026-01-31")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        foreach (var path in new[] { "/api/purchase-receipts", "/api/purchase-receipts/export", "/api/purchase-receipts/raft-material-costs" })
        {
            Assert.Equal("To date", await (await world.Owner.GetAsync($"{path}?from=2000-01-01&to=2005-01-02")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
            Assert.Equal("Raft", await (await world.Owner.GetAsync($"{path}?raftId={Guid.NewGuid()}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        await (await world.Owner.GetAsync("/api/purchase-receipts?from=2000-01-01&to=2005-01-01")).ReadAsync(HttpStatusCode.OK);
        var huge = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&page={int.MaxValue}&pageSize=100000");
        Assert.Equal((100, 1), (huge.GetProperty("page").GetProperty("pageSize").GetInt32(), huge.GetProperty("totals").GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task Pages_are_stable_and_the_totals_cover_every_page()
    {
        var world = await factory.WorldAsync();
        for (var i = 1; i <= 5; i++)
        {
            await world.CompletedAsync(new LineBody(world.Other, i, 10_000));
        }

        var seen = new List<string>();
        JsonElement page = default;
        for (var number = 1; number <= 3; number++)
        {
            page = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&pageSize=2&page={number}");
            seen.AddRange(page.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.GetProperty("code").GetString()!));
        }

        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal((5, 150_000m), (page.GetProperty("page").GetProperty("total").GetInt32(), page.GetProperty("totals").Money("receiptsTotal")));
    }

    [Fact]
    public async Task The_export_has_the_rows_and_the_totals_shown()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1.5m, 333));
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));
        var manager = await factory.ZoneManagerAsync(world.Zone);

        var response = await manager.GetAsync($"/api/purchase-receipts/export?supplierId={world.SupplierId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("purchase-receipts-", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    [Fact]
    public async Task The_supplier_detail_lists_its_latest_receipts_with_their_totals()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1.5m, 333));
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 2, 1_000)));

        var supplier = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}");

        var rows = supplier.GetProperty("receipts").EnumerateArray().ToDictionary(r => r.Id());
        Assert.Equal(("Completed", 500m), (rows[receipt.Id()].GetProperty("status").GetString(), rows[receipt.Id()].Money("total")));
        Assert.Equal(("Draft", 2_000m), (rows[draft.Id()].GetProperty("status").GetString(), rows[draft.Id()].Money("total")));
        Assert.False(rows[receipt.Id()].TryGetProperty("zoneId", out _));
        Assert.Equal(2, await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync(r => r.SupplierId == world.SupplierId)));
    }
}
