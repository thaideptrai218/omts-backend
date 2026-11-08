using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// GFR-08 – the Payment Notification Service reports transfers (API-13). A transfer to the receiving bank account whose content carries an invoice code is recorded once, by its
/// bank reference, as a customer payment Awaiting confirmation: of the saved invoice with that code (its customer, none for a walk-in customer; also a Cancelled invoice); or it
/// completes the sale awaiting it (SALE-05 step 8); or, when the code matches no saved invoice (Stop sale, an unknown code, less than the sale awaits), it has no customer and the
/// Accountant handles it (DEBT-02.6). Each transfer is saved on its own; the system is the actor.
/// </summary>
/// <remarks>
/// The same transfer reported twice at once: both read no payment with the reference, and the unique index on the reference of VietQR payments lets one save win; the other fails,
/// runs again and finds it. A transfer racing a change of payment or a stop of its sale loses or wins on the row of the sale, and its second attempt sees how the sale ended.
/// </remarks>
public sealed class BankTransferNoticeService(
    ICooperativeRepository cooperatives,
    ICustomerPaymentRepository payments,
    ISalesInvoiceRepository invoices,
    IPendingSaleRepository pendingSales,
    ICustomerRepository customers,
    PendingSaleCompletion completion,
    TransferPaymentWriter transferWriter,
    IDocumentNumberAllocator numbers,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const int MaxAttempts = 5;

    public async Task<BankNotificationReceipt> ReceiveAsync(IReadOnlyList<BankTransferNotice> notices, CancellationToken cancellationToken)
    {
        var results = new List<BankTransferResult>(notices.Count);
        foreach (var notice in notices)
        {
            results.Add(await ReceiveOneAsync(notice, cancellationToken));
        }

        return new BankNotificationReceipt(results);
    }

    /// <summary>Development only (<c>POST api/dev/payment-notifications</c>): a transfer as the fake service would report it, through the same rules.</summary>
    public async Task<Result<BankNotificationReceipt>> SimulateAsync(SimulateBankTransferRequest request, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        if (request.Amount is null)
        {
            invalid.Add("Amount");
        }

        if (string.IsNullOrWhiteSpace(request.InvoiceCode) && string.IsNullOrWhiteSpace(request.Content))
        {
            invalid.Add("Invoice code");
        }

        var accountNumber = request.AccountNumber ?? (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount?.AccountNumber;
        if (accountNumber is null)
        {
            invalid.Add("Account number");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var notice = new BankTransferNotice(
            request.BankReference ?? $"FAKE{Guid.NewGuid():N}"[..20],
            accountNumber!,
            request.Amount!.Value,
            request.Content ?? request.InvoiceCode,
            request.OccurredAt ?? timeProvider.GetUtcNow(),
            request.Outgoing ? BankTransferDirection.Out : BankTransferDirection.In);
        return await ReceiveAsync([notice], cancellationToken);
    }

    private async Task<BankTransferResult> ReceiveOneAsync(BankTransferNotice notice, CancellationToken cancellationToken)
    {
        // Each transfer reads afresh: the rows the previous one saved are not reused as they were tracked.
        unitOfWork.DiscardChanges();
        var reference = notice.BankReference?.Trim();
        var account = (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount;
        if (BankTransferRules.Ignored(notice, account?.AccountNumber) is { } reason)
        {
            return new BankTransferResult(reference, BankTransferOutcome.Ignored, null, BankTransferRules.InvoiceCodeIn(notice.Content), reason);
        }

        var code = BankTransferRules.InvoiceCodeIn(notice.Content)!;
        string? paymentCode = null;
        for (var attempt = 1; ; attempt++)
        {
            if (await payments.FindVietQrByBankReferenceAsync(reference!, cancellationToken) is { } recorded)
            {
                return new BankTransferResult(reference, BankTransferOutcome.AlreadyRecorded, recorded.Code, code, null);
            }

            paymentCode ??= SalesDocumentCodes.PaymentCode(await numbers.NextAsync(DocumentSequenceKeys.CustomerPayment, cancellationToken));
            var outcome = await RecordAsync(notice, code, paymentCode, cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return new BankTransferResult(reference, outcome, paymentCode, code, null);
            }
            catch (Exception exception) when ((exception is ConcurrencyConflictException or DuplicateRecordException or ForeignKeyViolationException) && attempt < MaxAttempts)
            {
                // Nothing was saved; read again: the parallel save recorded this reference, or ended the sale another way.
                unitOfWork.DiscardChanges();
            }
        }
    }

    private async Task<BankTransferOutcome> RecordAsync(BankTransferNotice notice, string code, string paymentCode, CancellationToken cancellationToken)
    {
        if (await invoices.FindByCodeAsync(code, cancellationToken) is { } invoice)
        {
            // BR-SALE-07 (2), (7): after Pay later it pays the invoice; after cash, or on a cancelled invoice, it lowers the customer debt (BR-DEBT-02). A merge at this moment
            // conflicts on the profile, so the payment never stays on a merged profile.
            if (invoice.CustomerId is { } customerId && await customers.GetForEditAsync(customerId, cancellationToken) is { } customer)
            {
                unitOfWork.MarkChanged(customer);
            }

            transferWriter.Record(notice, paymentCode, invoice.Id, invoice.CustomerId, code);
            return BankTransferOutcome.PaidInvoice;
        }

        if (await pendingSales.GetForEditAsync(code, cancellationToken) is { Status: PendingSaleStatus.Awaiting } sale && notice.Amount >= sale.ToPayAmount)
        {
            var saved = await completion.CompleteAsync(sale, cancellationToken);
            transferWriter.Record(notice, paymentCode, saved.Id, saved.CustomerId, code);
            return BankTransferOutcome.CompletedSale;
        }

        transferWriter.Record(notice, paymentCode, invoiceId: null, customerId: null, code);
        return BankTransferOutcome.WithoutCustomer;
    }
}
