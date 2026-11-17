using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-02 by day, week and month (ISO weeks of UTC+7), the zones of the user (BR-FARM-08), and the invoice lines of a product and period (REPORT-02.2).</summary>
public class PriceTrendsPeriodZoneAndLinesTests
{
    // 2026-10-31 is a Saturday, 2026-11-01 a Sunday and 2026-11-02 the Monday that starts ISO week 45.
    [Fact]
    public async Task Days_weeks_and_months_group_the_lines_by_the_business_date_with_the_price_figures_of_each()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var saturday = await sale.SellKgAsync(sale.CustomerId, 2m, unitPrice: 10_000m);
        var sunday = await sale.SellKgAsync(sale.CustomerId, 2m, unitPrice: 20_000m);
        var monday = await sale.SellKgAsync(sale.CustomerId, 3m, unitPrice: 30_000m);
        await api.DateAsync(saturday.Id(), new DateOnly(2026, 10, 31));
        await api.DateAsync(sunday.Id(), new DateOnly(2026, 11, 1));
        await api.DateAsync(monday.Id(), new DateOnly(2026, 11, 2));
        var period = Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30));

        var days = (await sale.Owner.Client.PriceTrendsAsync($"{period}&unit=day")).TrendSeries().Single().TrendPeriods();
        var weeks = (await sale.Owner.Client.PriceTrendsAsync($"{period}&unit=week")).TrendSeries().Single().TrendPeriods();
        var months = (await sale.Owner.Client.PriceTrendsAsync($"{period}&unit=MONTH")).TrendSeries().Single().TrendPeriods();

        Assert.Equal(["2026-10-31", "2026-11-01", "2026-11-02"], days.Select(p => p.Text("key")));
        Assert.Equal([(2m, 10_000m, 10_000m, 10_000m), (2m, 20_000m, 20_000m, 20_000m), (3m, 30_000m, 30_000m, 30_000m)], days.Select(p => p.PriceFigures()));
        // The Sunday is in the week of the Monday before it.
        Assert.Equal(["2026-10-26", "2026-11-02"], weeks.Select(p => p.Text("key")));
        Assert.Equal(["2026-W44", "2026-W45"], weeks.Select(p => p.Text("label")));
        Assert.Equal([(4m, 15_000m, 10_000m, 20_000m), (3m, 30_000m, 30_000m, 30_000m)], weeks.Select(p => p.PriceFigures()));
        Assert.Equal(["2026-10-01", "2026-11-01"], months.Select(p => p.Text("key")));
        Assert.Equal(["2026-10", "2026-11"], months.Select(p => p.Text("label")));
        Assert.Equal([(2m, 10_000m, 10_000m, 10_000m), (5m, 26_000m, 20_000m, 30_000m)], months.Select(p => p.PriceFigures()));
    }

    // Product A is sold in both zones: 10 kg at 30,000 in zone A and 10 kg at 50,000 in zone B (the zone of the invoice).
    [Fact]
    public async Task The_zone_filter_and_the_zones_of_the_user_limit_the_invoices_counted_and_a_zone_outside_theirs_is_refused()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        await a.SellKgAsync(a.CustomerId, 10m, unitPrice: 30_000m);
        await (b with { ProductId = a.ProductId }).SellKgAsync(b.CustomerId, 10m, unitPrice: 50_000m);
        var accountantA = (await api.StaffAsync(await api.ZoneEntityAsync(a.ZoneId), StaffRole.Accountant)).Client;
        var accountantAll = (await api.StaffAsync(null, StaffRole.Accountant)).Client;

        var all = await a.Owner.Client.PriceTrendsAsync();
        var onlyA = await a.Owner.Client.PriceTrendsAsync($"zoneIds={a.ZoneId}");
        var own = await accountantA.PriceTrendsAsync();

        Assert.Equal((20m, 40_000m, 30_000m, 50_000m), Assert.Single(Assert.Single(all.TrendSeries()).TrendPeriods()).PriceFigures());
        Assert.Equal((10m, 30_000m, 30_000m, 30_000m), Assert.Single(Assert.Single(onlyA.TrendSeries()).TrendPeriods()).PriceFigures());
        Assert.Equal((10m, 30_000m, 30_000m, 30_000m), Assert.Single(Assert.Single(own.TrendSeries()).TrendPeriods()).PriceFigures());
        Assert.Equal(20m, Assert.Single(Assert.Single((await accountantAll.PriceTrendsAsync()).TrendSeries()).TrendPeriods()).Money("soldKg"));
        Assert.Equal(a.ZoneId, Assert.Single(onlyA.GetProperty("filters").GetProperty("zones").EnumerateArray()).Id());

        foreach (var query in new[] { $"zoneIds={b.ZoneId}", $"zoneIds={a.ZoneId}&zoneIds={b.ZoneId}" })
        {
            var refused = await accountantA.GetPriceTrendsAsync(query);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await accountantA.GetPriceTrendsExportAsync($"zoneIds={b.ZoneId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountantA.GetPriceTrendLinesAsync($"productId={a.ProductId}&periodKey={Day(PurchaseRequestTestKit.Today)}&zoneIds={b.ZoneId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountantA.GetPriceTrendsExportAsync($"zoneIds={a.ZoneId}")).StatusCode);
    }

    // 2026-10-12 and 2026-10-19 are Mondays. Two lines on the 12th (one for a walk-in customer) and one on the 20th.
    [Fact]
    public async Task The_lines_of_a_period_are_listed_with_their_invoice_customer_kg_and_unit_price_newest_first()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var first = await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 30_000m);
        var walkIn = await sale.SellKgAsync(null, 5m, unitPrice: 25_000m);
        var later = await sale.SellKgAsync(sale.CustomerId, 2m, unitPrice: 50_000m);
        await api.DateAsync(first.Id(), new DateOnly(2026, 10, 12));
        await api.DateAsync(walkIn.Id(), new DateOnly(2026, 10, 12));
        await api.DateAsync(later.Id(), new DateOnly(2026, 10, 20));
        var period = Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var owner = sale.Owner.Client;

        var day = await (await owner.GetPriceTrendLinesAsync($"{period}&productId={sale.ProductId}&periodKey=2026-10-12")).ReadAsync(HttpStatusCode.OK);
        var week = await (await owner.GetPriceTrendLinesAsync($"{period}&unit=week&productId={sale.ProductId}&periodKey=2026-10-14")).ReadAsync(HttpStatusCode.OK);
        var month = await (await owner.GetPriceTrendLinesAsync($"{period}&unit=month&productId={sale.ProductId}&periodKey=2026-10-01")).ReadAsync(HttpStatusCode.OK);

        var lines = day.TrendLines();
        // The same day: the later invoice (the greater code) first.
        Assert.Equal([walkIn.Text("code"), first.Text("code")], lines.Select(l => l.Text("invoiceCode")));
        var firstLine = lines.Single(l => l.Text("invoiceCode") == first.Text("code"));
        Assert.Equal((10m, 30_000m, 300_000m, first.Id()), (firstLine.Money("kg"), firstLine.Money("unitPrice"), firstLine.Money("amount"), firstLine.GetProperty("invoiceId").GetGuid()));
        Assert.Equal(sale.CustomerId, firstLine.GetProperty("customer").Id());
        Assert.Equal("2026-10-12", firstLine.Text("date"));
        Assert.Equal(JsonValueKind.Null, lines.Single(l => l.Text("invoiceCode") == walkIn.Text("code")).GetProperty("customer").ValueKind);
        Assert.Equal(2, week.TrendLines().Count);
        Assert.Equal(["2026-10-20", "2026-10-12", "2026-10-12"], month.TrendLines().Select(l => l.Text("date")));
        Assert.Null(day.Text("messageCode"));
        Assert.Equal("day", day.Text("unit"));
    }

    [Fact]
    public async Task A_period_without_a_line_gives_no_lines_and_msg_gen_10()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 10m);

        var none = await (await sale.Owner.Client.GetPriceTrendLinesAsync($"productId={sale.ProductId}&periodKey=2026-10-13&{Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))}"))
            .ReadAsync(HttpStatusCode.OK);

        Assert.Empty(none.TrendLines());
        Assert.Equal("MSG-GEN-10", none.Text("messageCode"));
    }
}
