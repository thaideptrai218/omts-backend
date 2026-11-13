using OMTS.Application.Features.Reports;
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

/// <summary>One invoice line: its kg and amount, and the kg a sales return that stands took back (BR-HARVEST-07).</summary>
public sealed record SalesPerformanceLine(Guid InvoiceId, Guid ProductId, string ProductName, decimal Kg, decimal Amount, decimal ReturnedKg);

/// <summary>The invoices in the period and the zones asked for, with their lines; cancelled invoices are not in it.</summary>
public sealed record SalesPerformanceFacts(IReadOnlyList<SalesPerformanceInvoice> Invoices, IReadOnlyList<SalesPerformanceLine> Lines);

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
    /// <summary>The invoices with an invoice date in the period, not cancelled, in the zones and of the customers and products chosen (an invoice with one of the products on a line).</summary>
    Task<SalesPerformanceFacts> FactsAsync(ReportFilter filter, CancellationToken cancellationToken);

    /// <summary>The money of each profile asked for; every id has an entry. Payments and debts have no zone, so no zone is asked here (BR-FARM-08).</summary>
    Task<IReadOnlyDictionary<Guid, SalesPerformanceCustomerMoney>> CustomerMoneyAsync(
        IReadOnlyCollection<Guid> customerIds, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Completed payments received and money paid back with no customer, dated in the period (one command).</summary>
    Task<SalesPerformanceUnassignedMoney> UnassignedMoneyAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
