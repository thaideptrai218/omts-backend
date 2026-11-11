using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.PostSaleIssuesTestKit;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-03: who may open it (§I.4.4), MSG-GEN-01 for a wrong period, grouping or choice, and the file of REPORT-03.3.</summary>
public class PostSaleIssuesRulesTests
{
    private static async Task<HttpResponseMessage[]> AllCallsAsync(HttpClient client) =>
    [
        await client.GetPostSaleAsync(), await client.GetPostSaleItemsAsync("groupBy=customer&groupKey=walk-in"), await client.GetPostSaleExportAsync(),
    ];

    [Fact]
    public async Task The_owner_and_the_accountant_open_the_report_and_nobody_else_does()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var allowed = new[]
        {
            sale.Owner.Client,
            (await api.StaffAsync(await api.ZoneEntityAsync(sale.ZoneId), StaffRole.Accountant)).Client,
            (await api.StaffAsync(null, StaffRole.Accountant)).Client,
        };
        var refused = new[]
        {
            sale.Manager.Client,
            (await api.StaffAsync(null, StaffRole.ZoneManager)).Client,
            (await PurchaseRequestTestKit.TraderAsync(api)).Client,
        };

        foreach (var client in allowed)
        {
            foreach (var response in await AllCallsAsync(client))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        foreach (var client in refused)
        {
            foreach (var response in await AllCallsAsync(client))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }
        }

