using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// The moves of STD-CUSTOMER-PAYMENT that DEBT-02 makes on a saved payment, with their audit rows (GBR-02), in the caller's unit of work: Awaiting confirmation →
/// Completed (DEBT-02.2, changes no debt), the same move when settling a payment without a customer (DEBT-02.6), Awaiting confirmation or Completed → Cancelled with a reason
/// (DEBT-02.4, the payment leaves the debt), and Completed → Awaiting confirmation when the money paid back that settled a payment is cancelled. The caller checks the status first.
/// </summary>
public sealed class CustomerPaymentStatusChanges(AuditLogService audit, IStatusCatalog statusCatalog, ICurrentUser currentUser, TimeProvider timeProvider)
{
    public const string ConfirmedAction = "CustomerPayment.Confirmed";
    public const string CancelledAction = "CustomerPayment.Cancelled";
    public const string CancelledAfterConfirmAction = "CustomerPayment.CancelledAfterConfirm";
    public const string SettledAction = "CustomerPayment.Settled";
    public const string SettlementUndoneAction = "CustomerPayment.SettlementUndone";

    public void Confirm(CustomerPayment payment)
    {
        var before = payment.StatusId;
        SetCompleted(payment);
        Audit(payment, ConfirmedAction, before, null);
    }

    /// <summary>DEBT-02.6 step 4: Completed (the Accountant found the money on the bank statement; it enters the cash book), and how it was settled.</summary>
    /// <param name="settlement">AttachedToCustomer or PaidBack.</param>
    /// <param name="paidBack">The money paid back; null when the payment went to a profile.</param>
    /// <param name="allocations">The invoices the payment pays once attached to a profile.</param>
    public void Settle(CustomerPayment payment, string settlement, CustomerPayment? paidBack, IReadOnlyList<(Guid InvoiceId, decimal Amount)> allocations)
    {
        var before = payment.StatusId;
        SetCompleted(payment);
        audit.Record(new AuditEntry(SettledAction, SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            Before = new { Status = statusCatalog.CodeOf(before), CustomerId = (Guid?)null },
            After = new
            {
                Status = statusCatalog.CodeOf(payment.StatusId), Settlement = settlement, payment.CustomerId, payment.CashAccountId, PaidBackCode = paidBack?.Code, PaidBackAmount = paidBack?.Amount,
                Allocations = allocations.Select(a => new { a.InvoiceId, a.Amount }).ToList(),
            },
        });
    }

    /// <summary>The money paid back that settled this payment was cancelled (DEBT-02.4): the payment goes back to the list, out of the cash book, and is settled again.</summary>
    public void UndoSettlement(CustomerPayment payment, string paidBackCode)
    {
        var before = payment.StatusId;
        payment.StatusId = StatusIds.CustomerPayment.AwaitingConfirmation;
        payment.ConfirmedByAccountId = null;
        payment.ConfirmedAt = null;
        audit.Record(new AuditEntry(SettlementUndoneAction, SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            Before = new { Status = statusCatalog.CodeOf(before) },
            After = new { Status = statusCatalog.CodeOf(payment.StatusId), PaidBackCode = paidBackCode },
        });
    }

    private void SetCompleted(CustomerPayment payment)
    {
        payment.StatusId = StatusIds.CustomerPayment.Completed;
        payment.ConfirmedByAccountId = currentUser.AccountId;
        payment.ConfirmedAt = timeProvider.GetUtcNow();
    }

    public void Cancel(CustomerPayment payment, string reason)
    {
        var before = payment.StatusId;
        payment.StatusId = StatusIds.CustomerPayment.Cancelled;
        payment.CancelledByAccountId = currentUser.AccountId;
        payment.CancelledAt = timeProvider.GetUtcNow();
        payment.CancelReason = reason;
        Audit(payment, CancelledAction, before, reason);
    }

    /// <summary>
    /// A cancellation that found the payment Confirmed although the client sent no row version, so it may have lost a race with a confirmation (PM answer 12/11/2026, OMTS-224 [1.6]):
    /// allowed, and beside the usual <see cref="CancelledAction"/> row it writes <see cref="CancelledAfterConfirmAction"/> saying the payment was Confirmed when it was cancelled,
    /// with who confirmed it and when, the payment code and the reason.
    /// </summary>
    public void CancelConfirmed(CustomerPayment payment, string reason)
    {
        var (confirmedAt, confirmedBy) = (payment.ConfirmedAt, payment.ConfirmedByAccountId);
        Cancel(payment, reason);
        audit.Record(new AuditEntry(CancelledAfterConfirmAction, SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.CustomerPayment.Completed), ConfirmedAt = confirmedAt, ConfirmedByAccountId = confirmedBy },
            After = new { Status = statusCatalog.CodeOf(payment.StatusId), payment.Kind, payment.Amount, payment.CustomerId },
            Reason = reason,
        });
    }

    private void Audit(CustomerPayment payment, string action, int before, string? reason) =>
        audit.Record(new AuditEntry(action, SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            Before = new { Status = statusCatalog.CodeOf(before) },
            After = new { Status = statusCatalog.CodeOf(payment.StatusId), payment.Kind, payment.Amount, payment.CustomerId },
            Reason = reason,
        });
}
