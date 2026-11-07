using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-06 steps 1-3: reads the phone of the customer profile, prepares MSG-SALE-15 at the latest values of the invoice and adds the sending, Sending, to the caller's save.
/// The caller delivers it after the save (<see cref="TraderMessageService.DeliverAsync"/>). The message is not a notification type and is never in a list (BR-SALE-09).
/// </summary>
public sealed class SalesInvoiceMessages(ICooperativeRepository cooperatives, TraderMessageService traderMessages, IOptions<ProvenanceOptions> provenance)
{
    /// <summary>The type of the Notification rows that keep the sendings of an invoice; not registered in <see cref="NotificationTypes.All"/>, so never dispatched as a notification.</summary>
    public const string MessageType = "SALES_INVOICE_MESSAGE";

    public const string Title = "Sales invoice";

    /// <summary>The sending row, or MSG-SALE-16 when nothing can be sent: a walk-in customer, or a profile without a phone number (SALE-06.0.E1).</summary>
    /// <param name="corrected">Sent again after a correction (SALE-07, PR 10).</param>
    public async Task<(Notification? Sending, UserMessage? NotSent)> QueueAsync(SalesInvoice invoice, bool corrected, CancellationToken cancellationToken)
    {
        if (invoice.Customer is not { } customer || string.IsNullOrWhiteSpace(customer.PhoneNumber))
        {
            return (null, UserMessages.InvoiceNotSent(invoice.Code, hasCustomerProfile: invoice.Customer is not null));
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var paid = PaidAmount(invoice);
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

    /// <summary>
    /// The part of the total that the payments of the invoice which are not Cancelled cover; the cash subtracted from the debt (MSG-SALE-11) is not part of it. Payments without
    /// an invoice are allocated to the oldest invoices by BR-DEBT-05 (PR 8a); until then only the invoice's own payments count here.
    /// </summary>
    public static decimal PaidAmount(SalesInvoice invoice) => Math.Min(
        invoice.Total,
        invoice.Payments.Where(p => p.Kind == CustomerPaymentKind.Payment && p.StatusId != StatusIds.CustomerPayment.Cancelled).Sum(p => p.Amount));
}