        foreach (var response in await AllCallsAsync(api.CreateClient()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // REPORT-03.0.E1 and the choices: MSG-GEN-01 names every field to fix.
    [Fact]
    public async Task A_wrong_period_grouping_or_choice_answers_msg_gen_01_naming_the_fields()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var unknown = Guid.NewGuid();

        Assert.Equal(["From date"], await (await owner.GetPostSaleAsync("from=2026-11-10&to=2026-11-01")).InvalidFieldsAsync());
        Assert.Equal(["To date"], await (await owner.GetPostSaleAsync("from=2020-01-01&to=2026-11-01")).InvalidFieldsAsync());
        Assert.Equal(["Grouping"], await (await owner.GetPostSaleAsync("groupBy=product")).InvalidFieldsAsync());
        Assert.Equal(["Grouping"], await (await owner.GetPostSaleAsync("groupBy=1")).InvalidFieldsAsync());
        Assert.Equal(["Zone"], await (await owner.GetPostSaleAsync($"zoneIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["Customer"], await (await owner.GetPostSaleAsync($"customerIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["Customer"], await (await owner.GetPostSaleAsync($"customerIds={sale.CustomerId}&customerIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["Grouping", "From date", "Zone"], await (await owner.GetPostSaleAsync($"groupBy=x&from=2026-11-10&to=2026-11-01&zoneIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["From date"], await (await owner.GetPostSaleExportAsync("from=2026-11-10&to=2026-11-01")).InvalidFieldsAsync());
        Assert.Equal(["Grouping"], await (await owner.GetPostSaleExportAsync("groupBy=quarter")).InvalidFieldsAsync());

        // The events of a group need the key of a group.
        Assert.Equal(["Group key"], await (await owner.GetPostSaleItemsAsync("groupBy=customer")).InvalidFieldsAsync());
        Assert.Equal(["Grouping", "Group key"], await (await owner.GetPostSaleItemsAsync("groupBy=x")).InvalidFieldsAsync());
        Assert.Equal(["From date"], await (await owner.GetPostSaleItemsAsync("from=2026-11-10&to=2026-11-01&groupBy=customer&groupKey=walk-in")).InvalidFieldsAsync());
        // An unknown id of a user who may not read any zone is still the right's answer first.
        Assert.Equal(HttpStatusCode.Forbidden, (await sale.Manager.Client.GetPostSaleAsync($"zoneIds={unknown}")).StatusCode);
    }

    // REPORT-03.3: the figures shown, with the period and the choices. Text is never a formula; the figures are numbers.
    [Fact]
    public async Task The_export_holds_the_groups_a_total_row_the_period_and_the_choices()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;
        const string formulaName = "=SUM(1,2)";
        var buyer = (await sale.Owner.Client.CreateCustomerAsync()).Id();
        // A name a user typed that would be a formula in a spreadsheet: written straight in the database, as the other export tests do.
        await api.UpdateAsync<Customer>(buyer, c => c.Name = formulaName);
        var cut = (await sale.SellKgAsync(buyer, 10m)).Id();
        await sale.CutPriceAsync(cut, 20_000m);
        await sale.CancelSaleAsync((await sale.SellKgAsync(buyer, 4m)).Id());
        var from = today.AddDays(-3);

        var file = await sale.Owner.Client.GetPostSaleExportAsync($"{Period(from, today)}&groupBy=customer&zoneIds={sale.ZoneId}&customerIds={buyer}");

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"post-sale-issues-customer-{from:yyyyMMdd}-{today:yyyyMMdd}.xlsx", file.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var figures = book.Worksheet("Post-sale issues");
        Assert.Equal(
            ["Customer", "Claims sent", "Claims awaiting response", "Claims rejected", "Claims accepted", "Sales returns", "Returned kg", "Discarded kg", "Returns value", "Price cuts",
                "Price cuts amount", "Cancelled invoices", "Cancelled total"],
            figures.Row(1).Cells(1, 13).Select(c => c.GetString()));
        var row = figures.Row(2);
        Assert.False(row.Cell(1).HasFormula);
        Assert.Equal((XLDataType.Text, formulaName), (row.Cell(1).DataType, row.Cell(1).GetString()));
        Assert.Equal((0d, 0d, 1d, 20_000d, 1d, 120_000d), (row.Cell(2).GetDouble(), row.Cell(6).GetDouble(), row.Cell(10).GetDouble(), row.Cell(11).GetDouble(), row.Cell(12).GetDouble(), row.Cell(13).GetDouble()));
        Assert.Equal(XLDataType.Number, row.Cell(11).DataType);
        Assert.Equal(("Total", 1d, 20_000d, 120_000d), (figures.Cell(3, 1).GetString(), figures.Cell(3, 10).GetDouble(), figures.Cell(3, 11).GetDouble(), figures.Cell(3, 13).GetDouble()));
        Assert.True(figures.Cell(4, 1).IsEmpty());

        var choices = book.Worksheet("Choices").RowsUsed().Skip(1).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2));
        Assert.Equal(from, DateOnly.FromDateTime(choices["Period from"].GetDateTime()));
        Assert.Equal(today, DateOnly.FromDateTime(choices["Period to"].GetDateTime()));
        Assert.Equal("Customer", choices["Grouping"].GetString());
        Assert.Equal((await api.ZoneEntityAsync(sale.ZoneId)).Name, choices["Zones"].GetString());
        Assert.Equal(formulaName, choices["Customers"].GetString());
        Assert.False(choices["Customers"].HasFormula);
        Assert.Equal("All", choices["Products"].GetString());
    }

    // By lot, what is not split between lots is an empty cell, not a 0 that would read as "none".
    [Fact]
    public async Task The_export_by_lot_leaves_the_whole_invoice_figures_empty_and_an_empty_selection_exports_a_zero_total()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = (await sale.SellKgAsync(sale.CustomerId, 10m)).Id();
        await sale.ReturnAsync(invoice);
        var today = PurchaseRequestTestKit.Today;

        var byLot = await sale.Owner.Client.GetPostSaleExportAsync("groupBy=lot");
        var empty = await sale.Owner.Client.GetPostSaleExportAsync($"{Period(today.AddDays(-9), today.AddDays(-8))}&groupBy=zone");

        using var book = new XLWorkbook(await byLot.Content.ReadAsStreamAsync());
        var figures = book.Worksheet("Post-sale issues");
        Assert.Equal((1d, 10d), (figures.Cell(2, 6).GetDouble(), figures.Cell(2, 7).GetDouble()));
        Assert.True(figures.Cell(2, 2).IsEmpty());
        Assert.True(figures.Cell(2, 9).IsEmpty());
        Assert.True(figures.Cell(2, 13).IsEmpty());
        Assert.Equal(("Total", 0d, 300_000d), (figures.Cell(3, 1).GetString(), figures.Cell(3, 2).GetDouble(), figures.Cell(3, 9).GetDouble()));

        using var emptyBook = new XLWorkbook(await empty.Content.ReadAsStreamAsync());
        var emptyFigures = emptyBook.Worksheet("Post-sale issues");
        Assert.Equal(13, emptyFigures.Row(1).CellsUsed().Count());
        Assert.Equal(("Total", 0d, 0d), (emptyFigures.Cell(2, 1).GetString(), emptyFigures.Cell(2, 2).GetDouble(), emptyFigures.Cell(2, 13).GetDouble()));
        Assert.True(emptyFigures.Cell(3, 1).IsEmpty());
    }
}
