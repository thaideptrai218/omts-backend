using System.IO.Compression;
using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Independent QA of SALE-09.2: the file obeys the same filters, every page, formula-looking text stays text, the file name is safe.</summary>
public class SalesInvoiceExportQaTests
{
    private static readonly DateOnly Today = PurchaseRequestTestKit.Today;

    private static async Task<XLWorkbook> WorkbookAsync(HttpResponseMessage file)
    {
        Assert.True(file.StatusCode == HttpStatusCode.OK, await file.Content.ReadAsStringAsync());
        return new XLWorkbook(await file.Content.ReadAsStreamAsync());
    }

    private static IXLCell TotalRowCell(XLWorkbook workbook, string label)
    {
        foreach (var sheet in workbook.Worksheets)
        {
            foreach (var row in sheet.RowsUsed())
            {
                if (row.Cell(1).GetString() == label)
                {
                    return row.Cell(sheet.Name == SalesInvoiceExportSheets.ListSheet ? 5 : 7);
                }
            }
        }

        throw new InvalidOperationException(label);
    }

    // SALE-09.2: "the list and its total", every page; POST-2 "the list on the screen".
    [Fact]
    public async Task The_file_holds_every_match_of_the_filters_with_the_total_of_the_screen()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseDbAsync(db => db.SalesInvoices.AddRange(Enumerable.Range(0, 130).Select(_ => SalesInvoiceSearchQaTests.NewInvoice(sale.ZoneId, sale.Owner.Account.Id))));
        var owner = sale.Owner.Client;
        var list = await owner.InvoiceListAsync("?pageSize=100");

        using var file = await WorkbookAsync(await owner.ExportInvoicesAsync());

        var sheet = file.Worksheet(SalesInvoiceExportSheets.ListSheet);
        Assert.Equal((100, 130), (list.Rows().Count, list.GetProperty("page").GetProperty("total").GetInt32()));
        Assert.Equal(130 + 2, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal(list.Money("completedTotal"), TotalRowCell(file, "Total of Completed invoices").GetValue<decimal>());
    }

    // SALE-09.2: same filters as the screen: paid, status, zone, code, cash.
    [Fact]
    public async Task The_file_obeys_the_same_filters_as_the_screen()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        var other = (await sale.Owner.Client.CreateCustomerAsync()).Id();
        var unpaid = (await manager.SellAsync(sale.PayLater(sale.LotId, 1m))).GetProperty("invoice");
        var paid = await manager.SoldAsync(sale, sale.LotId, 2m, customerId: other);
        var cancelled = await manager.SoldAsync(sale, sale.LotId, 4m, walkIn: true);
        await api.UpdateAsync<SalesInvoice>(cancelled.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        foreach (var (query, codes, total) in new (string Query, string?[] Codes, decimal Total)[]
                 {
                     ("?paid=false", [unpaid.Text("code")], ListPrice),
                     ("?paid=true", [paid.Text("code")], 2 * ListPrice),
                     ($"?statusId={StatusIds.SalesInvoice.Cancelled}", [cancelled.Text("code")], 0m),
                     ($"?customerId={other}", [paid.Text("code")], 2 * ListPrice),
                     ($"?code={paid.Text("code")![2..]}", [paid.Text("code")], 2 * ListPrice),
                     ($"?harvestLotId={sale.OtherLotId}", [], 0m),
                 })
        {
            var list = await manager.InvoiceListAsync(query);
            Assert.Equal(codes, list.Codes());
            using var file = await WorkbookAsync(await manager.ExportInvoicesAsync(query));
            var sheet = file.Worksheet(SalesInvoiceExportSheets.ListSheet);
            Assert.Equal(codes.Length + 2, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal(codes.Select(c => c), Enumerable.Range(2, codes.Length).Select(r => (string?)sheet.Cell(r, 1).GetString()));
            Assert.Equal(total, TotalRowCell(file, "Total of Completed invoices").GetValue<decimal>());
        }

        var wrong = await manager.ExportInvoicesAsync($"?from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}");
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (wrong.StatusCode, await wrong.ErrorCodeAsync()));
    }

    public static TheoryData<string> Formulas() => new()
    {
        "=1+1", "+1+1", "-1+1", "@SUM(A1)", "\t=1+1", "\r=1+1", "=cmd|' /C calc'!A0", "=HYPERLINK(\"https://example.invalid\",\"DEMO\")",
    };

