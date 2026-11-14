using OMTS.Application.Features.Reports;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>One invoice line counted by REPORT-02 (BR-REPORT-02), at the latest values of its invoice: its kg and unit price (VND/kg, not rounded) and its amount (kg × unit price, to the dong).</summary>
public sealed record PriceTrendLine(
    Guid InvoiceId, string InvoiceCode, DateOnly Date, Guid? CustomerId, string? CustomerName, Guid ProductId, string ProductName, decimal Kg, decimal UnitPrice, decimal Amount);

/// <summary>What REPORT-02 reads, in one command whatever the number of invoices. Read only.</summary>
public interface IPriceTrendsQuery
{
    /// <summary>
    /// The lines of the invoices with an invoice date in the period, in the zones chosen (the zone of the invoice), that are not Cancelled and have no sales return that stands (a cancelled
    /// return does not count, BR-CLAIM-03), of the products chosen (all when none).
    /// </summary>
    Task<IReadOnlyList<PriceTrendLine>> LinesAsync(ReportFilter filter, CancellationToken cancellationToken);
}
