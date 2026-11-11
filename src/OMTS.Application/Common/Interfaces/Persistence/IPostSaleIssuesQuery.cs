using OMTS.Application.Features.Reports;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The kinds of post-sale event of REPORT-03 (BR-REPORT-03).</summary>
public enum PostSaleEventType
{
    Claim,
    Return,
    PriceCut,
    Cancellation,
}

/// <summary>
/// One event of the period, dated by its own date (not by its invoice), with its invoice (zone and customer). <see cref="Id"/> is the claim, the sales return, the price cut or the invoice.
/// <see cref="Amount"/> is the value of a return, the amount of a price cut or the total of a cancelled invoice; a claim has none. <see cref="ClaimStatusId"/> is for claims only.
/// </summary>
public sealed record PostSaleEvent(
    PostSaleEventType Type, Guid Id, DateOnly Date, Guid InvoiceId, string InvoiceCode, Guid ZoneId, string ZoneName, Guid? CustomerId, string? CustomerName, string? Note, decimal? Amount,
    int? ClaimStatusId);

/// <summary>A harvest lot that an invoice of an event sells on one of its lines.</summary>
public sealed record PostSaleInvoiceLot(Guid InvoiceId, Guid LotId, string LotCode);

/// <summary>A return line of a sales return of an event: the kg that came back from one lot and the kg of them thrown away (BR-HARVEST-07).</summary>
public sealed record PostSaleReturnLot(Guid ReturnId, Guid LotId, string LotCode, decimal ReturnedKg, decimal DiscardedKg);

public sealed record PostSaleFacts(IReadOnlyList<PostSaleEvent> Events, IReadOnlyList<PostSaleInvoiceLot> InvoiceLots, IReadOnlyList<PostSaleReturnLot> ReturnLots);

/// <summary>What REPORT-03 reads, in a fixed number of commands whatever the number of events. Read only.</summary>
public interface IPostSaleIssuesQuery
{
    /// <summary>
    /// The claims sent, the sales returns that stand (not cancelled), the price cuts and the cancellations dated in the period, of the invoices in the zones and of the customers chosen
    /// (the zone and the customer of the invoice), with the lots they concern. Corrections of data-entry errors are not events.
    /// </summary>
    Task<PostSaleFacts> FactsAsync(ReportFilter filter, CancellationToken cancellationToken);
}