    // Formula injection in every free-text column of both files: the raw sheet holds no formula element at all.
    [Theory]
    [MemberData(nameof(Formulas))]
    public async Task Text_that_looks_like_a_formula_is_never_a_formula_in_the_raw_files(string text)
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UpdateAsync<Customer>(sale.CustomerId, c => c.Name = text);
        await api.UpdateAsync<FarmingZone>(sale.ZoneId, z => z.Name = text);
        await api.UpdateAsync<Product>(sale.ProductId, p => p.Name = text);
        await api.UpdateAsync<HarvestLot>(sale.LotId, l => l.LotCode = text);
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);

        foreach (var response in new[] { await sale.Owner.Client.ExportInvoicesAsync(), await sale.Owner.Client.PrintInvoiceAsync(invoice.Id()) })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
            foreach (var part in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(part.Open());
                var xml = await reader.ReadToEndAsync();
                Assert.DoesNotContain("<f>", xml);
                Assert.DoesNotContain("<f ", xml);
            }
        }

        using var list = await WorkbookAsync(await sale.Owner.Client.ExportInvoicesAsync());
        var sheet = list.Worksheet(SalesInvoiceExportSheets.ListSheet);
        Assert.Equal((XLDataType.Text, XLDataType.Text), (sheet.Cell(2, 3).DataType, sheet.Cell(2, 4).DataType));
        // XML reads a carriage return as a line feed; the text after it is what matters.
        Assert.Equal(text.TrimStart('\t', '\r', '\n'), sheet.Cell(2, 4).GetString().TrimStart('\t', '\r', '\n'));
    }

    // A name that came from a trader's own registration or another system: the whole file must not be lost for one control character.
    [Fact]
    public async Task A_control_character_in_a_customer_name_does_not_lose_the_file()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UpdateAsync<Customer>(sale.CustomerId, c => c.Name = "DEMO\u0001\u0008\u000BNAME");
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        using var list = await WorkbookAsync(await sale.Owner.Client.ExportInvoicesAsync());
        using var print = await WorkbookAsync(await sale.Owner.Client.PrintInvoiceAsync(invoice.Id()));

        Assert.Contains("NAME", list.Worksheet(SalesInvoiceExportSheets.ListSheet).Cell(2, 4).GetString());
        Assert.Contains("NAME", print.Worksheet(SalesInvoiceExportSheets.InvoiceSheet).Cell(5, 2).GetString());
    }

    [Fact]
    public async Task The_file_names_are_safe_and_the_files_are_xlsx()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        var list = await sale.Owner.Client.ExportInvoicesAsync();
        var print = await sale.Owner.Client.PrintInvoiceAsync(invoice.Id());

        const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        Assert.Equal((Xlsx, Xlsx), (list.Content.Headers.ContentType!.MediaType, print.Content.Headers.ContentType!.MediaType));
        Assert.Matches("^sales-invoices-[0-9]{8}-[0-9]{8}\\.xlsx$", list.Content.Headers.ContentDisposition!.FileName!);
        Assert.Matches("^sales-invoice-[A-Za-z0-9]+\\.xlsx$", print.Content.Headers.ContentDisposition!.FileName!);
    }

    // SALE-09.1 step 2: the printed invoice shows the price cuts and the latest total.
    [Fact]
    public async Task The_printed_invoice_shows_the_price_cuts_and_the_latest_total()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await api.UpdateAsync<SalesInvoice>(invoice.Id(), i => (i.PriceCutTotal, i.Total) = (100_000m, 200_000m));

        using var file = await WorkbookAsync(await sale.Manager.Client.PrintInvoiceAsync(invoice.Id()));

        Assert.Equal(100_000m, TotalRowCell(file, "Price cuts").GetValue<decimal>());
        Assert.Equal(200_000m, file.Worksheet(SalesInvoiceExportSheets.LinesSheet).Column(7).CellsUsed().Last().GetValue<decimal>());
    }

    // Print of a Cancelled invoice: SALE-09.1 step 3 limits Print by no state.
    [Fact]
    public async Task A_cancelled_invoice_prints_with_no_paid_state()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId, walkIn: true);
        await api.UpdateAsync<SalesInvoice>(invoice.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        using var file = await WorkbookAsync(await sale.Owner.Client.PrintInvoiceAsync(invoice.Id()));

        var heading = file.Worksheet(SalesInvoiceExportSheets.InvoiceSheet);
        var paidRow = heading.RowsUsed().Single(r => r.Cell(1).GetString() == "Paid");
        Assert.Equal(string.Empty, paidRow.Cell(2).GetString());
    }
}
