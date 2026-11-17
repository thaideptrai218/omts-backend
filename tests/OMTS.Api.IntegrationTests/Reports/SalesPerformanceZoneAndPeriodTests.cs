using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-01 grouped by zone and by period, the zone filter and the zones of the user (BR-FARM-08), the ISO weeks of UTC+7.</summary>
public class SalesPerformanceZoneAndPeriodTests
{
    // The zone of the invoice holds its whole revenue (BR-SALE-08).
    [Fact]
    public async Task Grouped_by_zone_each_zone_has_its_invoices_and_the_zone_filter_keeps_one()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        await a.SellKgAsync(a.CustomerId, 10m);
        await b.SellKgAsync(b.CustomerId, 5m);

        var all = await a.Owner.Client.ReportAsync("groupBy=zone");
        var onlyA = await a.Owner.Client.ReportAsync($"groupBy=zone&zoneIds={a.ZoneId}");
        var both = await a.Owner.Client.ReportAsync($"groupBy=zone&zoneIds={a.ZoneId}&zoneIds={b.ZoneId}");

        Assert.Equal((300_000m, 150_000m), (all.Group(a.ZoneId.ToString()).Money("revenue"), all.Group(b.ZoneId.ToString()).Money("revenue")));
        Assert.Equal(450_000m, all.Totals().Money("revenue"));
        Assert.Equal(300_000m, Assert.Single(onlyA.Groups()).Money("revenue"));
        Assert.Equal(300_000m, onlyA.Totals().Money("revenue"));
        Assert.Equal(450_000m, both.Totals().Money("revenue"));
        // Grouped by zone, the label is the name of the zone.
        Assert.Equal((await api.ZoneEntityAsync(a.ZoneId)).Name, all.Group(a.ZoneId.ToString()).Text("label"));
    }

    [Fact]
    public async Task A_user_of_one_zone_sees_that_zone_only_and_a_zone_outside_theirs_is_refused()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        await a.SellKgAsync(a.CustomerId, 10m);
        await b.SellKgAsync(b.CustomerId, 5m);
        var accountantA = (await api.StaffAsync(await api.ZoneEntityAsync(a.ZoneId), StaffRole.Accountant)).Client;
        var accountantAll = (await api.StaffAsync(null, StaffRole.Accountant)).Client;

        // Only the invoices of the zones they can access count (BR-REPORT-01, BR-FARM-08), the total included.
        var own = await accountantA.ReportAsync("groupBy=zone");
        Assert.Equal(a.ZoneId.ToString(), Assert.Single(own.Groups()).Text("key"));
        Assert.Equal(300_000m, own.Totals().Money("revenue"));
        Assert.Equal(450_000m, (await accountantAll.ReportAsync()).Totals().Money("revenue"));

        // A zone they cannot access is refused (as SALE-09 and HARVEST-04), also with one they can.
        foreach (var query in new[] { $"zoneIds={b.ZoneId}", $"zoneIds={a.ZoneId}&zoneIds={b.ZoneId}" })
        {
            var refused = await accountantA.GetReportAsync(query);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await accountantA.GetExportAsync($"zoneIds={b.ZoneId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountantA.GetExportAsync($"zoneIds={a.ZoneId}")).StatusCode);
    }

    // 2026-10-31 is a Saturday, 2026-11-01 a Sunday and 2026-11-02 the Monday that starts ISO week 45. The Sunday sale is at 23:59 in Vietnam, the Monday sale at 00:01 on Monday.
    [Fact]
    public async Task Weeks_start_on_monday_at_midnight_in_vietnam_and_days_and_months_follow_the_business_date()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var saturday = await sale.SellKgAsync(sale.CustomerId, 1m);
        var sunday = await sale.SellKgAsync(sale.CustomerId, 2m);
        var monday = await sale.SellKgAsync(sale.CustomerId, 3m);
        await DateAtAsync(api, saturday.Id(), new DateOnly(2026, 10, 31), new DateTimeOffset(2026, 10, 31, 5, 0, 0, TimeSpan.Zero));
        await DateAtAsync(api, sunday.Id(), new DateOnly(2026, 11, 1), new DateTimeOffset(2026, 11, 1, 16, 59, 0, TimeSpan.Zero));
        await DateAtAsync(api, monday.Id(), new DateOnly(2026, 11, 2), new DateTimeOffset(2026, 11, 1, 17, 1, 0, TimeSpan.Zero));
        var period = Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30));

        var weeks = await sale.Owner.Client.ReportAsync($"{period}&groupBy=week");
        var days = await sale.Owner.Client.ReportAsync($"{period}&groupBy=day");
        var months = await sale.Owner.Client.ReportAsync($"{period}&groupBy=month");

        Assert.Equal(["2026-10-26", "2026-11-02"], weeks.Groups().Select(g => g.Text("key")));
        Assert.Equal(["2026-W44", "2026-W45"], weeks.Groups().Select(g => g.Text("label")));
        Assert.Equal([90_000m, 90_000m], weeks.Groups().Select(g => g.Money("revenue")));
        Assert.Equal(["2026-10-31", "2026-11-01", "2026-11-02"], days.Groups().Select(g => g.Text("key")));
        Assert.Equal([30_000m, 60_000m, 90_000m], days.Groups().Select(g => g.Money("revenue")));
        Assert.Equal(["2026-10-01", "2026-11-01"], months.Groups().Select(g => g.Text("key")));
        Assert.Equal(["2026-10", "2026-11"], months.Groups().Select(g => g.Text("label")));
        Assert.Equal([30_000m, 150_000m], months.Groups().Select(g => g.Money("revenue")));
        // Period groupings have no customer money; every grouping adds up to the same total.
        Assert.Equal(180_000m, weeks.Totals().Money("revenue"));
        Assert.Equal(180_000m, days.SumOf("revenue"));
        Assert.Equal(JsonValueKind.Null, weeks.Groups()[0].GetProperty("netDebt").ValueKind);
    }

    private static Task DateAtAsync(AuthApiFactory api, Guid invoiceId, DateOnly date, DateTimeOffset at) =>
        api.UpdateAsync<SalesInvoice>(invoiceId, i => (i.InvoiceDate, i.InvoicedAt) = (date, at));
}
