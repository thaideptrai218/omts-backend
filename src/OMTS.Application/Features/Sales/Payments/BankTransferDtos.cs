namespace OMTS.Application.Features.Sales.Payments;

/// <summary>What the system did with one reported transfer (GFR-08).</summary>
public enum BankTransferOutcome
{
    /// <summary>A payment of the saved invoice with that code (later payment, after a change of payment method, a cancelled invoice too), Completed.</summary>
    PaidInvoice,

    /// <summary>The sale awaiting this transfer was saved Completed with the transfer as its payment (SALE-05 step 8).</summary>
    CompletedSale,

    /// <summary>The transfer carried the code of a payment waiting for it (a VietQR code made on the customer screen): that payment is Completed with the amount received.</summary>
    CompletedPayment,

    /// <summary>A payment without a customer: the code matches no saved invoice (Stop sale, an unknown code, a transfer short of the sale awaiting it); DEBT-02.6.</summary>
    WithoutCustomer,

    /// <summary>Its bank reference is already recorded: nothing is recorded again (AC-07).</summary>
    AlreadyRecorded,

    /// <summary>Not a transfer GFR-08 records; see the reason.</summary>
    Ignored,
}

/// <param name="PaymentCode">The customer payment recorded, or the one already recorded with the reference.</param>
/// <param name="InvoiceCode">The code found in the transfer content.</param>
public sealed record BankTransferResult(string? BankReference, BankTransferOutcome Outcome, string? PaymentCode, string? InvoiceCode, BankTransferIgnoredReason? Reason);

/// <summary>The answer to the service (200): one result per transfer, in the order of the notification.</summary>
public sealed record BankNotificationReceipt(IReadOnlyList<BankTransferResult> Transactions);

/// <summary>Development only: a transfer to the receiving bank account, as the fake service would report it.</summary>
/// <param name="Content">The transfer content; the invoice code when left out.</param>
/// <param name="BankReference">A new reference when left out; send the same one again to replay a notification.</param>
/// <param name="AccountNumber">The receiving bank account when left out.</param>
public sealed record SimulateBankTransferRequest(
    string? InvoiceCode, decimal? Amount, string? Content = null, string? BankReference = null, string? AccountNumber = null, DateTimeOffset? OccurredAt = null, bool Outgoing = false);
