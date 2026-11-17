using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-02: who may open it (§I.4.4), MSG-GEN-01 for a wrong period, unit or choice, and the file of REPORT-02.3.</summary>
public class PriceTrendsRulesTests
{
    private static async Task<HttpResponseMessage[]> AllCallsAsync(HttpClient client, Guid productId) =>
    [
        await client.GetPriceTrendsAsync(), await client.GetPriceTrendLinesAsync($"productId={productId}&periodKey={Day(PurchaseRequestTestKit.Today)}"), await client.GetPriceTrendsExportAsync(),
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
            foreach (var response in await AllCallsAsync(client, sale.ProductId))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        foreach (var client in refused)
        {
            foreach (var response in await AllCallsAsync(client, sale.ProductId))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }
        }

        foreach (var response in await AllCallsAsync(api.CreateClient(), sale.ProductId))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // REPORT-02.0.E1 and the choices: MSG-GEN-01 names every field to fix.
    [Fact]
    public async Task A_wrong_period_unit_or_choice_answers_msg_gen_01_naming_the_fields()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var unknown = Guid.NewGuid();
        var key = $"productId={sale.ProductId}&periodKey=2026-10-12";

        Assert.Equal(["From date"], await (await owner.GetPriceTrendsAsync("from=2026-11-10&to=2026-11-01")).InvalidFieldsAsync());
        Assert.Equal(["To date"], await (await owner.GetPriceTrendsAsync("from=2020-01-01&to=2026-11-01")).InvalidFieldsAsync());
        Assert.Equal(["Time unit"], await (await owner.GetPriceTrendsAsync("unit=year")).InvalidFieldsAsync());
        Assert.Equal(["Time unit"], await (await owner.GetPriceTrendsAsync("unit=1")).InvalidFieldsAsync());
        Assert.Equal(["Zone"], await (await owner.GetPriceTrendsAsync($"zoneIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["Product"], await (await owner.GetPriceTrendsAsync($"productIds={sale.ZoneId}")).InvalidFieldsAsync());
        Assert.Equal(["Product"], await (await owner.GetPriceTrendsAsync($"productIds={sale.ProductId}&productIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["Time unit", "From date", "Zone"], await (await owner.GetPriceTrendsAsync($"unit=x&from=2026-11-10&to=2026-11-01&zoneIds={unknown}")).InvalidFieldsAsync());
        Assert.Equal(["From date"], await (await owner.GetPriceTrendsExportAsync("from=2026-11-10&to=2026-11-01")).InvalidFieldsAsync());
        Assert.Equal(["Time unit"], await (await owner.GetPriceTrendsExportAsync("unit=year")).InvalidFieldsAsync());

        // The lines of a period need a product that exists and a period key (a date) too.
        Assert.Equal(["Product"], await (await owner.GetPriceTrendLinesAsync("periodKey=2026-10-12")).InvalidFieldsAsync());
        Assert.Equal(["Period key"], await (await owner.GetPriceTrendLinesAsync($"productId={sale.ProductId}")).InvalidFieldsAsync());
        Assert.Equal(["Period key"], await (await owner.GetPriceTrendLinesAsync($"productId={sale.ProductId}&periodKey=October")).InvalidFieldsAsync());
        Assert.Equal(["Product", "Period key"], await (await owner.GetPriceTrendLinesAsync("periodKey=x")).InvalidFieldsAsync());
        Assert.Equal(["Product"], await (await owner.GetPriceTrendLinesAsync($"productId={unknown}&periodKey=2026-10-12")).InvalidFieldsAsync());
        Assert.Equal(["Time unit"], await (await owner.GetPriceTrendLinesAsync($"{key}&unit=year")).InvalidFieldsAsync());
        Assert.Equal(["From date"], await (await owner.GetPriceTrendLinesAsync($"{key}&from=2026-11-10&to=2026-11-01")).InvalidFieldsAsync());
        // An unknown id of a user who may not read any zone is still the right's answer first.
        Assert.Equal(HttpStatusCode.Forbidden, (await sale.Manager.Client.GetPriceTrendsAsync($"zoneIds={unknown}")).StatusCode);
    }

    // REPORT-02.3: the figures shown, with the period and the choices. Text is never a formula; the figures are numbers.
    [Fact]
    public async Task The_export_holds_one_row_per_product_and_period_and_the_period_and_the_choices()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;
        const string formulaName = "=SUM(1,2)";
        // A name a user typed that would be a formula in a spreadsheet: written straight in the database, as the other export tests do.
        await api.UpdateAsync<Product>(sale.ProductId, p => p.Name = formulaName);
        await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 30_000m);
        await sale.SellKgAsync(sale.CustomerId, 30m, unitPrice: 20_000m);
        var from = today.AddDays(-3);

        var file = await sale.Owner.Client.GetPriceTrendsExportAsync($"{Period(from, today)}&unit=week&zoneIds={sale.ZoneId}&productIds={sale.ProductId}");

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"price-trends-week-{from:yyyyMMdd}-{today:yyyyMMdd}.xlsx", file.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var figures = book.Worksheet("Price trends");
        Assert.Equal(["Product", "Period", "Sold kg", "Average unit price", "Lowest unit price", "Highest unit price"], figures.Row(1).Cells(1, 6).Select(c => c.GetString()));
        var row = figures.Row(2);
        Assert.False(row.Cell(1).HasFormula);
        Assert.Equal((XLDataType.Text, formulaName), (row.Cell(1).DataType, row.Cell(1).GetString()));
        Assert.Contains("-W", row.Cell(2).GetString());
        Assert.Equal((40d, 22_500d, 20_000d, 30_000d), (row.Cell(3).GetDouble(), row.Cell(4).GetDouble(), row.Cell(5).GetDouble(), row.Cell(6).GetDouble()));
        Assert.Equal(XLDataType.Number, row.Cell(4).DataType);
        Assert.True(figures.Cell(3, 1).IsEmpty());

        var choices = book.Worksheet("Choices").RowsUsed().Skip(1).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2));
        Assert.Equal(from, DateOnly.FromDateTime(choices["Period from"].GetDateTime()));
        Assert.Equal(today, DateOnly.FromDateTime(choices["Period to"].GetDateTime()));
        Assert.Equal("Week", choices["Time unit"].GetString());
        Assert.Equal((await api.ZoneEntityAsync(sale.ZoneId)).Name, choices["Zones"].GetString());
        Assert.Equal(formulaName, choices["Products"].GetString());
        Assert.False(choices["Products"].HasFormula);
        Assert.Equal("All", choices["Customers"].GetString());
    }

    [Fact]
    public async Task An_empty_selection_exports_the_headers_only()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;

        var file = await sale.Owner.Client.GetPriceTrendsExportAsync(Period(today.AddDays(-9), today.AddDays(-8)));

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var figures = book.Worksheet("Price trends");
        Assert.Equal(6, figures.Row(1).CellsUsed().Count());
        Assert.True(figures.Cell(2, 1).IsEmpty());
    }
}
