using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The cash a collector holds that is still Awaiting confirmation: <paramref name="Count"/> payments, <paramref name="OverdueCount"/> of them older than the limit, their sum and the oldest time.</summary>
public sealed record AwaitingCashSummary(int Count, int OverdueCount, decimal Amount, DateTimeOffset? OldestPaidAt);

/// <summary>The customer payments of DEBT-02 (BR-DEBT-03), with their customer profile and the invoice they came with.</summary>
public interface ICustomerPaymentRepository
{
    void Add(CustomerPayment payment);

    /// <summary>Deletes a part of a payment (a short transfer cuts its allocation).</summary>
    void RemoveAllocation(CustomerPaymentAllocation allocation);

    /// <summary>Adds a part to a payment that is already saved (a transfer attached to a customer profile sets its money against the invoices); a new payment brings its own parts.</summary>
    void AddAllocation(CustomerPaymentAllocation allocation);

    /// <summary>The id of the payment with this code regardless of letter case, other than <paramref name="exceptPaymentId"/>; cancelled ones included.</summary>
    Task<Guid?> FindIdByCodeAsync(string code, Guid? exceptPaymentId, CancellationToken cancellationToken);

    /// <summary>The payments waiting for their transfer, tracked, with their customer, allocations (and the invoices of those) and bank account, oldest first.</summary>
    Task<IReadOnlyList<CustomerPayment>> GetPendingTransfersAsync(CancellationToken cancellationToken);

    /// <summary>Tracked, with its customer, invoice, allocations and bank account.</summary>
    Task<CustomerPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Tracked, with their customers and invoices; the ids not found are left out.</summary>
    Task<IReadOnlyList<CustomerPayment>> GetForEditAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken);

    /// <summary>Read only, with its customer and invoice.</summary>
    Task<CustomerPayment?> GetAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<CustomerPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The latest payment that is not cancelled with this bank reference, whatever its customer (MSG-DEBT-05), with its customer.</summary>
    Task<CustomerPayment?> FindNotCancelledByBankReferenceAsync(string bankReference, CancellationToken cancellationToken);

    /// <summary>
    /// The payment that already records the transfer with this bank reference (GFR-08 "not recorded again"), read only: a VietQR payment, cancelled or not, or a payment the
    /// Accountant recorded from the bank statement that is not cancelled (BR-DEBT-01/03). The reference is compared as MSG-DEBT-05 compares it (trimmed, the column's collation).
    /// </summary>
    Task<CustomerPayment?> FindRecordedTransferAsync(string bankReference, CancellationToken cancellationToken);

    /// <summary>
    /// For each invoice code: Σ of the transfers recorded with it and no invoice (GFR-08) that are still Awaiting confirmation (not cancelled, not settled by the Accountant,
    /// DEBT-02.6), i.e. transfers short of the sale awaiting them; one query for every code. A code with none is left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> ReceivedWithoutInvoiceAsync(IReadOnlyCollection<string> invoiceCodes, CancellationToken cancellationToken);

    /// <summary>
    /// The payments Awaiting confirmation, oldest first (payment date, then code), and their number and sum. With <paramref name="paidBefore"/> only those paid before that time (the filter
    /// "waiting more than N days"); the sum is always that of every payment Awaiting confirmation.
    /// </summary>
    Task<(IReadOnlyList<CustomerPayment> Items, int Total, decimal TotalAmount)> AwaitingConfirmationAsync(int page, int pageSize, DateTimeOffset? paidBefore, CancellationToken cancellationToken);

    /// <summary>
    /// The cash Awaiting confirmation that <paramref name="collectorAccountId"/> collected (BR-SALE-07 (3): the Zone Manager hands it to the Accountant): how many payments, how many of them were paid
    /// before <paramref name="overdueBefore"/>, their sum and the time of the oldest.
    /// </summary>
    Task<AwaitingCashSummary> AwaitingCashOfAsync(Guid collectorAccountId, DateTimeOffset overdueBefore, CancellationToken cancellationToken);

    /// <summary>True when money paid back that is not cancelled stands for this payment (it settled the payment, DEBT-02.6).</summary>
    Task<bool> HasMoneyPaidBackAsync(Guid paymentId, CancellationToken cancellationToken);
}
