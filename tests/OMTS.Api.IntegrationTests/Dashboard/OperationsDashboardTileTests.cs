using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Application.Features.Dashboard;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using Water = OMTS.Api.IntegrationTests.WaterQuality.WaterTestKit;
using static OMTS.Api.IntegrationTests.Dashboard.OperationsDashboardTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;

namespace OMTS.Api.IntegrationTests.Dashboard;

/// <summary>FARM-06 / BR-FARM-10: what each tile counts, and which filter reaches which tile.</summary>
public class OperationsDashboardTileTests
{
    // Daily revenue: Cancelled out, a changed total at its latest value, the period and the zone narrow it.
    [Fact]
    public async Task Daily_revenue_counts_invoices_not_cancelled_at_their_latest_total_inside_the_period_and_zone()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var otherZone = await api.AddZoneAsync(AcsTestKit.NewCode());
        var account = sale.Owner.Account.Id;
        await api.AddInvoiceAsync(sale.ZoneId, account, 100_000m);
        await api.AddInvoiceAsync(sale.ZoneId, account, 50_000m, statusId: StatusIds.SalesInvoice.Cancelled);
        await api.AddInvoiceAsync(otherZone.Id, account, 20_000m);
        var corrected = await api.AddInvoiceAsync(sale.ZoneId, account, 80_000m);
        await api.UpdateAsync<SalesInvoice>(corrected.Id, i => i.Total = 65_000m);
        await api.AddInvoiceAsync(sale.ZoneId, account, 7_000m, Today.AddDays(-1));
        await api.AddInvoiceAsync(sale.ZoneId, account, 3_000m, Today.AddDays(-30));

        var byDefault = await sale.Owner.Client.DashboardAsync();
        Assert.Equal((185_000m, 3), (byDefault.RevenueTotal(), byDefault.RevenueCount()));
        var tile = byDefault.GetProperty("dailyRevenue");
        Assert.Equal((Today.ToString("yyyy-MM-dd"), Today.ToString("yyyy-MM-dd")), (tile.GetProperty("from").GetString(), tile.GetProperty("to").GetString()));

        var twoDays = await sale.Owner.Client.DashboardAsync(Query(("from", Today.AddDays(-1)), ("to", Today)));
        Assert.Equal((192_000m, 4), (twoDays.RevenueTotal(), twoDays.RevenueCount()));
        var longPeriod = await sale.Owner.Client.DashboardAsync(Query(("from", Today.AddDays(-30)), ("to", Today)));
        Assert.Equal(195_000m, longPeriod.RevenueTotal());
        var past = await sale.Owner.Client.DashboardAsync(Query(("from", Today.AddDays(-20)), ("to", Today.AddDays(-5))));
        Assert.Equal((0m, 0), (past.RevenueTotal(), past.RevenueCount()));

