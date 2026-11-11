using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>SALE-09 steps 1-3 against the database. Every method is one command, whatever the number of invoices.</summary>
/// <param name="ZoneIds">Null: every zone.</param>
/// <param name="Code">Part of the invoice code, read as plain text; empty for none.</param>
/// <param name="CashCollectedBy">Only invoices with a cash payment this account collected that is Awaiting confirmation.</param>
public sealed record SalesInvoiceSearchFilter(
    DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? ZoneIds, Guid? CustomerId, Guid? HarvestLotId, Guid? RaftId, int? StatusId, string? Code, Guid? CashCollectedBy);

/// <summary>One match, as much as the paid filter and the totals need.</summary>
/// <param name="OwnPayments">The payments of the invoice that are not cancelled, for a walk-in invoice only (0 with a profile).</param>
public sealed record SalesInvoiceSearchKey(Guid Id, Guid? CustomerId, decimal Total, int StatusId, decimal OwnPayments);

public sealed record SalesInvoiceSearchRow(
    Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, Guid ZoneId, string ZoneCode, string ZoneName, Guid? CustomerId, string? CustomerName, string? CustomerPhone,
    decimal Total, int StatusId, SalesPaymentTerm PaymentTerm, string? CustomerCode = null);

/// <summary>The latest sending of an invoice as its Notification row keeps it.</summary>
public sealed record SalesInvoiceLatestSending(int? DeliveryStatusId, DateTimeOffset? DeliveryStatusAt);

public interface ISalesInvoiceSearchReader
{
    /// <summary>The matches, newest first (invoice time, then code).</summary>
    Task<IReadOnlyList<SalesInvoiceSearchKey>> MatchingAsync(SalesInvoiceSearchFilter filter, CancellationToken cancellationToken);

    /// <summary>The rows of these invoices, in no order.</summary>
    Task<IReadOnlyList<SalesInvoiceSearchRow>> RowsAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken);

    /// <summary>The latest sending of the invoice message of each invoice that has one (BR-SALE-09).</summary>
    Task<IReadOnlyDictionary<Guid, SalesInvoiceLatestSending>> LatestSendingsAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken);

    /// <summary>The cash of each invoice that <paramref name="collectorAccountId"/> collected and that is Awaiting confirmation (BR-DEBT-03).</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> CashAwaitingConfirmationAsync(IReadOnlyCollection<Guid> invoiceIds, Guid collectorAccountId, CancellationToken cancellationToken);
}
