using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-01: who may open it (§I.4.4), MSG-GEN-01 for a wrong period, grouping or choice, and the file of REPORT-01.4.</summary>
public class SalesPerformanceRulesTests
{
    private static async Task<HttpResponseMessage[]> AllCallsAsync(HttpClient client) =>
    [
        await client.GetReportAsync(), await client.GetGroupInvoicesAsync("groupBy=customer&groupKey=walk-in"), await client.GetExportAsync(),
    ];

    [Fact]
    public async Task The_owner_and_the_accountant_open_the_report_and_nobody_else_does()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 1m);
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

    // REPORT-01.0.E1 and the choices: MSG-GEN-01 names every field to fix.
    [Fact]
    public async Task A_wrong_period_grouping_or_choice_answers_msg_gen_01_naming_the_fields()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var unknown = Guid.NewGuid();

        Assert.Equal(["From date"], await (await owner.GetReportAsync("from=2026-11-10&to=2026-11-01")).InvalidFieldsAsync());
        Assert.Equal(["To date"], await (await owner.GetReportAsync("from=2020-01-01&to=2026-11-01")).InvalidFieldsAsync());
        Assert.Equal(["Grouping"], await (await owner.GetReportAsync("groupBy=quarter")).InvalidFieldsAsync());
        Assert.Equal(["Grouping"], await (await owner.GetReportAsync("groupBy=1")).InvalidFieldsAsync());
        Assert.Equal(["Zone"], await (await owner.GetReportAsync($"zoneIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["Customer"], await (await owner.GetReportAsync($"customerIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["Product"], await (await owner.GetReportAsync($"productIds={sale.ZoneId}")).InvalidFieldsAsync());
        Assert.Equal(["Customer"], await (await owner.GetReportAsync($"customerIds={sale.CustomerId}&customerIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["Grouping", "From date", "Zone"], await (await owner.GetReportAsync($"groupBy=x&from=2026-11-10&to=2026-11-01&zoneIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["From date"], await (await owner.GetGroupInvoicesAsync("from=2026-11-10&to=2026-11-01&groupBy=customer&groupKey=walk-in")).InvalidFieldsAsync());
        Assert.Equal(["From date"], await (await owner.GetExportAsync("from=2026-11-10&to=2026-11-01")).InvalidFieldsAsync());
        // An unknown id of a user who may not read any zone is still the right's answer first.
        Assert.Equal(HttpStatusCode.Forbidden, (await sale.Manager.Client.GetReportAsync($"zoneIds={unknown}")).StatusCode);
    }

    // REPORT-01.4: the figures shown, with the period and the choices. Text is never a formula; the figures are numbers.
    [Fact]
    public async Task The_export_holds_the_figures_the_period_and_the_choices()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;
        const string formulaName = "=SUM(1,2)";
        var buyer = (await sale.Owner.Client.CreateCustomerAsync()).Id();
        // A name a user typed that would be a formula in a spreadsheet: written straight in the database, as the other export tests do.
        await api.UpdateAsync<Customer>(buyer, c => c.Name = formulaName);
        await sale.SellKgAsync(buyer, 10m);
        await sale.SellKgAsync(buyer, 2m, sale.OtherLotId);
        var from = today.AddDays(-3);

        var file = await sale.Owner.Client.GetExportAsync($"{Period(from, today)}&groupBy=customer&zoneIds={sale.ZoneId}&customerIds={buyer}");

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"sales-performance-customer-{from:yyyyMMdd}-{today:yyyyMMdd}.xlsx", file.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var figures = book.Worksheet("Sales performance");
        Assert.Equal(["Customer", "Invoices", "Invoiced kg", "Revenue", "Price cuts", "Sales returns", "Net revenue", "Sold kg", "Net money received", "Receivable at end of period", "Owed back at end of period", "Net debt at end of period"],
            figures.Row(1).Cells(1, 12).Select(c => c.GetString()));
        var row = figures.Row(2);
        Assert.False(row.Cell(1).HasFormula);
        Assert.Equal((XLDataType.Text, formulaName), (row.Cell(1).DataType, row.Cell(1).GetString()));
        Assert.Equal((2d, 12d, 360_000d), (row.Cell(2).GetDouble(), row.Cell(3).GetDouble(), row.Cell(4).GetDouble()));
        Assert.Equal(XLDataType.Number, row.Cell(7).DataType);
        Assert.Equal(("Total", 360_000d, 360_000d), (figures.Row(3).Cell(1).GetString(), figures.Row(3).Cell(4).GetDouble(), figures.Row(3).Cell(10).GetDouble()));

        var choices = book.Worksheet("Choices").RowsUsed().Skip(1).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2));
        Assert.Equal(from, DateOnly.FromDateTime(choices["Period from"].GetDateTime()));
        Assert.Equal(today, DateOnly.FromDateTime(choices["Period to"].GetDateTime()));
        Assert.Equal("Customer", choices["Grouping"].GetString());
        Assert.Equal((await api.ZoneEntityAsync(sale.ZoneId)).Name, choices["Zones"].GetString());
        Assert.Equal(formulaName, choices["Customers"].GetString());
        Assert.False(choices["Customers"].HasFormula);
        Assert.Equal("All", choices["Products"].GetString());
    }

    [Fact]
    public async Task An_empty_selection_exports_the_headers_and_a_zero_total_and_a_product_export_has_no_customer_money_columns()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;

        var file = await sale.Owner.Client.GetExportAsync($"{Period(today.AddDays(-9), today.AddDays(-8))}&groupBy=product");

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var figures = book.Worksheet("Sales performance");
        Assert.Equal(8, figures.Row(1).CellsUsed().Count());
        Assert.Equal("Product", figures.Cell(1, 1).GetString());
        Assert.Equal(("Total", 0d, 0d), (figures.Cell(2, 1).GetString(), figures.Cell(2, 2).GetDouble(), figures.Cell(2, 4).GetDouble()));
        Assert.True(figures.Cell(3, 1).IsEmpty());
    }
}
