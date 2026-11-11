namespace OMTS.Application.Features.Reports.SalesPerformance;

/// <summary>The groupings of REPORT-01 step 1.</summary>
public enum SalesPerformanceGrouping
{
    Customer,
    Product,
    Zone,
    Day,
    Week,
    Month,
}

/// <summary>
/// The query of the three REPORT-01 calls (the report, the invoices of a group, the export). Dates are Vietnam dates: the current month when none is given. <c>GroupBy</c> is customer,
/// product, zone, day, week or month (customer when none). <c>GroupKey</c> is the <c>key</c> of a group of the report, for the invoices call only.
/// </summary>
public sealed record SalesPerformanceQuery(
    DateOnly? From = null, DateOnly? To = null, string? GroupBy = null, Guid[]? ZoneIds = null, Guid[]? CustomerIds = null, Guid[]? ProductIds = null, string? GroupKey = null);

/// <summary>The choices that were checked and applied, as the screen and the file show them.</summary>
public sealed record SalesPerformanceChoices(IReadOnlyList<ReportChoice> Zones, IReadOnlyList<ReportChoice> Customers, IReadOnlyList<ReportChoice> Products);

/// <summary>
/// The figures of a group or of the total (BR-REPORT-01). <see cref="NetRevenue"/> is the revenue minus the sales returns; <see cref="SoldKg"/> is the invoiced kg minus the returned kg
/// (BR-HARVEST-07). <see cref="NetMoneyReceived"/> and <see cref="DebtAtEnd"/> are filled for the grouping by customer only: they have no zone and follow the customer, not the invoices.
/// </summary>
public sealed record SalesPerformanceFigures(
    int InvoiceCount, decimal InvoicedKg, decimal Revenue, decimal PriceCuts, decimal SalesReturns, decimal NetRevenue, decimal SoldKg, decimal? NetMoneyReceived, decimal? DebtAtEnd);

/// <param name="Key">What the invoices call sends back as <c>groupKey</c>: an id, a start date (yyyy-MM-dd) or a fixed name (walk-in, discounts, other-charges, price-cuts).</param>
public sealed record SalesPerformanceGroup(
    string Key, string Label, int InvoiceCount, decimal InvoicedKg, decimal Revenue, decimal PriceCuts, decimal SalesReturns, decimal NetRevenue, decimal SoldKg,
    decimal? NetMoneyReceived, decimal? DebtAtEnd);

/// <summary>REPORT-01 step 3: worked out when it is shown. <see cref="Message"/> is MSG-GEN-10 when no invoice matches (REPORT-01.2); the groups are then empty and the totals 0.</summary>
public sealed record SalesPerformanceReport(
    DateTimeOffset ComputedAt, DateOnly From, DateOnly To, string GroupBy, SalesPerformanceChoices Filters, IReadOnlyList<SalesPerformanceGroup> Groups,
    SalesPerformanceFigures Totals, string? Message, string? MessageCode);

public sealed record SalesPerformanceInvoiceCustomer(Guid Id, string Name);

public sealed record SalesPerformanceInvoiceZone(Guid Id, string Code, string Name);

/// <summary>REPORT-01.3 step 2: <see cref="Net"/> is the total minus the value of the sales return that stands (0 when it has none).</summary>
public sealed record SalesPerformanceInvoiceRow(
    Guid Id, string Code, DateOnly Date, SalesPerformanceInvoiceCustomer? Customer, SalesPerformanceInvoiceZone Zone, decimal Total, decimal ReturnValue, decimal Net);

/// <summary>The invoices of one group, newest first; <see cref="Message"/> is MSG-GEN-10 when the group has none.</summary>
public sealed record SalesPerformanceInvoiceList(
    DateOnly From, DateOnly To, string GroupBy, string GroupKey, IReadOnlyList<SalesPerformanceInvoiceRow> Invoices, string? Message, string? MessageCode);
