using OMTS.Application.Features.Reports;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>One invoice line counted by REPORT-02 (BR-REPORT-02), at the latest values of its invoice: its kg and unit price (VND/kg, not rounded) and its amount (kg × unit price, to the dong).</summary>
public sealed record PriceTrendLine(
    Guid InvoiceId, string InvoiceCode, DateOnly Date, Guid? CustomerId, string? CustomerName, Guid ProductId, string ProductName, decimal Kg, decimal UnitPrice, decimal Amount);

/// <summary>
/// The lines counted of one product on one invoice date, added up by the database: their kg, their amounts and the lowest and highest unit price. A week or a month is cut from the days
/// by the calculator (kg and amounts add up, the lowest and highest of the days give those of the period).
/// </summary>
public sealed record PriceTrendDay(Guid ProductId, string ProductName, DateOnly Date, decimal Kg, decimal Amount, decimal LowestPrice, decimal HighestPrice);

/// <summary>What REPORT-02 reads, in a fixed number of commands whatever the number of invoices. Read only.</summary>
public interface IPriceTrendsQuery
{
    /// <summary>
    /// The lines of the invoices with an invoice date in the period, in the zones chosen (the zone of the invoice), that are not Cancelled and have no sales return that stands (a cancelled
    /// return does not count, BR-CLAIM-03), of the products chosen (all when none), added up per product and invoice date.
    /// </summary>
    Task<IReadOnlyList<PriceTrendDay>> DaysAsync(ReportFilter filter, CancellationToken cancellationToken);

    /// <summary>The lines of the same selection that are of one product and dated from <paramref name="from"/> to <paramref name="to"/> (a day, a week or a month of the report); only those are read.</summary>
    Task<IReadOnlyList<PriceTrendLine>> LinesAsync(ReportFilter filter, Guid productId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
