using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-06 steps 1-3: reads the phone of the customer profile, prepares MSG-SALE-15 at the latest values of the invoice and adds the sending, Sending, to the caller's save.
/// The caller delivers it after the save (<see cref="TraderMessageService.DeliverAsync"/>). The message is not a notification type and is never in a list (BR-SALE-09).
/// </summary>
public sealed class SalesInvoiceMessages(
    ICooperativeRepository cooperatives, TraderMessageService traderMessages, CustomerDebtReader debts, IOptions<ProvenanceOptions> provenance)
{
    /// <summary>The type of the Notification rows that keep the sendings of an invoice; not registered in <see cref="NotificationTypes.All"/>, so never dispatched as a notification.</summary>
    public const string MessageType = "SALES_INVOICE_MESSAGE";

    public const string Title = "Sales invoice";

    /// <summary>Error code of the Not sent row of a walk-in customer (MSG-SALE-16 cause).</summary>
    public const string NoCustomerProfile = "NO_CUSTOMER_PROFILE";

    /// <summary>Error code of the Not sent row of a customer profile without a phone number (MSG-SALE-16 cause).</summary>
    public const string NoPhoneNumber = "NO_PHONE_NUMBER";

    /// <summary>The type of the rows that keep the cancellation notice (SALE-08 step 7): not a sending of the invoice message, and in no list (BR-SALE-09).</summary>
    public const string CancellationType = "SALES_INVOICE_CANCELLED_MESSAGE";

    /// <summary>
    /// MSG-SALE-22 added to the caller's save as Sending, to deliver after it; null when nothing is sent (SALE-08.1: a walk-in customer or a profile without a phone number).
    /// </summary>
    /// <param name="paidAmount">The payments that are not Cancelled after the cancellation.</param>
    public async Task<Notification?> QueueCancellationAsync(SalesInvoice invoice, decimal paidAmount, CancellationToken cancellationToken)
    {
        if (invoice.Customer is not { } customer || string.IsNullOrWhiteSpace(customer.PhoneNumber))
        {
            return null;
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var message = UserMessages.InvoiceCancellationNotice(
            cooperative.ShortName ?? cooperative.FullName, invoice.Code, invoice.InvoiceDate, invoice.Total, paidAmount, cooperative.PhoneNumber ?? "the cooperative");
        var content = new NotificationContent(CancellationType, Title, message, new RelatedRecord(RelatedRecordTypes.SalesInvoice, invoice.Id, invoice.Code));
        return traderMessages.Queue(customer.AccountId, customer.PhoneNumber, content);
    }

    /// <summary>
    /// The sending row, Sending, to deliver after the save. When nothing can be sent (a walk-in customer, or a profile without a phone number, SALE-06.0.E1) the row is
    /// already Not sent, has no phone and keeps MSG-SALE-16 with its cause as the error code, and <c>NotSent</c> is MSG-SALE-16: the invoice then shows Not sent even after
    /// an earlier Sent (SALE-06 POST-1). That row names no receiving account, so the DEBT-01 count never sees it.
    /// </summary>
    /// <param name="corrected">Sent again after a correction (SALE-07, PR 10).</param>
    public async Task<(Notification Sending, UserMessage? NotSent)> QueueAsync(SalesInvoice invoice, bool corrected, CancellationToken cancellationToken)
    {
        if (invoice.Customer is not { } customer || string.IsNullOrWhiteSpace(customer.PhoneNumber))
        {
            var notSent = UserMessages.InvoiceNotSent(invoice.Code, hasCustomerProfile: invoice.Customer is not null);
            var record = new NotificationContent(MessageType, Title, notSent, new RelatedRecord(RelatedRecordTypes.SalesInvoice, invoice.Id, invoice.Code));
            return (traderMessages.NotSent(invoice.Customer?.AccountId, record, invoice.Customer is null ? NoCustomerProfile : NoPhoneNumber), notSent);
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        // BR-DEBT-05: what is still unpaid of this invoice once the customer's payments are allocated, oldest invoices first (Σ unpaid = debt).
        var paid = invoice.Total - (await debts.AllocationAsync(customer.Id, cancellationToken)).UnpaidOf(invoice.Id);
        var message = UserMessages.InvoiceMessage(
            cooperative.ShortName ?? cooperative.FullName,
            invoice.Code,
            corrected,
            invoice.InvoiceDate,
            invoice.Lines.Sum(l => l.Kg),
            invoice.Total,
            paid,
            invoice.Total - paid,
            cooperative.BankAccount?.AccountNumber,
            cooperative.BankAccount?.BankName,
            invoice.QrToken is { } token ? provenance.Value.LinkOf(token) : null);
        var content = new NotificationContent(MessageType, Title, message, new RelatedRecord(RelatedRecordTypes.SalesInvoice, invoice.Id, invoice.Code));
        return (traderMessages.Queue(customer.AccountId, customer.PhoneNumber, content), null);
    }
}
