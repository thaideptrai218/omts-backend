using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-09.2 Print: the list and its total, or one invoice, as a file; rights and content.</summary>
public class SalesInvoiceExportTests
{
    private static async Task<XLWorkbook> WorkbookAsync(HttpResponseMessage file) => new(await file.Content.ReadAsStreamAsync());

    [Fact]
    public async Task The_zone_manager_the_accountant_and_the_owner_export_and_print()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var other = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var inOther = await other.Manager.Client.SoldAsync(other, other.LotId);
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var accountant = await api.StaffAsync(zone, StaffRole.Accountant);
        var accountantAll = await api.StaffAsync(null, StaffRole.Accountant);

        // PM answer 12/11/2026 (OMTS-224 [5.1]): the Accountant prints and exports like the Zone Manager, in its zones.
        foreach (var client in new[] { sale.Manager.Client, sale.Owner.Client, accountant.Client, accountantAll.Client })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.ExportInvoicesAsync()).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PrintInvoiceAsync(invoice.Id())).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await accountantAll.Client.PrintInvoiceAsync(inOther.Id())).StatusCode);
        var outside = await accountant.Client.PrintInvoiceAsync(inOther.Id());
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (outside.StatusCode, await outside.ErrorCodeAsync()));
        using var zoneFile = await WorkbookAsync(await accountant.Client.ExportInvoicesAsync());
        Assert.Equal(3, zoneFile.Worksheet(SalesInvoiceExportSheets.ListSheet).LastRowUsed()!.RowNumber());
    }

    [Fact]
    public async Task The_export_stays_in_the_zones_of_the_caller_and_a_print_of_another_zone_is_refused()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var inA = await a.Manager.Client.SoldAsync(a, a.LotId);
        var inB = await b.Manager.Client.SoldAsync(b, b.LotId);

        using var workbook = await WorkbookAsync(await a.Manager.Client.ExportInvoicesAsync());
        Assert.Equal(inA.Text("code"), workbook.Worksheet(SalesInvoiceExportSheets.ListSheet).Cell(2, 1).GetString());
        Assert.Equal(3, workbook.Worksheet(SalesInvoiceExportSheets.ListSheet).LastRowUsed()!.RowNumber());

        var other = await a.Manager.Client.PrintInvoiceAsync(inB.Id());
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (other.StatusCode, await other.ErrorCodeAsync()));
        var filtered = await a.Manager.Client.ExportInvoicesAsync($"?zoneId={b.ZoneId}");
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (filtered.StatusCode, await filtered.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await a.Manager.Client.PrintInvoiceAsync(Guid.NewGuid())).StatusCode);
    }

    // SALE-09.2: the list on the screen and its total: every page, whatever the page size asked for.
    [Fact]
    public async Task The_list_file_holds_every_match_and_the_total_as_values()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var first = await client.SoldAsync(sale, sale.LotId, 2m);
        var second = await client.SoldAsync(sale, sale.LotId, 3m);
        var cancelled = await client.SoldAsync(sale, sale.LotId, 4m);
        await api.UpdateAsync<SalesInvoice>(cancelled.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        var file = await client.ExportInvoicesAsync("?pageSize=1");

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Contains($"sales-invoices-{PurchaseRequestTestKit.Today:yyyyMMdd}-{PurchaseRequestTestKit.Today:yyyyMMdd}.xlsx", file.Content.Headers.ContentDisposition!.FileName!);
        using var workbook = await WorkbookAsync(file);
        var sheet = workbook.Worksheet(SalesInvoiceExportSheets.ListSheet);
        Assert.Equal(new[] { cancelled.Text("code"), second.Text("code"), first.Text("code") }, Enumerable.Range(2, 3).Select(r => sheet.Cell(r, 1).GetString()));
        Assert.Equal((XLDataType.Number, 90_000m), (sheet.Cell(3, 5).DataType, sheet.Cell(3, 5).GetValue<decimal>()));
        Assert.Equal((XLDataType.DateTime, "Completed"), (sheet.Cell(3, 2).DataType, sheet.Cell(3, 6).GetString()));
        Assert.Equal(("Total of Completed invoices", 150_000m), (sheet.Cell(5, 1).GetString(), sheet.Cell(5, 5).GetValue<decimal>()));
    }

    [Fact]
    public async Task The_cash_column_is_there_only_with_the_cash_filter_and_an_empty_list_is_a_header_and_a_zero_total()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        await client.SoldAsync(sale, sale.LotId, 2m);

        using var withCash = await WorkbookAsync(await client.ExportInvoicesAsync("?cashCollectedByMe=true"));
        var cashSheet = withCash.Worksheet(SalesInvoiceExportSheets.ListSheet);
        Assert.Equal(("Order", "Cash awaiting confirmation"), (cashSheet.Cell(1, 11).GetString(), cashSheet.Cell(1, 12).GetString()));
        Assert.Equal(60_000m, cashSheet.Cell(2, 12).GetValue<decimal>());

        var day = PurchaseRequestTestKit.Today.AddDays(-20);
        using var empty = await WorkbookAsync(await client.ExportInvoicesAsync($"?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}"));
        var emptySheet = empty.Worksheet(SalesInvoiceExportSheets.ListSheet);
        Assert.Equal(("Code", "Total of Completed invoices", 0m), (emptySheet.Cell(1, 1).GetString(), emptySheet.Cell(2, 1).GetString(), emptySheet.Cell(2, 5).GetValue<decimal>()));
    }

    // SALE-09.2 and SALE-09.1 step 2: the invoice file.
    [Fact]
    public async Task The_invoice_file_holds_the_heading_lines_payments_and_sendings()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var sold = (await client.SellAsync(sale.PayLater(sale.LotId, 10m, cash: 100_000m))).GetProperty("invoice");
        await client.FinishedAsync(sold.Id(), wantsQrCode: false);

        var file = await client.PrintInvoiceAsync(sold.Id());

        Assert.Contains($"sales-invoice-{sold.Text("code")}.xlsx", file.Content.Headers.ContentDisposition!.FileName!);
        using var workbook = await WorkbookAsync(file);
        var heading = workbook.Worksheet(SalesInvoiceExportSheets.InvoiceSheet);
        Assert.Equal(("Invoice", sold.Text("code")), (heading.Cell(2, 1).GetString(), heading.Cell(2, 2).GetString()));
        var lines = workbook.Worksheet(SalesInvoiceExportSheets.LinesSheet);
        Assert.Equal((1, 10m, ListPrice, "PriceList", 300_000m), (lines.Cell(2, 1).GetValue<int>(), lines.Cell(2, 4).GetValue<decimal>(), lines.Cell(2, 5).GetValue<decimal>(), lines.Cell(2, 6).GetString(), lines.Cell(2, 7).GetValue<decimal>()));
        Assert.Equal(("Total", 300_000m), (lines.Cell(7, 1).GetString(), lines.Cell(7, 7).GetValue<decimal>()));
        var payments = workbook.Worksheet(SalesInvoiceExportSheets.PaymentsSheet);
        Assert.Equal(("Cash", 100_000m), (payments.Cell(2, 3).GetString(), payments.Cell(2, 4).GetValue<decimal>()));
        Assert.Equal(1, workbook.Worksheet(SalesInvoiceExportSheets.SendingsSheet).LastRowUsed()!.RowNumber() - 1);
    }

    // PM answer 12/11/2026 (OMTS-224 [5.7]): at most 5,000 invoices in one file; above that 400 MSG-GEN-01 on Filters and nothing is written.
    [Fact]
    public async Task A_selection_of_more_than_five_thousand_invoices_is_refused_and_exactly_five_thousand_are_exported()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var accountId = sale.Owner.Account.Id;
        var invoices = Enumerable.Range(0, SalesInvoiceSearchService.MaxExportRows)
            .Select(n => SalesInvoiceSearchQaTests.NewInvoice(sale.ZoneId, accountId, code: $"HDX{n:D5}")).ToList();
        await api.UseDbAsync(db => db.SalesInvoices.AddRange(invoices));

        var atTheLimit = await sale.Manager.Client.ExportInvoicesAsync();
        Assert.Equal(HttpStatusCode.OK, atTheLimit.StatusCode);
        using (var workbook = await WorkbookAsync(atTheLimit))
        {
            Assert.Equal(SalesInvoiceSearchService.MaxExportRows + 2, workbook.Worksheet(SalesInvoiceExportSheets.ListSheet).LastRowUsed()!.RowNumber());
        }

        await api.UseDbAsync(db => db.SalesInvoices.Add(SalesInvoiceSearchQaTests.NewInvoice(sale.ZoneId, accountId, code: "HDXLAST")));

        var refused = await sale.Manager.Client.ExportInvoicesAsync();
        var problem = await refused.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Filters"), (problem.Text("code"), problem.Text("field")));
        Assert.Contains("Narrow the period or the filters", problem.Text("detail"));
        // The screen still lists them: only the file is limited, and narrowing the filters opens it again.
        Assert.Equal(SalesInvoiceSearchService.MaxExportRows + 1, (await sale.Manager.Client.InvoiceListAsync()).GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await sale.Manager.Client.ExportInvoicesAsync("?code=HDXLAST")).StatusCode);
    }

    // PM answer 12/11/2026 (OMTS-224 [5.5]): the filter by the code (DH) of the purchase request the invoice fulfils, and the code in the list and the file.
    [Fact]
    public async Task The_order_code_filters_the_list_and_the_file_and_shows_in_both()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var ordered = await client.SoldAsync(sale, sale.LotId, 2m);
        var plain = await client.SoldAsync(sale, sale.LotId, 3m);
        var orderCode = "DH" + AcsTestKit.NewCode();
        var request = new PurchaseRequest
        {
            Code = orderCode, CustomerId = sale.CustomerId, ProductId = sale.ProductId, ProductName = "Oyster", RequestedCages = 10, PickupDate = PurchaseRequestTestKit.Today, SentAt = DateTimeOffset.UtcNow,
        };
        await api.UseDbAsync(db => db.PurchaseRequests.Add(request));
        await api.UpdateAsync<SalesInvoice>(ordered.Id(), i => i.PurchaseRequestId = request.Id);

        var list = await client.InvoiceListAsync(Query(("orderCode", orderCode)));
        Assert.Equal([ordered.Text("code")], list.Codes());
        Assert.Equal(orderCode, list.Rows()[0].Text("orderCode"));
        Assert.Equal([ordered.Text("code")], (await client.InvoiceListAsync(Query(("orderCode", " " + orderCode[..4] + " ")))).Codes());
        var wildcard = await client.InvoiceListAsync(Query(("orderCode", "DH%")));
        Assert.Equal(("MSG-GEN-10", 0), (wildcard.Text("messageCode"), wildcard.Rows().Count));
        var all = await client.InvoiceListAsync();
        Assert.Equal(2, all.Rows().Count);
        Assert.Equal(JsonValueKindNull, all.Rows().Single(r => r.Text("code") == plain.Text("code")).GetProperty("orderCode").ValueKind);

        using var file = await WorkbookAsync(await client.ExportInvoicesAsync(Query(("orderCode", orderCode))));
        var sheet = file.Worksheet(SalesInvoiceExportSheets.ListSheet);
        Assert.Equal(("Order", ordered.Text("code"), orderCode, 3), (sheet.Cell(1, 11).GetString(), sheet.Cell(2, 1).GetString(), sheet.Cell(2, 11).GetString(), sheet.LastRowUsed()!.RowNumber()));
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    // No formula injection, in the list and the invoice file.
    [Fact]
    public async Task A_name_that_looks_like_a_formula_stays_text_in_both_files()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        foreach (var name in new[] { "=HYPERLINK(\"https://example.invalid\",\"DEMO\")", "+1+1", "-1+1", "@SUM(A1)" })
        {
            await api.UpdateAsync<Customer>(sale.CustomerId, c => c.Name = name);
            var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

            using var list = await WorkbookAsync(await sale.Owner.Client.ExportInvoicesAsync());
            var cell = list.Worksheet(SalesInvoiceExportSheets.ListSheet).Cell(2, 4);
            Assert.Equal((false, XLDataType.Text, name), (cell.HasFormula, cell.DataType, cell.GetString()));

            using var print = await WorkbookAsync(await sale.Owner.Client.PrintInvoiceAsync(invoice.Id()));
            var value = print.Worksheet(SalesInvoiceExportSheets.InvoiceSheet).Cell(5, 2);
            Assert.Equal((false, name), (value.HasFormula, value.GetString()));
        }
    }
}