        var inZone = await sale.Owner.Client.DashboardAsync(Query(("zoneId", sale.ZoneId)));
        Assert.Equal((165_000m, 2), (inZone.RevenueTotal(), inZone.RevenueCount()));
        Assert.Equal(20_000m, (await sale.Owner.Client.DashboardAsync(Query(("zoneId", otherZone.Id)))).RevenueTotal());
    }

    // Receivables: the debts above zero, the money owed back apart; neither the period nor the zone reaches them.
    [Fact]
    public async Task Receivables_are_the_debts_above_zero_with_the_money_owed_back_apart_whatever_the_filters()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var otherZone = await api.AddZoneAsync(AcsTestKit.NewCode());
        var account = sale.Owner.Account.Id;
        var overpaid = (await sale.Owner.Client.CreateCustomerAsync()).GetProperty("id").GetGuid();
        await api.AddInvoiceAsync(sale.ZoneId, account, 70_000m, Today.AddDays(-12), customerId: sale.CustomerId);
        await api.AddInvoiceAsync(sale.ZoneId, account, 10_000m, Today.AddDays(-12), StatusIds.SalesInvoice.Cancelled, sale.CustomerId);
        await api.RecordPaymentAsync(overpaid, 20_000m);

        var all = await sale.Owner.Client.DashboardAsync();
        Assert.Equal((70_000m, 20_000m), (all.Receivables(), all.OwedBack()));
        var narrowed = await sale.Owner.Client.DashboardAsync(Query(("from", Today), ("to", Today), ("zoneId", otherZone.Id)));
        Assert.Equal((70_000m, 20_000m), (narrowed.Receivables(), narrowed.OwedBack()));
        Assert.Equal(0m, narrowed.RevenueTotal());

        // BR-FARM-10: DEBT-03 uses the same definition, so its two totals are the dashboard's.
        var debts = await sale.Owner.Client.GetFromJsonAsync<JsonElement>("/api/customer-debts");
        Assert.Equal((all.Receivables(), all.OwedBack()), (debts.GetProperty("receivablesTotal").GetDecimal(), debts.GetProperty("owedBackTotal").GetDecimal()));
    }

    // Open harvest lots: Open and Reopened count, Closed does not; the zone narrows them.
    [Fact]
    public async Task Open_harvest_lots_are_the_lots_for_sale_now_of_the_zone_or_of_every_zone()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var owner = a.Owner.Client;

        async Task<(int All, int InA, int InB)> CountsAsync() =>
            ((await owner.DashboardAsync()).OpenLots(),
             (await owner.DashboardAsync(Query(("zoneId", a.ZoneId)))).OpenLots(),
             (await owner.DashboardAsync(Query(("zoneId", b.ZoneId)))).OpenLots());

        Assert.Equal((4, 2, 2), await CountsAsync());

        await api.UpdateAsync<HarvestLot>(a.OtherLotId, l => l.StatusId = StatusIds.HarvestLot.Reopened);
        Assert.Equal((4, 2, 2), await CountsAsync());
        await api.UpdateAsync<HarvestLot>(a.LotId, l => l.StatusId = StatusIds.HarvestLot.Closed);

        Assert.Equal((3, 1, 2), await CountsAsync());
    }

    // Water alerts: zones whose latest measurement is outside its thresholds, once each; the zone narrows them, the period does not.
    [Fact]
    public async Task Water_alerts_count_each_zone_with_an_alert_now_once_and_follow_the_latest_measurement()
    {
        using var api = new AuthApiFactory();
        var alerting = await Water.ZoneAsync(api);
        var calm = await Water.ZoneAsync(api);
        var now = DateTimeOffset.UtcNow;
        await Water.RecordedAsync(alerting, 9.5m, 20m, now.AddMinutes(-60));
        await Water.RecordedAsync(alerting, 9.8m, 20m, now.AddMinutes(-30));
        await Water.RecordedAsync(calm, 7.2m, 20m, now.AddMinutes(-30));
        var owner = alerting.Owner;

        var everywhere = await owner.DashboardAsync();
        Assert.Equal(1, everywhere.WaterAlerts());
        Assert.Equal([alerting.Zone.Id], everywhere.AlertZoneIds());
        Assert.Equal(1, (await owner.DashboardAsync(Query(("zoneId", alerting.Zone.Id)))).WaterAlerts());
        Assert.Equal(0, (await owner.DashboardAsync(Query(("zoneId", calm.Zone.Id)))).WaterAlerts());
        Assert.Equal(1, (await owner.DashboardAsync(Query(("from", Today.AddDays(-9)), ("to", Today.AddDays(-8))))).WaterAlerts());

        // A later reading inside the thresholds ends the alert: it is worked out now, not stored.
        var calmLatest = await Water.RecordedAsync(alerting, 7.4m, 20m, now.AddMinutes(-5));
        Assert.Equal(0, (await owner.DashboardAsync()).WaterAlerts());

        // "Latest" is the measurement time, not the time it was saved: a reading saved now but dated earlier changes nothing.
        await Water.RecordedAsync(alerting, 9.9m, 20m, now.AddMinutes(-10));
        Assert.Equal(0, (await owner.DashboardAsync()).WaterAlerts());

        // Voiding the latest reading lets the one before it decide again.
        Assert.Equal(HttpStatusCode.OK, (await Water.VoidAsync(owner, calmLatest.GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(1, (await owner.DashboardAsync()).WaterAlerts());
    }

    // A zone that is no longer In Use has no alert on the dashboard, as on the WATER-03 overview.
    [Fact]
    public async Task A_zone_that_is_no_longer_in_use_has_no_water_alert()
    {
        using var api = new AuthApiFactory();
        var alerting = await Water.ZoneAsync(api);
        await Water.RecordedAsync(alerting, 9.8m, 20m, DateTimeOffset.UtcNow.AddMinutes(-30));
        Assert.Equal(1, (await alerting.Owner.DashboardAsync()).WaterAlerts());

        await api.UpdateAsync<FarmingZone>(alerting.Zone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);

        Assert.Equal(0, (await alerting.Owner.DashboardAsync()).WaterAlerts());
    }

    // Pending tasks: one count per kind; always the whole cooperative.
    [Fact]
    public async Task Pending_tasks_count_each_kind_awaiting_and_ignore_the_period_and_the_zone()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var otherZone = await api.AddZoneAsync(AcsTestKit.NewCode());
        var account = sale.Owner.Account.Id;
        var waitingRequest = await api.AddPurchaseRequestAsync(sale);
        await api.AddPurchaseRequestAsync(sale, StatusIds.PurchaseRequest.Accepted);
        await api.AddPurchaseRequestAsync(sale, StatusIds.PurchaseRequest.Declined);
        await api.AddPurchaseRequestAsync(sale, StatusIds.PurchaseRequest.Cancelled);
        var waitingClaim = await api.AddQualityClaimAsync(await api.AddInvoiceAsync(sale.ZoneId, account, 1_000m, customerId: sale.CustomerId));
        await api.AddQualityClaimAsync(await api.AddInvoiceAsync(sale.ZoneId, account, 1_000m, customerId: sale.CustomerId), StatusIds.QualityClaim.Rejected);
        await api.AddQualityClaimAsync(await api.AddInvoiceAsync(sale.ZoneId, account, 1_000m, customerId: sale.CustomerId), StatusIds.QualityClaim.Accepted);
        var waitingPayroll = await api.AddPayrollAsync(StatusIds.Payroll.AwaitingFinalization, 1);
        await api.AddPayrollAsync(StatusIds.Payroll.Draft, 2);
        await api.AddPayrollAsync(StatusIds.Payroll.SentBack, 3);
        await api.AddPayrollAsync(StatusIds.Payroll.Finalized, 4);

        foreach (var query in new[] { "", Query(("zoneId", otherZone.Id), ("from", Today.AddDays(-9)), ("to", Today.AddDays(-8))) })
        {
            var dashboard = await sale.Owner.Client.DashboardAsync(query);
            Assert.Equal((3, 1, 1, 1), (dashboard.PendingTotal(), dashboard.PendingCount(PendingTaskKinds.PurchaseRequest), dashboard.PendingCount(PendingTaskKinds.QualityClaim), dashboard.PendingCount(PendingTaskKinds.Payroll)));
            Assert.Equal([waitingRequest.Id], dashboard.PendingIds(PendingTaskKinds.PurchaseRequest));
            Assert.Equal([waitingClaim.Id], dashboard.PendingIds(PendingTaskKinds.QualityClaim));
            Assert.Equal([waitingPayroll.Id], dashboard.PendingIds(PendingTaskKinds.Payroll));
            Assert.Equal(waitingClaim.Code, dashboard.PendingGroup(PendingTaskKinds.QualityClaim).GetProperty("oldest")[0].GetProperty("code").GetString());

            // Each record has a label to show it without opening it.
            string Label(string kind) => dashboard.PendingGroup(kind).GetProperty("oldest")[0].GetProperty("label").GetString()!;
            Assert.Matches(@" – \d+ cages .+, pickup \d{2}/\d{2}/\d{4}$", Label(PendingTaskKinds.PurchaseRequest));
            Assert.Matches(@" – invoice \S+$", Label(PendingTaskKinds.QualityClaim));
            Assert.Matches(@"^Payroll \d{2}/\d{2}/\d{4}–\d{2}/\d{2}/\d{4}$", Label(PendingTaskKinds.Payroll));
        }
    }

    // LABOR-05: the requests an employee sent that the Owner has not reviewed; an approved, rejected or cancelled one is not a task. No zone, so the zone filter does not reach them.
    [Fact]
    public async Task Employee_requests_awaiting_review_are_a_kind_with_the_employee_the_kind_and_the_days_in_its_label()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var otherZone = await api.AddZoneAsync(AcsTestKit.NewCode());
        var leave = await api.AddEmployeeRequestAsync(StatusIds.EmployeeRequest.AwaitingReview, days: 3, sentAgo: TimeSpan.FromHours(3), employeeName: "Nguyen Van A");
        var missed = await api.AddEmployeeRequestAsync(StatusIds.EmployeeRequest.AwaitingReview, EmployeeRequestType.MissedCheckOut, sentAgo: TimeSpan.FromHours(1), employeeName: "Tran Thi B");
        await api.AddEmployeeRequestAsync(StatusIds.EmployeeRequest.Approved);
        await api.AddEmployeeRequestAsync(StatusIds.EmployeeRequest.Rejected);
        await api.AddEmployeeRequestAsync(StatusIds.EmployeeRequest.Cancelled);

        foreach (var query in new[] { "", Query(("zoneId", otherZone.Id)) })
        {
            var dashboard = await sale.Owner.Client.DashboardAsync(query);
            Assert.Equal((2, 2), (dashboard.PendingTotal(), dashboard.PendingCount(PendingTaskKinds.EmployeeRequest)));
            Assert.Equal([leave.Id, missed.Id], dashboard.PendingIds(PendingTaskKinds.EmployeeRequest));
            var labels = dashboard.PendingGroup(PendingTaskKinds.EmployeeRequest).GetProperty("oldest").EnumerateArray().Select(t => t.GetProperty("label").GetString()).ToList();
            Assert.Equal(
                [$"Nguyen Van A – leave {Today:dd/MM/yyyy}–{Today.AddDays(2):dd/MM/yyyy}", $"Tran Thi B – missed check-out {Today:dd/MM/yyyy}"],
                labels);
        }
    }

    // The list of a kind is short, oldest first; its count is of every record.
    [Fact]
    public async Task A_kind_with_many_records_lists_the_longest_waiting_few_and_counts_them_all()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var requests = new List<PurchaseRequest>();
        for (var hours = 1; hours <= 7; hours++)
        {
            requests.Add(await api.AddPurchaseRequestAsync(sale, sentAgo: TimeSpan.FromHours(hours)));
        }

        var dashboard = await sale.Owner.Client.DashboardAsync();

        Assert.Equal(7, dashboard.PendingCount(PendingTaskKinds.PurchaseRequest));
        Assert.Equal(OperationsDashboardService.PendingTaskSample, dashboard.PendingIds(PendingTaskKinds.PurchaseRequest).Count);
        Assert.Equal(requests.AsEnumerable().Reverse().Take(OperationsDashboardService.PendingTaskSample).Select(r => r.Id), dashboard.PendingIds(PendingTaskKinds.PurchaseRequest));
        Assert.Equal((0, 0), (dashboard.PendingCount(PendingTaskKinds.QualityClaim), dashboard.PendingCount(PendingTaskKinds.Payroll)));
    }
}
