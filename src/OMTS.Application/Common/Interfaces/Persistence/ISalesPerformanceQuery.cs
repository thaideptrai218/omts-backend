using OMTS.Application.Features.Reports;
using OMTS.Application.Features.Reports.SalesPerformance;
using OMTS.Application.Features.Sales.Debts;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>One invoice of the report (BR-REPORT-01) at its latest values: its parts, and the value of its sales return that stands (0 when it has none).</summary>
/// <param name="Subtotal">The sum of the line amounts.</param>
/// <param name="PriceCuts">The price cuts of SALE-07, apart from the discount.</param>
/// <param name="Total">Subtotal − discount + other charges − price cuts: the revenue of the invoice, as the daily revenue of FARM-06.</param>
/// <param name="ReturnValue">The value of its sales return that is not cancelled, whatever the date of the return.</param>
public sealed record SalesPerformanceInvoice(
    Guid Id, string Code, DateOnly Date, Guid ZoneId, string ZoneCode, string ZoneName, Guid? CustomerId, string? CustomerName,
    decimal Subtotal, decimal Discount, decimal OtherCharges, decimal PriceCuts, decimal Total, decimal ReturnValue, bool HasReturn);

/// <summary>
/// The figures of a slice of the selection, summed by the database. <paramref name="InvoiceCount"/> counts the invoices of the slice, <paramref name="InvoicedKg"/> the kg of their lines,
/// <paramref name="Revenue"/> their totals (or the amounts of the lines, for a product), <paramref name="PriceCuts"/> their price cuts, <paramref name="Returns"/> the value of the sales
/// returns that stand and <paramref name="SoldKg"/> the invoiced kg minus the kg those returns took back (BR-HARVEST-07).
/// </summary>
public sealed record SalesPerformanceSums(int InvoiceCount, decimal InvoicedKg, decimal Revenue, decimal PriceCuts, decimal Returns, decimal SoldKg)
{
    public static readonly SalesPerformanceSums None = new(0, 0m, 0m, 0m, 0m, 0m);
}

/// <summary>
/// One slice of the selection for a grouping: a customer (<paramref name="Id"/> null for the walk-in customers), a product or a zone (<paramref name="Id"/> and <paramref name="Label"/>), or a
/// day (<paramref name="Date"/>, the invoice date). The week and the month are cut from the days by the calculator.
/// </summary>
public sealed record SalesPerformanceSlice(Guid? Id, string? Label, DateOnly? Date, SalesPerformanceSums Sums);

/// <summary>
/// What the grouped by product report adds to its product rows: the discounts, the other charges and the price cuts of the invoices, each a row of its own. Only the count, the revenue
/// (negative for a discount or a price cut), the price cuts and the part of the sales returns are used; the count is of the invoices that have a part other than 0.
/// </summary>
public sealed record SalesPerformanceParts(SalesPerformanceSums Discounts, SalesPerformanceSums OtherCharges, SalesPerformanceSums PriceCuts)
{
    public static readonly SalesPerformanceParts None = new(SalesPerformanceSums.None, SalesPerformanceSums.None, SalesPerformanceSums.None);
}

/// <summary>The figures of a selection already added up per slice: the total over every invoice of the selection, the slices of the grouping and, by product only, the parts of the invoices.</summary>
public sealed record SalesPerformanceAggregates(SalesPerformanceSums Total, IReadOnlyList<SalesPerformanceSlice> Slices, SalesPerformanceParts Parts);

/// <summary>What names the invoices of one group of the report, so the database reads only those (REPORT-01.3).</summary>
public enum SalesPerformanceGroupKind
{
    /// <summary>The invoices of walk-in customers (no profile).</summary>
    WalkIn,
    Customer,
    Zone,
    /// <summary>The invoices dated from <see cref="SalesPerformanceGroupFilter.From"/> to <see cref="SalesPerformanceGroupFilter.To"/> (a day, a week or a month).</summary>
    DateRange,
    /// <summary>The invoices with a line of the product.</summary>
    Product,
    /// <summary>The invoices with a discount, other charges or a price cut other than 0 (the three product rows of their own).</summary>
    Discounts,
    OtherCharges,
    PriceCuts,
}

public sealed record SalesPerformanceGroupFilter(SalesPerformanceGroupKind Kind, Guid? Id = null, DateOnly? From = null, DateOnly? To = null);

/// <summary>The money of one customer profile for the customer grouping: the debt formula as at the end of the period, and the payments dated in the period (BR-DEBT-02, BR-DEBT-03).</summary>
/// <param name="DebtAtEnd">The four sums of the formula over the entries dated up to the end of the period, Awaiting confirmation payments included, as the statement of DEBT-04 counts them.</param>
/// <param name="ConfirmedReceived">Payments received that are Completed, with a payment date in the period.</param>
/// <param name="PaidBack">Money paid back that is not cancelled, with a payment date in the period.</param>
public sealed record SalesPerformanceCustomerMoney(CustomerDebtFigures DebtAtEnd, decimal ConfirmedReceived, decimal PaidBack);

/// <summary>The money of the period that no profile has: the payments of walk-in sales and transfers completed with no customer, and money paid back with no customer.</summary>
public sealed record SalesPerformanceUnassignedMoney(decimal Received, decimal PaidBack);

/// <summary>What REPORT-01 reads, in a fixed number of commands whatever the number of invoices. Read only.</summary>
public interface ISalesPerformanceQuery
{
    /// <summary>
    /// The figures of the invoices with an invoice date in the period, not cancelled, in the zones and of the customers and products chosen (an invoice with one of the products on a line),
    /// added up by the database per slice of <paramref name="grouping"/>. The rows read are the slices, not the invoices.
    /// </summary>
    Task<SalesPerformanceAggregates> AggregatesAsync(ReportFilter filter, SalesPerformanceGrouping grouping, CancellationToken cancellationToken);

    /// <summary>The invoices of the same selection that belong to one group of the report; only those are read.</summary>
    Task<IReadOnlyList<SalesPerformanceInvoice>> InvoicesOfGroupAsync(ReportFilter filter, SalesPerformanceGroupFilter group, CancellationToken cancellationToken);

    /// <summary>The money of each profile asked for; every id has an entry. Payments and debts have no zone, so no zone is asked here (BR-FARM-08).</summary>
    Task<IReadOnlyDictionary<Guid, SalesPerformanceCustomerMoney>> CustomerMoneyAsync(
        IReadOnlyCollection<Guid> customerIds, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Completed payments received and money paid back with no customer, dated in the period (one command).</summary>
    Task<SalesPerformanceUnassignedMoney> UnassignedMoneyAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
