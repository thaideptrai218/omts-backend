using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-07: filters, paging, totals over the filtered set, raft material cost, Excel export and zone limits.</summary>
public class PurchaseReceiptListExportTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<JsonElement> SearchAsync(HttpClient client, string query) =>
        await (await client.GetAsync($"/api/purchase-receipts?{query}")).ReadAsync(HttpStatusCode.OK);

    private static List<Guid> Ids(JsonElement result) =>
        result.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Id()).ToList();

    // Done when: totals equal the sum of matching Completed receipts.
    [Fact]
    public async Task Filters_page_newest_first_and_totals_count_every_match_but_money_of_completed_receipts_only()
    {
        var world = await factory.WorldAsync();
        var seed = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 100, 10_000)) with
        {
            ReceiptDate = Today.AddDays(-2), DiscountAmount = 100_000, AmountPaidNow = 300_000, PaidNowMethod = "Cash", Complete = true,
        });
        var material = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Material, 10, 50_000)) with { RaftId = world.Raft.Id, ReceiptDate = Today.AddDays(-1), Complete = true });
        await (await world.Owner.PostReturnAsync(seed.Id(), new ReturnBody { DebtReductionAmount = 90_000, Lines = [new ReturnLineBody(seed.LineOf(world.Seed), 10)] })).ReadAsync(HttpStatusCode.Created);
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 700_000)));
        var cancelled = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 800_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Wrong", null)).ReadAsync(HttpStatusCode.OK);

        // The receipts are dated up to 2 days back, so the default "this year" period would miss them on 1–2
        // January: the period is pinned, and the default is checked on its own below.
        var period = $"from={Today.AddDays(-2):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";
        var all = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&{period}");
        // Newest receipt date first, then the higher code of the day first.
        Assert.Equal([cancelled.Id(), draft.Id(), material.Id(), seed.Id()], Ids(all));
        var totals = all.GetProperty("totals");
        Assert.Equal((4, 2), (totals.GetProperty("count").GetInt32(), totals.GetProperty("completedCount").GetInt32()));
        Assert.Equal(
            (1_500_000m, 100_000m, 1_400_000m, 90_000m, 300_000m, 1_010_000m),
            (totals.Money("subtotal"), totals.Money("discount"), totals.Money("payable"), totals.Money("returned"), totals.Money("paid"), totals.Money("outstanding")));
        Assert.Equal(JsonValueKind.Null, all.GetProperty("message").ValueKind);
        var defaults = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}");
        Assert.Equal((new DateOnly(Today.Year, 1, 1).ToString("yyyy-MM-dd"), new DateOnly(Today.Year, 12, 31).ToString("yyyy-MM-dd")),
            (defaults.GetProperty("from").GetString(), defaults.GetProperty("to").GetString()));

        var row = all.GetProperty("page").GetProperty("items").EnumerateArray().Single(r => r.Id() == seed.Id());
        Assert.Equal((seed.GetProperty("code").GetString(), world.SupplierId, world.Zone.Id, "Completed"),
            (row.GetProperty("code").GetString(), row.GetProperty("supplier").Id(), row.GetProperty("zone").Id(), row.GetProperty("status").GetString()));
        Assert.Equal((1_000_000m, 100_000m, 900_000m, 90_000m, 300_000m, 510_000m),
            (row.Money("subtotal"), row.Money("discount"), row.Money("payable"), row.Money("returned"), row.Money("paid"), row.Money("outstanding")));
        Assert.Equal(world.Raft.Id, all.GetProperty("page").GetProperty("items").EnumerateArray().Single(r => r.Id() == material.Id()).GetProperty("raft").Id());

        // Totals cover the whole filtered set, not the page.
        var page = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&{period}&page=2&pageSize=3");
        Assert.Equal([seed.Id()], Ids(page));
        Assert.Equal((4, 1_400_000m), (page.GetProperty("page").GetProperty("total").GetInt32(), page.GetProperty("totals").Money("payable")));

        Assert.Equal([seed.Id()], Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&{period}&itemGroup=Seed")));
        Assert.Equal([material.Id()], Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&{period}&itemId={world.Material}")));
        Assert.Equal([material.Id()], Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&{period}&raftId={world.Raft.Id}")));
        Assert.Equal([draft.Id()], Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&{period}&statusId={StatusIds.PurchaseReceipt.Draft}")));
        Assert.Equal([seed.Id()], Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&from={Today.AddDays(-2):yyyy-MM-dd}&to={Today.AddDays(-2):yyyy-MM-dd}")));
        Assert.Equal([material.Id()], Ids(await SearchAsync(world.Owner, $"code={material.GetProperty("code").GetString()![3..]}&supplierId={world.SupplierId}&{period}")));
        Assert.Equal(4, Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&{period}&zoneId={world.Zone.Id}")).Count);
    }

    [Fact]
    public async Task The_default_period_is_the_current_year_and_nothing_in_scope_answers_MSG08_with_zero_totals()
    {
        var world = await factory.WorldAsync();
        var lastYear = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)) with { ReceiptDate = new DateOnly(Today.Year - 1, 6, 1), Complete = true });

        var empty = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}");
        Assert.Empty(Ids(empty));
        Assert.Equal((0, 0m), (empty.GetProperty("totals").GetProperty("count").GetInt32(), empty.GetProperty("totals").Money("payable")));
        Assert.Equal("There is no data for purchase receipts in the selected scope.", empty.GetProperty("message").GetString());

        Assert.Equal([lastYear.Id()], Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&from={Today.Year - 1}-01-01")));
        // Only "to" given: from 1 January of that year.
        Assert.Equal([lastYear.Id()], Ids(await SearchAsync(world.Owner, $"supplierId={world.SupplierId}&to={Today.Year - 1}-12-31")));
    }

    [Fact]
    public async Task Bad_filters_answer_MSG01_and_filters_outside_the_callers_zones_MSG02()
    {
        var world = await factory.WorldAsync();
        var (otherZone, otherRaft) = await factory.ZoneWithRaftAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);

        foreach (var path in new[] { "/api/purchase-receipts", "/api/purchase-receipts/raft-material-costs", "/api/purchase-receipts/export" })
        {
            Assert.Equal("From date", await (await world.Owner.GetAsync($"{path}?from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}")).FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
            Assert.Equal("Raft", await (await world.Owner.GetAsync($"{path}?raftId={Guid.NewGuid()}")).FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
            await (await manager.GetAsync($"{path}?zoneId={otherZone.Id}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await manager.GetAsync($"{path}?raftId={otherRaft.Id}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        }
    }

    // Done when: a Zone Manager never sees another zone's receipts.
    [Fact]
    public async Task A_zone_manager_never_sees_another_zones_receipts_in_list_raft_costs_or_export()
    {
        var world = await factory.WorldAsync();
        var (otherZone, otherRaft) = await factory.ZoneWithRaftAsync();
        var own = await world.CompletedAsync(0, 0, new LineBody(world.Material, 1, 100_000));
        var foreign = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Material, 2, 100_000)) with { ZoneId = otherZone.Id, RaftId = otherRaft.Id, Complete = true });
        var manager = await factory.ZoneManagerAsync(world.Zone);

        var list = await SearchAsync(manager, $"supplierId={world.SupplierId}");
        Assert.Equal([own.Id()], Ids(list));
        Assert.Equal(100_000, list.GetProperty("totals").Money("payable"));
        var costs = await (await manager.GetAsync("/api/purchase-receipts/raft-material-costs")).ReadAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(costs.EnumerateArray(), c => c.GetProperty("raft").Id() == otherRaft.Id);
        Assert.Contains(costs.EnumerateArray(), c => c.GetProperty("raft").Id() == world.Raft.Id);
        var export = await ExportAsync(manager, $"supplierId={world.SupplierId}");
        Assert.Equal([own.GetProperty("code").GetString()], export.Codes);
        Assert.DoesNotContain(foreign.GetProperty("code").GetString(), export.Codes);
    }

    [Fact]
    public async Task Raft_material_cost_per_raft_counts_completed_receipts_less_discount_share_and_returns_with_the_receipts_behind_it()
    {
        var world = await factory.WorldAsync();
        var raft2 = new Domain.Entities.Farming.Raft { ZoneId = world.Zone.Id, Code = $"R2-{NewCode()}", Size = "20 m x 10 m" };
        await factory.UseDbAsync(db => db.Rafts.Add(raft2));
        // Floats 600,000 + seed 400,000, discount 100,000: floats cost 540,000; 6 floats returned (54,000) → 486,000.
        var first = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Material, 60, 10_000), new LineBody(world.Seed, 40, 10_000)) with
        {
            RaftId = world.Raft.Id, DiscountAmount = 100_000, Complete = true,
        });
        await (await world.Owner.PostReturnAsync(first.Id(), new ReturnBody { DebtReductionAmount = 54_000, Lines = [new ReturnLineBody(first.LineOf(world.Material), 6)] }))
            .ReadAsync(HttpStatusCode.Created);
        var second = await world.CompletedAsync(0, 0, new LineBody(world.Material, 3, 100_000));
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Material, 9, 100_000)) with { RaftId = world.Raft.Id });
        var cancelled = await world.CompletedAsync(0, 0, new LineBody(world.Material, 5, 100_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Wrong", null)).ReadAsync(HttpStatusCode.OK);
        var onRaft2 = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Material, 1, 70_000)) with { RaftId = raft2.Id, Complete = true });

        var rows = (await (await world.Owner.GetAsync($"/api/purchase-receipts/raft-material-costs?zoneId={world.Zone.Id}")).ReadAsync(HttpStatusCode.OK))
            .EnumerateArray().ToDictionary(r => r.GetProperty("raft").Id());
        Assert.Equal(2, rows.Count);
        var main = rows[world.Raft.Id];
        Assert.Equal(486_000 + 300_000, main.Money("materialCost"));
        Assert.Equal(new[] { first.Id(), second.Id() }.Order(), main.GetProperty("receipts").EnumerateArray().Select(r => r.GetProperty("receiptId").GetGuid()).Order());
        Assert.Equal(486_000, main.GetProperty("receipts").EnumerateArray().Single(r => r.GetProperty("receiptId").GetGuid() == first.Id()).Money("materialCost"));
        Assert.Equal(70_000, rows[raft2.Id].Money("materialCost"));
        Assert.Equal(onRaft2.Id(), rows[raft2.Id].GetProperty("receipts")[0].GetProperty("receiptId").GetGuid());
        Assert.Equal(world.Zone.Id, main.GetProperty("zone").Id());

        // FARM-04 shows the same figure on the raft.
        Assert.Equal(786_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));

        var onlyRaft2 = await (await world.Owner.GetAsync($"/api/purchase-receipts/raft-material-costs?raftId={raft2.Id}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([raft2.Id], onlyRaft2.EnumerateArray().Select(r => r.GetProperty("raft").Id()));
        var nothing = await (await world.Owner.GetAsync($"/api/purchase-receipts/raft-material-costs?zoneId={world.Zone.Id}&to={Today.Year - 1}-12-31")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(nothing.EnumerateArray());
    }

    // Done when: the export matches the on-screen list.
    [Fact]
    public async Task The_export_has_the_rows_and_totals_of_the_list_and_needs_the_export_action()
    {
        var world = await factory.WorldAsync();
        var a = await world.CompletedAsync(10_000, 50_000, new LineBody(world.Other, 3, 100_000));
        var b = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 40_000)));
        var list = await SearchAsync(world.Owner, $"supplierId={world.SupplierId}");

        var export = await ExportAsync(world.Owner, $"supplierId={world.SupplierId}");
        Assert.Equal(Ids(list).Select(id => id == a.Id() ? a.GetProperty("code").GetString() : b.GetProperty("code").GetString()), export.Codes);
        var rowA = export.Sheet.Row(2 + export.Codes.IndexOf(a.GetProperty("code").GetString()));
        Assert.Equal((300_000m, 10_000m, 290_000m, 0m, 50_000m, 240_000m),
            (Cell(rowA, 7), Cell(rowA, 8), Cell(rowA, 9), Cell(rowA, 10), Cell(rowA, 11), Cell(rowA, 12)));
        var totals = export.Sheet.Row(2 + export.Codes.Count);
        Assert.Equal(("Total (Completed)", "1 / 2"), (totals.Cell(1).GetString(), totals.Cell(6).GetString()));
        Assert.Equal(list.GetProperty("totals").Money("outstanding"), Cell(totals, 12));
        Assert.Equal(list.GetProperty("totals").Money("payable"), Cell(totals, 9));

        var noExport = await factory.PurchasingStaffAsync(world.Zone, new Perm(PermissionFunctions.PurchaseReceipts, true, true, true, true));
        await (await noExport.GetAsync("/api/purchase-receipts/export")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await noExport.GetAsync("/api/purchase-receipts")).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Export_never_goes_wider_than_the_zones_of_the_export_grant()
    {
        var world = await factory.WorldAsync();
        var (otherZone, _) = await factory.ZoneWithRaftAsync();
        var inZone = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));
        var inOther = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)) with { ZoneId = otherZone.Id, Complete = true });

        // View in both zones, Export in the first one only.
        var staff = await factory.AddAccountAsync(NewCode(), Password, Domain.Enums.SubjectType.Staff);
        var exporter = new Domain.Entities.Identity.Role { Name = NewCode() };
        exporter.Permissions.Add(new Domain.Entities.Identity.RolePermission { FunctionCode = PermissionFunctions.PurchaseReceipts, CanView = true, OtherPermissions = ["EXPORT"] });
        var viewer = new Domain.Entities.Identity.Role { Name = NewCode() };
        viewer.Permissions.Add(new Domain.Entities.Identity.RolePermission { FunctionCode = PermissionFunctions.PurchaseReceipts, CanView = true });
        await factory.UseDbAsync(db => db.Roles.AddRange(exporter, viewer));
        await factory.GrantAsync(staff, exporter, world.Zone);
        await factory.GrantAsync(staff, viewer, otherZone);
        var client = await factory.LoginAsync(staff.Username);

        Assert.Equal(2, Ids(await SearchAsync(client, $"supplierId={world.SupplierId}")).Count);
        var export = await ExportAsync(client, $"supplierId={world.SupplierId}");
        Assert.Equal([inZone.GetProperty("code").GetString()], export.Codes);
        await (await client.GetAsync($"/api/purchase-receipts/export?zoneId={otherZone.Id}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        Assert.DoesNotContain(inOther.GetProperty("code").GetString(), export.Codes);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil.example\",\"Click\")")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("@SUM(1+1)*cmd")]
    [InlineData("-2+3")]
    public async Task Text_starting_like_a_formula_is_exported_as_text_never_as_a_formula(string supplierName)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var world = await factory.WorldAsync(owner);
        world = world with { SupplierId = await owner.CreateSupplierAsync(name: supplierName) };
        await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));

        var export = await ExportAsync(owner, $"supplierId={world.SupplierId}");
        var cell = export.Sheet.Row(2).Cell(3);
        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Equal(supplierName, cell.GetString());
    }

    private static decimal Cell(IXLRow row, int column) => (decimal)row.Cell(column).GetDouble();

    private sealed record Exported(IXLWorksheet Sheet, List<string?> Codes);

    private static async Task<Exported> ExportAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/purchase-receipts/export?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^purchase-receipts-\d{8}-\d{8}\.xlsx$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "");
        var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheet("Purchase receipts");
        Assert.Equal("Code", sheet.Cell(1, 1).GetString());
        Assert.Equal("Outstanding", sheet.Cell(1, 12).GetString());
        var last = sheet.LastRowUsed()!.RowNumber();
        // Data rows sit between the header and the totals row.
        var codes = Enumerable.Range(2, last - 2).Select(r => (string?)sheet.Cell(r, 1).GetString()).ToList();
        return new Exported(sheet, codes);
    }
}
