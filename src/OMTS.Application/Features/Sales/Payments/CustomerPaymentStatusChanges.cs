using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// The two moves of STD-CUSTOMER-PAYMENT that DEBT-02 makes on a saved payment, with their audit rows (GBR-02), in the caller's unit of work: Awaiting confirmation →
/// Confirmed (DEBT-02.2, changes no debt) and Awaiting confirmation or Confirmed → Cancelled with a reason (DEBT-02.4, the payment leaves the debt).
/// The caller checks the status first.
/// </summary>
public sealed class CustomerPaymentStatusChanges(AuditLogService audit, IStatusCatalog statusCatalog, ICurrentUser currentUser, TimeProvider timeProvider)
{
    public const string ConfirmedAction = "CustomerPayment.Confirmed";
    public const string CancelledAction = "CustomerPayment.Cancelled";

    public void Confirm(CustomerPayment payment)
    {
        var before = payment.StatusId;
        payment.StatusId = StatusIds.CustomerPayment.Completed;
        payment.ConfirmedByAccountId = currentUser.AccountId;
        payment.ConfirmedAt = timeProvider.GetUtcNow();
        Audit(payment, ConfirmedAction, before, null);
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
