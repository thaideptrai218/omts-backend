namespace OMTS.Application.Features.Traceability.Tracing;

/// <summary>
/// One sales invoice line of a harvest lot, on an invoice that is not cancelled, at its latest values (BR-TRACE-03): the invoice, its zone and its trader. A walk-in sale has no
/// customer profile, so no customer.
/// </summary>
public sealed record LotSaleLine(
    Guid InvoiceId,
    string InvoiceCode,
    DateTimeOffset InvoicedAt,
    DateOnly InvoiceDate,
    Guid InvoiceZoneId,
    string InvoiceZoneCode,
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerPhone,
    int LineNumber,
    string ProductName,
    decimal Kg,
    decimal UnitPrice,
    decimal Amount);

/// <summary>One correction saved in Correct traceability data (TRACE-03) of a record of the trace: the audit row <c>*.TraceCorrected</c> (GBR-02).</summary>
public sealed record TraceCorrectionRow(Guid RecordId, DateTimeOffset CorrectedAt, Guid? ActorAccountId, string? Reason);
