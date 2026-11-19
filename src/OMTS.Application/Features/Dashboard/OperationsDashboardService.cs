using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Dashboard;

/// <summary>
/// FARM-06 – the Owner's operations dashboard, worked out each time the screen opens and never stored (BR-FARM-10). The definitions are those of the screens it links to: the debts of
/// <see cref="CustomerDebtService"/> (DEBT-03), the lots for sale of <see cref="HarvestLotForSaleService"/> (BR-HARVEST-04), the alert of <see cref="WaterAlertService"/> (BR-WATER-04) as
/// WATER-03 shows it. The period applies to the daily revenue only; the zone to the revenue, the open lots and the water alerts; receivables and pending tasks are cooperative-wide.
/// Read only. The API checks that the caller is the Cooperative Owner, so no zone grant is looked at here.
/// </summary>
public sealed class OperationsDashboardService(
    IOperationsDashboardQuery queries,
    CustomerDebtService debts,
    HarvestLotForSaleService lotsForSale,
    IWaterMeasurementRepository measurements,
    IFarmingZoneRepository zones,
    ZoneAccessGuard zoneAccess,
    CustomerDebtStandings standings,
    TimeProvider timeProvider)
{
    /// <summary>How many of the longest-waiting records of each kind come with its count.</summary>
    public const int PendingTaskSample = 5;

    /// <summary>"Due soon" debts reach the days limit of the cooperative within this many days.</summary>
    public const int DueSoonDays = 7;

    /// <param name="from">Daily revenue period, Vietnam dates: today when neither date is given, as the invoice list of SALE-09.</param>
    /// <param name="zoneId">One zone, or null for every zone; a zone that does not exist is MSG-GEN-01.</param>
    public async Task<Result<OperationsDashboard>> GetAsync(DateOnly? from, DateOnly? to, Guid? zoneId, CancellationToken cancellationToken)
    {
        // The period is checked first, and the clock is read before any figure, so computedAt is not later than the figures it names.
        var computedAt = timeProvider.GetUtcNow();
        // Owner, or an Accountant of every zone (D5, 19/11/2026): the figures are cooperative-wide, so a user limited to some zones is refused.
        if (!(await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesReports, PermissionAction.View, cancellationToken)).AllZones)
        {
            return AppError.Forbidden;
        }

        var period = SalesInvoicePeriod.Resolve(from, to, BusinessClock.ToBusinessDate(computedAt));
        if (!period.IsSuccess)
        {
            return period.Error!;
        }

        if (zoneId is { } chosen && (await zones.GetByIdsAsync([chosen], cancellationToken)).Count == 0)
        {
            return AppError.Invalid("Zone");
        }

        var (start, end) = period.Value;
        var revenue = await queries.RevenueAsync(start, end, zoneId, cancellationToken);
        var (receivables, owedBack) = await debts.TotalsAsync(cancellationToken);
        var lots = await lotsForSale.CountAsync(zoneId, cancellationToken);
        var alerts = await WaterAlertsAsync(zoneId, cancellationToken);
        // The pending decisions (claims, payrolls, purchase requests, employee requests) are the Owner's: an Accountant sees none.
        var tasks = (await zoneAccess.IsCooperativeOwnerAsync(cancellationToken)
                ? await queries.PendingTasksAsync(PendingTaskSample, cancellationToken)
                : Array.Empty<PendingTaskFigures>())
            .Select(t => new DashboardPendingTaskGroup(t.Kind, t.Count, t.Oldest.Select(r => new DashboardPendingTask(r.Id, r.Code, r.Label, r.Since)).ToList()))
            .ToList();
        var today = BusinessClock.ToBusinessDate(computedAt);
        var todayTile = DayTile(today, await queries.DayAsync(today, zoneId, cancellationToken));
        var yesterday = today.AddDays(-1);
        var yesterdayTile = DayTile(yesterday, await queries.DayAsync(yesterday, zoneId, cancellationToken));
        var policy = await standings.PolicyAsync(cancellationToken);
        var (debtors, overdue, dueSoon) = await standings.DueSummaryAsync(policy, DueSoonDays, cancellationToken);
        return new OperationsDashboard(
            computedAt,
            zoneId,
            new DashboardRevenueTile(start, end, revenue.Total, revenue.InvoiceCount),
            new DashboardReceivablesTile(receivables, owedBack),
            new DashboardOpenHarvestLotsTile(lots.Count),
            alerts,
            new DashboardPendingTasksTile(tasks.Sum(t => t.Count), tasks),
            todayTile,
            yesterdayTile,
            new DashboardDueDebtsTile(debtors, overdue, dueSoon, policy.Enabled, policy.Enabled ? policy.MaxDays : null));
    }

    private static DashboardDayTile DayTile(DateOnly date, DashboardDayFigures f) =>
        new(date, f.InvoiceCount, f.Revenue, f.ReturnCount, f.ReturnValue, f.Revenue - f.ReturnValue);

    /// <summary>The zones In Use whose latest Active measurement has a value outside its thresholds, as the WATER-03 overview shows them (the alert is worked out, not stored).</summary>
    private async Task<DashboardWaterAlertsTile> WaterAlertsAsync(Guid? zoneId, CancellationToken cancellationToken)
    {
        var inUse = await measurements.ListZonesAsync(PermittedZones.Everywhere, inUseOnly: true, track: false, cancellationToken);
        var latest = await measurements.LatestActiveByZoneAsync(cancellationToken);
        var alerting = inUse
            .Where(z => zoneId is null || z.Id == zoneId)
            .Where(z => latest.TryGetValue(z.Id, out var m) && WaterAlertService.Evaluate(m).ValuesOutside.Count > 0)
            .Select(z => new DashboardAlertZone(z.Id, z.Code, z.Name, latest[z.Id].MeasuredAt))
            .ToList();
        return new DashboardWaterAlertsTile(alerting.Count, alerting);
    }
}
