namespace OMTS.Application.Features.Traceability.Tracing;

/// <summary>
/// One sales invoice line of a harvest lot, on an invoice that is not cancelled, at its latest values (BR-TRACE-03): the invoice, its zone and its trader. A walk-in sale has no
/// customer profile, so no customer.
/// </summary>
/// <param name="CustomerAccountId">The trader account the customer profile is linked to, if any: Withdraw traceability record (TRACE-04) also pushes the notice to it.</param>
/// <param name="TakenBackByReturn">The invoice has a sales return that stands (a return takes back the whole invoice): the trader no longer has these oysters (BR-TRACE-07).</param>
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
    decimal Amount,
    Guid? CustomerAccountId = null,
    bool TakenBackByReturn = false);

/// <summary>The part of a sales return that stands of an invoice on one lot, per outcome (BR-TRACE-03, AC-11): the forward trace shows it under its invoice.</summary>
public sealed record LotSaleReturn(Guid InvoiceId, Guid Id, string Code, DateOnly ReturnDate, decimal Kg, string Outcome);

/// <summary>One correction saved in Correct traceability data (TRACE-03) of a record of the trace: the audit row <c>*.TraceCorrected</c> (GBR-02).</summary>
public sealed record TraceCorrectionRow(Guid RecordId, DateTimeOffset CorrectedAt, Guid? ActorAccountId, string? Reason);
