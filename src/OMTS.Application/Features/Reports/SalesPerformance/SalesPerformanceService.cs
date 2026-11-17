using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Debts;

namespace OMTS.Application.Features.Reports.SalesPerformance;

/// <summary>
/// REPORT-01 – the sales figures of a period by customer, product, zone, day, week or month, worked out each time they are shown and never stored (BR-REPORT-01, as BR-FARM-10). The
/// API checks the role (SALES_REPORTS View, or Export for the file: the Cooperative Owner and the Accountant, §I.4.4); this checks the zones of the user's pairs (BR-FARM-08), the period
/// and the choices (MSG-GEN-01), and finds no invoice (MSG-GEN-10, REPORT-01.2). The money of a customer comes from <see cref="CustomerDebtMath"/>, the formula every debt screen uses.
/// </summary>
public sealed class SalesPerformanceService(ReportFilterResolver filters, ISalesPerformanceQuery query, TimeProvider timeProvider)
{
    public const string GroupByField = "Grouping";
    public const string GroupKeyField = "Group key";
    public const string UnassignedKey = "unassigned";
    public const string UnassignedLabel = "Not assigned to a customer";

    public Task<Result<SalesPerformanceReport>> ReportAsync(SalesPerformanceQuery request, CancellationToken cancellationToken) =>
        BuildAsync(request, PermissionAction.View, cancellationToken);

    /// <summary>REPORT-01.3: the invoices of one group of the same selection; MSG-GEN-10 when the group has none.</summary>
    public async Task<Result<SalesPerformanceInvoiceList>> InvoicesAsync(SalesPerformanceQuery request, CancellationToken cancellationToken)
    {
        var grouping = ParseGrouping(request.GroupBy);
        var invalid = new List<string>();
        AddIf(invalid, grouping is null, GroupByField);
        AddIf(invalid, string.IsNullOrWhiteSpace(request.GroupKey), GroupKeyField);
        var filter = await ResolveAsync(request, PermissionAction.View, invalid, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // Both are known to be given: a missing grouping or key is an invalid field above.
        var (f, groupBy, key) = (filter.Value!, grouping!.Value, request.GroupKey!.Trim());
        var invoices = SalesPerformanceCalculator.InvoicesOf(await query.FactsAsync(f, cancellationToken), groupBy, key);
        var empty = invoices.Count == 0 ? UserMessages.NoDataForFilters("group") : null;
        return new SalesPerformanceInvoiceList(f.From, f.To, Name(groupBy), key, invoices, empty?.Text, empty?.Code);
    }

    /// <summary>Shared with the export (<see cref="SalesPerformanceExportService"/>), which asks for the Export right.</summary>
    internal async Task<Result<SalesPerformanceReport>> BuildAsync(SalesPerformanceQuery request, PermissionAction action, CancellationToken cancellationToken)
    {
        // The clock is read before any figure, so computedAt is not later than the figures it names.
        var computedAt = timeProvider.GetUtcNow();
        var grouping = ParseGrouping(request.GroupBy);
        var filter = await ResolveAsync(request, action, grouping is null ? new[] { GroupByField } : Array.Empty<string>(), cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (f, groupBy) = (filter.Value!, grouping!.Value);
        var calculation = SalesPerformanceCalculator.Build(await query.FactsAsync(f, cancellationToken), groupBy);
        var (groups, totals) = groupBy == SalesPerformanceGrouping.Customer
            ? await WithCustomerMoneyAsync(calculation, f, cancellationToken)
            : (calculation.Groups, calculation.Totals);
        var empty = groups.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new SalesPerformanceReport(
            computedAt, f.From, f.To, Name(groupBy), new SalesPerformanceChoices(f.Zones, f.Customers, f.Products), groups, totals, empty?.Text, empty?.Code);
    }

    /// <summary>
    /// BR-REPORT-01, grouped by customer: the net money received in the period (Completed payments minus money paid back, by payment date, BR-DEBT-03) and the debts at the end of the
    /// period (Awaiting confirmation payments count, BR-DEBT-02), split into the receivable and the money owed back, with their net (PM answer 12/11). None follows the zone filter,
    /// nor the invoices of the period: they are the customer's. Walk-in invoices have no profile and no money here. Money received and paid back with no customer yet is one more row,
    /// so the total matches the money received (PM answer 12/11); it is left out when customers were chosen.
    /// </summary>
    private async Task<(IReadOnlyList<SalesPerformanceGroup> Groups, SalesPerformanceFigures Totals)> WithCustomerMoneyAsync(
        SalesPerformanceCalculation calculation, ReportFilter filter, CancellationToken cancellationToken)
    {
        var ids = calculation.Groups.Select(g => Guid.TryParse(g.Key, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        var money = await query.CustomerMoneyAsync(ids, filter.From, filter.To, cancellationToken);
        var groups = calculation.Groups.Select(g =>
        {
            // The walk-in row has no profile, so no money: the figures stay empty.
            if (!Guid.TryParse(g.Key, out var id) || !money.TryGetValue(id, out var m))
            {
                return g;
            }

            var debt = CustomerDebtMath.Debt(m.DebtAtEnd);
            return g with
            {
                NetMoneyReceived = m.ConfirmedReceived - m.PaidBack,
                Receivable = CustomerDebtMath.Receivable(debt),
                OwedBack = CustomerDebtMath.OwedBack(debt),
                NetDebt = debt,
            };
        }).ToList();
        if (groups.Count > 0 && filter.Customers.Count == 0)
        {
            var unassigned = await query.UnassignedMoneyAsync(filter.From, filter.To, cancellationToken);
            if (unassigned.Received != 0m || unassigned.PaidBack != 0m)
            {
                groups.Add(new SalesPerformanceGroup(UnassignedKey, UnassignedLabel, 0, 0m, 0m, 0m, 0m, 0m, 0m, unassigned.Received - unassigned.PaidBack, 0m, 0m, 0m));
            }
        }

        // The totals add the rows; the net debt is the receivable minus the money owed back.
        var receivable = groups.Sum(g => g.Receivable);
        var owedBack = groups.Sum(g => g.OwedBack);
        return (groups, calculation.Totals with { NetMoneyReceived = groups.Sum(g => g.NetMoneyReceived), Receivable = receivable, OwedBack = owedBack, NetDebt = receivable - owedBack });
    }

    private Task<Result<ReportFilter>> ResolveAsync(
        SalesPerformanceQuery request, PermissionAction action, IReadOnlyList<string> invalid, CancellationToken cancellationToken) =>
        filters.ResolveAsync(
            PermissionFunctions.SalesReports, action, request.From, request.To, request.ZoneIds, request.CustomerIds, request.ProductIds, invalid, cancellationToken);

    /// <summary>By customer when none is given; names only, in any letter case (never a number).</summary>
    internal static SalesPerformanceGrouping? ParseGrouping(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "customer" => SalesPerformanceGrouping.Customer,
        "product" => SalesPerformanceGrouping.Product,
        "zone" => SalesPerformanceGrouping.Zone,
        "day" => SalesPerformanceGrouping.Day,
        "week" => SalesPerformanceGrouping.Week,
        "month" => SalesPerformanceGrouping.Month,
        _ => null,
    };

    internal static string Name(SalesPerformanceGrouping grouping) => grouping.ToString().ToLowerInvariant();

    private static void AddIf(List<string> invalid, bool wrong, string field)
    {
        if (wrong)
        {
            invalid.Add(field);
        }
    }
}
