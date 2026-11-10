using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using Microsoft.Extensions.Logging;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// GFR-08 – the Payment Notification Service reports transfers (API-13). A transfer is recorded once, by its bank reference. First the content is read for the code of a payment
/// waiting for its transfer (a VietQR code made on the customer screen) to the bank account of that payment: the payment is Completed with the amount that actually arrived (a short
/// transfer cuts its allocation from the newest invoice back, an over one leaves the rest unallocated, and the note says what the customer transferred and what the voucher asked).
/// Otherwise a transfer to the receiving bank account whose content carries an invoice code is recorded as a Completed customer payment: of the saved invoice with that code (its
/// customer, none for a walk-in customer; also a Cancelled invoice); or it completes the sale awaiting it (SALE-05 step 8); or, when the code matches no saved invoice (Stop sale, an
/// unknown code, less than the sale awaits), it has no customer, stays Awaiting confirmation and the Accountant handles it (DEBT-02.6). Each transfer is saved on its own; the
/// system is the actor.
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
    ICashAccountRepository cashAccounts,
    PendingSaleCompletion completion,
    TransferPaymentWriter transferWriter,
    CustomerPaymentCodeService paymentCodes,
    CustomerDebtReader debts,
    CustomerPaymentNotifications notifications,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    PendingSaleWriter pendingWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<BankTransferNoticeService> logger)
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
        var now = timeProvider.GetUtcNow();
        if (BankTransferRules.NotATransfer(notice, now) is { } notTransfer)
        {
            return new BankTransferResult(reference, BankTransferOutcome.Ignored, null, BankTransferRules.InvoiceCodeIn(notice.Content), notTransfer);
        }

        string? paymentCode = null;
        for (var attempt = 1; ; attempt++)
        {
            if (await payments.FindRecordedTransferAsync(reference!, cancellationToken) is { } recorded)
            {
                return new BankTransferResult(reference, BankTransferOutcome.AlreadyRecorded, recorded.Code, BankTransferRules.InvoiceCodeIn(notice.Content), null);
            }

            BankTransferOutcome outcome;
            string? invoiceCode = BankTransferRules.InvoiceCodeIn(notice.Content);
            if (await FindPendingPaymentAsync(notice, cancellationToken) is { } pending)
            {
                paymentCode = pending.Code;
                outcome = await CompletePendingAsync(pending, notice, cancellationToken);
            }
            else
            {
                var account = (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount;
                if (BankTransferRules.Ignored(notice, account?.AccountNumber, now) is { } reason)
                {
                    return new BankTransferResult(reference, BankTransferOutcome.Ignored, null, invoiceCode, reason);
                }

                paymentCode ??= await paymentCodes.NextAsync(cancellationToken);
                outcome = await RecordAsync(notice, invoiceCode!, paymentCode, cancellationToken);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return new BankTransferResult(reference, outcome, paymentCode, invoiceCode, null);
            }
            catch (Exception exception) when ((exception is ConcurrencyConflictException or DuplicateRecordException or ForeignKeyViolationException) && attempt < MaxAttempts)
            {
                // Nothing was saved; read again: the parallel save recorded this reference, or ended the sale another way.
                unitOfWork.DiscardChanges();
            }
        }
    }

    /// <summary>The payment waiting for its transfer whose code the content carries and whose bank account the money arrived in; the longest code when several match.</summary>
    private async Task<CustomerPayment?> FindPendingPaymentAsync(BankTransferNotice notice, CancellationToken cancellationToken) =>
        (await payments.GetPendingTransfersAsync(cancellationToken))
            .Where(p => p.Kind == CustomerPaymentKind.Payment && p.CashAccount is { } account && BankTransferRules.SameAccount(notice.AccountNumber, account.AccountNumber)
                && BankTransferRules.ContainsPaymentCode(notice.Content, p.Code))
            .OrderByDescending(p => p.Code.Length)
            .FirstOrDefault();

    /// <summary>
    /// Pending transfer → Completed with what arrived. A transfer short of the payment cuts the allocation from the newest invoice back until the money and the discount cover it;
    /// one over the payment leaves the rest unallocated (it covers the oldest invoices as an advance). The payment takes the time of the transfer.
    /// </summary>
    private async Task<BankTransferOutcome> CompletePendingAsync(CustomerPayment payment, BankTransferNotice notice, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var asked = payment.Amount;
        var received = notice.Amount;
        var debtBefore = payment.CustomerId is { } customerId ? await debts.DebtAsync(customerId, cancellationToken) : (decimal?)null;
        if (payment.Customer is { } customer)
        {
            // A merge or another payment of the profile at this moment conflicts, so the debt told to the Owner rests on a true reading.
            unitOfWork.MarkChanged(customer);
        }

        var parts = payment.Allocations.Select(a => (a.InvoiceId, a.Invoice.InvoicedAt, a.Invoice.Code, a.Amount)).ToList();
        if (parts.Sum(p => p.Amount) > received + payment.DiscountAmount)
        {
            var kept = CustomerPaymentRules.TrimParts(parts, received + payment.DiscountAmount).ToDictionary(p => p.InvoiceId, p => p.Amount);
            foreach (var allocation in payment.Allocations.ToList())
            {
                if (kept.TryGetValue(allocation.InvoiceId, out var amount))
                {
                    allocation.Amount = amount;
                }
                else
                {
                    payments.RemoveAllocation(allocation);
                }
            }
        }

        var before = statusCatalog.CodeOf(payment.StatusId);
        var paidAt = (notice.OccurredAt > now ? now : notice.OccurredAt).ToOffset(BusinessClock.UtcOffset);
        payment.Amount = received;
        payment.PaidAt = paidAt;
        payment.PaymentDate = BusinessClock.ToBusinessDate(paidAt);
        payment.BankReference = notice.BankReference.Trim();
        payment.TransferredAt = notice.OccurredAt;
        payment.StatusId = StatusIds.CustomerPayment.Completed;
        payment.ConfirmedAt = now;
        if (received != asked)
        {
            var difference = CustomerPaymentRules.TransferDifferenceNote(received, asked);
            payment.Note = string.IsNullOrWhiteSpace(payment.Note) ? difference : $"{payment.Note.Trim()} · {difference}";
        }

        audit.Record(new AuditEntry("CustomerPayment.TransferReceived", SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            Before = new { Status = before, Amount = asked },
            After = new
            {
                Status = statusCatalog.CodeOf(payment.StatusId), payment.Amount, payment.PaidAt, payment.BankReference,
                Allocations = payment.Allocations.Select(a => new { a.InvoiceId, a.Amount }).ToList(),
            },
        });
        if (debtBefore is { } debt)
        {
            await notifications.TransactionAsync(payment, cancelled: false, CustomerPaymentRules.DebtAfter(debt, payment.Kind, received, cancelled: false, payment.DiscountAmount), cancellationToken);
        }

        return BankTransferOutcome.CompletedPayment;
    }

    /// <summary>The cash account that is the cooperative's receiving bank account, the one every VietQR code pays into; null when the cash book holds none.</summary>
    private async Task<Guid?> ReceivingCashAccountAsync(CancellationToken cancellationToken) =>
        (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount is { } receiving
            ? (await cashAccounts.FindBankAccountAsync(receiving.BankBin, receiving.AccountNumber, cancellationToken))?.Id
            : null;

    private async Task<BankTransferOutcome> RecordAsync(BankTransferNotice notice, string code, string paymentCode, CancellationToken cancellationToken)
    {
        var receivingAccountId = await ReceivingCashAccountAsync(cancellationToken);
        if (await invoices.FindByCodeAsync(code, cancellationToken) is { } invoice)
        {
            // BR-SALE-07 (2), (7): after Pay later it pays the invoice; after cash, or on a cancelled invoice, it lowers the customer debt (BR-DEBT-02). A merge at this moment
            // conflicts on the profile, so the payment never stays on a merged profile.
            if (invoice.CustomerId is { } customerId && await customers.GetForEditAsync(customerId, cancellationToken) is { } customer)
            {
                unitOfWork.MarkChanged(customer);
            }

            transferWriter.Record(notice, paymentCode, invoice.Id, invoice.CustomerId, code).CashAccountId = receivingAccountId;
            return BankTransferOutcome.PaidInvoice;
        }

        if (await pendingSales.GetForEditAsync(code, cancellationToken) is { Status: PendingSaleStatus.Awaiting } sale && notice.Amount >= sale.ToPayAmount)
        {
            try
            {
                var saved = await completion.CompleteAsync(sale, cancellationToken);
                transferWriter.Record(notice, paymentCode, saved.Id, saved.CustomerId, code).CashAccountId = receivingAccountId;
                return BankTransferOutcome.CompletedSale;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Money that arrived is never refused: a sale that can no longer be saved ends, and its transfer waits for the Accountant without a customer (DEBT-02.6).
                logger.LogWarning(exception, "The transfer for pending sale {Code} arrived, but the sale could not be saved; recorded without a customer", code);
                unitOfWork.DiscardChanges();
                if (await pendingSales.GetForEditAsync(code, cancellationToken) is { Status: PendingSaleStatus.Awaiting } unsaved)
                {
                    pendingWriter.StopOnTransfer(unsaved, exception is PendingSaleNotSavableException refused ? refused.Message : "the sale could not be saved.");
                }
            }
        }

        transferWriter.Record(notice, paymentCode, invoiceId: null, customerId: null, code).CashAccountId = receivingAccountId;
        return BankTransferOutcome.WithoutCustomer;
    }
}
