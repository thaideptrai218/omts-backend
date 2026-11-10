using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// GFR-08 "Recording": the customer payment of a reported transfer, with the method VietQR code of the invoice, Awaiting confirmation and recorded by the system, holding the
/// invoice code, the amount, the bank reference and the time of the transfer; written to the audit log with the system as the actor. In the caller's unit of work.
/// </summary>
public sealed class TransferPaymentWriter(ICustomerPaymentRepository payments, AuditLogService audit, IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    /// <param name="invoiceId">The saved invoice of the code; null for a payment without a customer.</param>
    /// <param name="customerId">The customer of the invoice; null for a walk-in invoice and without an invoice.</param>
    /// <param name="invoiceCode">The code of the transfer content.</param>
    public CustomerPayment Record(BankTransferNotice notice, string code, Guid? invoiceId, Guid? customerId, string invoiceCode)
    {
        // The business date of the transfer, never after today (a bank clock ahead of ours).
        var date = BusinessClock.ToBusinessDate(notice.OccurredAt);
        var today = BusinessClock.ToBusinessDate(timeProvider.GetUtcNow());
        var payment = new CustomerPayment
        {
            Code = code,
            CustomerId = customerId,
            Kind = CustomerPaymentKind.Payment,
            PaymentDate = date > today ? today : date,
            Amount = notice.Amount,
            Method = CustomerPaymentMethod.VietQrInvoice,
            InvoiceId = invoiceId,
            BankReference = notice.BankReference.Trim(),
            TransferInvoiceCode = invoiceId is null ? invoiceCode : null,
            TransferredAt = notice.OccurredAt,
        };
        payments.Add(payment);
        audit.Record(new AuditEntry(SalesInvoiceWriter.PaymentRecordedAction, SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            After = new
            {
                payment.Kind, payment.Method, payment.Amount, payment.PaymentDate, payment.CustomerId, InvoiceCode = invoiceCode, InvoiceFound = invoiceId is not null,
                payment.BankReference, payment.TransferredAt, Status = statusCatalog.CodeOf(payment.StatusId),
            },
        });
        return payment;
    }
}
