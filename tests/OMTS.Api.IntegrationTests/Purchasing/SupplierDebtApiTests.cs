using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>PURCHASE-04 / BR-PURCHASE-04: the list with the debt age, the unpaid receipts (FIFO), the statement of a period and the export.</summary>
public class SupplierDebtApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Debts = "/api/supplier-debts";

    private static ReceiptBody Dated(PurchasingWorld world, int daysAgo, decimal amount) =>
        world.Body(new LineBody(world.Other, 1, amount)) with { ReceiptDate = Today.AddDays(-daysAgo), Complete = true };

    [Fact]
    public async Task The_list_has_each_supplier_with_a_debt_not_zero_the_oldest_debt_first_and_the_totals_of_every_match()
    {
        var tag = NewCode();
        var owner = await factory.PurchasingOwnerAsync();
        async Task<PurchasingWorld> World(string name)
        {
            var world = await factory.WorldAsync(owner);
            var supplier = await owner.CreateSupplierAsync(name: $"{tag} {name}");
            return world with { SupplierId = supplier };
        }

        var (older, newer, advance, settled, discontinued) = (await World("older"), await World("newer"), await World("advance"), await World("settled"), await World("stopped"));
        await owner.CreateReceiptAsync(Dated(older, 10, 100_000));
        await owner.CreateReceiptAsync(Dated(newer, 3, 900_000));
        await advance.PayAsync(50_000);
        await settled.PaidAsync(1_000, new LineBody(settled.Other, 1, 1_000));
        await owner.CreateReceiptAsync(Dated(discontinued, 5, 7_000));
        await (await owner.PostAsync($"/api/suppliers/{discontinued.SupplierId}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        var list = await (await owner.GetAsync($"{Debts}?search={tag}")).ReadAsync(HttpStatusCode.OK);

        var rows = list.GetProperty("page").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            [(older.SupplierId, 100_000m, (int?)10), (discontinued.SupplierId, 7_000m, 5), (newer.SupplierId, 900_000m, 3), (advance.SupplierId, -50_000m, null)],
            rows.Select(r => (r.GetProperty("supplier").Id(), r.Money("debt"), Age(r))).ToList());
        Assert.Equal(Today.AddDays(-10).ToString("yyyy-MM-dd"), rows[0].GetProperty("oldestUnpaidDate").GetString());
        Assert.Equal("Discontinued", rows[1].GetProperty("supplier").GetProperty("status").GetString());
        Assert.Equal((1_007_000m, 50_000m), (list.Money("totalOwed"), list.Money("totalAdvance")));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("messageCode").ValueKind);

        var page = await (await owner.GetAsync($"{Debts}?search={tag}&page=2&pageSize=3")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((4, 1, 1_007_000m), (page.GetProperty("page").GetProperty("total").GetInt32(), page.GetProperty("page").GetProperty("items").GetArrayLength(), page.Money("totalOwed")));

        var none = await (await owner.GetAsync($"{Debts}?search={NewCode()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-10", 0), (none.GetProperty("messageCode").GetString(), none.GetProperty("page").GetProperty("items").GetArrayLength()));
    }

    [Fact]
    public async Task The_detail_has_the_unpaid_receipts_oldest_first_covered_by_payments_and_a_cancelled_receipt_or_payment_or_draft_counts_nowhere()
    {
        var world = await factory.WorldAsync();
        var owner = world.Owner;
        var first = await owner.CreateReceiptAsync(Dated(world, 20, 100_000));
        var second = await owner.CreateReceiptAsync(Dated(world, 10, 200_000));
        var third = await owner.CreateReceiptAsync(Dated(world, 5, 300_000));
        var cancelledReceipt = await owner.CreateReceiptAsync(Dated(world, 30, 999_000));
        await (await owner.CancelAsync(cancelledReceipt.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        await owner.CreateReceiptAsync(Dated(world, 40, 888_000) with { Complete = false });
        await (await owner.PostReturnAsync(second.Id(), new ReturnBody { DebtReductionAmount = 50_000, Lines = [new ReturnLineBody(second.LineOf(world.Other), 0.25m)] })).ReadAsync(HttpStatusCode.Created);
        await world.PayAsync(180_000);
        var mistake = await world.PayAsync(70_000);
        await (await owner.CancelPaymentAsync(mistake.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);

        var detail = await (await owner.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);

        // 600,000 − 50,000 − 180,000. FIFO: 100,000 covers the first, the remaining 80,000 goes on the net 150,000 of the second.
        Assert.Equal(370_000m, detail.Money("debtToday"));
        Assert.Equal((10, Today.AddDays(-10).ToString("yyyy-MM-dd")), (detail.GetProperty("debtAgeDays").GetInt32(), detail.GetProperty("oldestUnpaidDate").GetString()));
        var unpaid = detail.GetProperty("unpaidReceipts").EnumerateArray().ToList();
        Assert.Equal(
            [(second.Id(), 200_000m, 50_000m, 80_000m, 70_000m, 10), (third.Id(), 300_000m, 0m, 0m, 300_000m, 5)],
            unpaid.Select(r => (r.Id(), r.Money("total"), r.Money("returned"), r.Money("covered"), r.Money("remaining"), r.GetProperty("ageDays").GetInt32())).ToList());
        Assert.DoesNotContain(unpaid, r => r.Id() == first.Id());
    }

    [Fact]
    public async Task A_debt_with_no_unpaid_receipt_counts_its_age_from_the_money_paid_back_and_no_debt_has_no_age()
    {
        var world = await factory.WorldAsync();
        await world.PayAsync(5_000, Today.AddDays(-8), "PaidBack");

        var detail = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((5_000m, 8, 0), (detail.Money("debtToday"), detail.GetProperty("debtAgeDays").GetInt32(), detail.GetProperty("unpaidReceipts").GetArrayLength()));

        var other = await factory.WorldAsync(world.Owner);
        var zero = await (await world.Owner.GetAsync($"{Debts}/{other.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((0m, JsonValueKind.Null), (zero.Money("debtToday"), zero.GetProperty("debtAgeDays").ValueKind));
        await (await world.Owner.GetAsync($"{Debts}/{Guid.NewGuid()}")).ReadAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_statement_opens_with_the_debt_before_the_period_and_opening_plus_entries_is_closing_also_in_a_past_period()
    {
        var world = await factory.WorldAsync();
        await world.Owner.CreateReceiptAsync(Dated(world, 30, 100_000));
        await world.PayAsync(40_000, Today.AddDays(-25));
        var inPeriod = await world.Owner.CreateReceiptAsync(Dated(world, 10, 500_000));
        await world.PayAsync(60_000, Today.AddDays(-9));
        await world.PayAsync(5_000, Today.AddDays(-8), "PaidBack");
        var cancelled = await world.PayAsync(11_000, Today.AddDays(-7));
        await world.PayAsync(77_000, Today.AddDays(-1));
        await (await world.Owner.CancelPaymentAsync(cancelled.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);

        var statement = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from={Today.AddDays(-15):yyyy-MM-dd}&to={Today.AddDays(-5):yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);

        var entries = statement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(60_000m, statement.Money("openingDebt"));
        Assert.Equal(
            [("Receipt", 500_000m, 560_000m), ("Payment", -60_000m, 500_000m), ("PaidBack", 5_000m, 505_000m)],
            entries.Select(e => (e.GetProperty("kind").GetString(), e.Money("amount"), e.Money("debtAfter"))).ToList());
        Assert.Equal(inPeriod.Id(), entries[0].Id());
        Assert.Equal(505_000m, statement.Money("closingDebt"));
        Assert.Equal(statement.Money("openingDebt") + entries.Sum(e => e.Money("amount")), statement.Money("closingDebt"));
        Assert.Equal(505_000m - 77_000m, statement.Money("debtToday"));
    }

    [Fact]
    public async Task The_period_defaults_to_the_current_month_and_is_checked_and_any_supplier_can_be_opened()
    {
        var world = await factory.WorldAsync();
        var url = $"{Debts}/{world.SupplierId}/statement";
        var month = new DateOnly(Today.Year, Today.Month, 1);

        var statement = await (await world.Owner.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((month.ToString("yyyy-MM-dd"), month.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd")), (statement.GetProperty("from").GetString(), statement.GetProperty("to").GetString()));
        Assert.Equal((0m, 0, 0m), (statement.Money("openingDebt"), statement.GetProperty("entries").GetArrayLength(), statement.Money("closingDebt")));

        Assert.Equal("From date", await (await world.Owner.GetAsync($"{url}?from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("To date", await (await world.Owner.GetAsync($"{url}?from=2000-01-01&to=2010-01-01")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await world.Owner.GetAsync($"{url}?from=0001-01-01&to=0001-01-02")).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.GetAsync($"{url}?from=9999-01-01&to=9999-12-31")).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.GetAsync($"{Debts}/{Guid.NewGuid()}/statement")).ReadAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_export_holds_the_debt_list_and_the_statement_of_the_supplier_chosen()
    {
        var tag = NewCode();
        var world = await factory.WorldAsync();
        var supplier = await world.Owner.CreateSupplierAsync(name: $"{tag} NCC");
        world = world with { SupplierId = supplier };
        await world.Owner.CreateReceiptAsync(Dated(world, 2, 12_345));
        await world.PayAsync(345, Today.AddDays(-1));

        var listOnly = await world.Owner.GetAsync($"{Debts}/export?search={tag}");
        Assert.Equal(HttpStatusCode.OK, listOnly.StatusCode);
        using (var workbook = new XLWorkbook(await listOnly.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(["Supplier debts"], workbook.Worksheets.Select(s => s.Name));
            var rows = workbook.Worksheet("Supplier debts").RowsUsed().Skip(1).ToList();
            Assert.Equal(($"{tag} NCC", 12_000m, 2m), (rows[0].Cell(1).GetString(), rows[0].Cell(4).GetValue<decimal>(), rows[0].Cell(5).GetValue<decimal>()));
            Assert.Equal(("Total owed", 12_000m), (rows[1].Cell(1).GetString(), rows[1].Cell(4).GetValue<decimal>()));
            Assert.Equal(("Total paid in advance", 0m), (rows[2].Cell(1).GetString(), rows[2].Cell(4).GetValue<decimal>()));
        }

        var withStatement = await world.Owner.GetAsync($"{Debts}/export?supplierId={supplier}&from={Today.AddDays(-5):yyyy-MM-dd}&to={Today:yyyy-MM-dd}&search={tag}");
        Assert.Equal(HttpStatusCode.OK, withStatement.StatusCode);
        Assert.Equal($"supplier-debts-{Today.AddDays(-5):yyyyMMdd}-{Today:yyyyMMdd}.xlsx", withStatement.Content.Headers.ContentDisposition!.FileName);
        using (var workbook = new XLWorkbook(await withStatement.Content.ReadAsStreamAsync()))
        {
            var rows = workbook.Worksheet("Statement").RowsUsed().Skip(1).ToList();
            Assert.Equal(
                [("Debt at the start", 0m), ("Purchase receipt", 12_345m), ("Payment", 12_000m), ("Debt at the end", 12_000m)],
                rows.Select(r => (r.Cell(2).GetString(), r.Cell(9).GetValue<decimal>())).ToList());
        }

        // MSG-GEN-01 for a wrong period, not found for an unknown supplier: no file.
        Assert.Equal("From date", await (await world.Owner.GetAsync($"{Debts}/export?supplierId={supplier}&from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await world.Owner.GetAsync($"{Debts}/export?supplierId={Guid.NewGuid()}")).ReadAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Debts_follow_the_accountant_role_in_any_zone_and_the_zone_manager_gets_MSG_GEN_08()
    {
        var world = await factory.WorldAsync();
        var (otherZone, _) = await factory.ZoneWithRaftAsync();
        var urls = new[] { Debts, $"{Debts}/{world.SupplierId}", $"{Debts}/{world.SupplierId}/statement", $"{Debts}/export" };

        var manager = await factory.PurchasingStaffAsync(null, StaffRole.ZoneManager);
        foreach (var url in urls)
        {
            await (await manager.GetAsync(url)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        foreach (var accountant in new[] { await factory.AccountantAsync(otherZone), await factory.AccountantAsync() })
        {
            foreach (var url in urls)
            {
                Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync(url)).StatusCode);
            }
        }

        // The old routes are gone.
        foreach (var url in new[] { "/api/suppliers/debts", $"/api/suppliers/{world.SupplierId}/debt" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await world.Owner.GetAsync(url)).StatusCode);
        }
    }

    private static int? Age(JsonElement row) => row.GetProperty("debtAgeDays").ValueKind == JsonValueKind.Null ? null : row.GetProperty("debtAgeDays").GetInt32();
}
