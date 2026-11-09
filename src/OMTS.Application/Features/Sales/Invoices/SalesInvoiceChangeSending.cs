using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>What SALE-07 step 9 and SALE-08 step 7 send, once their save is committed: a failure here never undoes the correction or the cancellation (GFR-06).</summary>
public sealed class SalesInvoiceChangeSending(
    SalesInvoiceSendingService sending,
    ISalesInvoiceRepository invoices,
    SalesInvoiceMessages messages,
    TraderMessageService traderMessages,
    IStatusCatalog statusCatalog,
    IUnitOfWork unitOfWork,
    ILogger<SalesInvoiceChangeSending> logger)
{
    /// <summary>
    /// The corrected invoice, after the commit so the message reads the corrected values and what is now paid. Before the end of the sale nothing has been sent and the QR question is open:
    /// the end of the sale sends it. A failure to queue it never undoes the save; it leaves a Not sent row, so the invoice does not keep showing an older Sent (SALE-07 POST-5).
    /// </summary>
    public async Task<SalesInvoiceSent> SendCorrectedAsync(Guid invoiceId)
    {
        Notification? row = null;
        UserMessage? notSent = null;
        try
        {
            var invoice = await invoices.GetForSendingAsync(invoiceId, CancellationToken.None);
            if (invoice!.QrDecidedAt is null)
            {
                return await sending.CurrentAsync(invoiceId, CancellationToken.None);
            }

            (row, notSent) = await messages.QueueAsync(invoice, corrected: true, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("The corrected invoice {InvoiceId} could not be queued for sending: {Exception}", invoiceId, exception.GetType().Name);
            await RecordNotQueuedAsync(invoiceId);
            return await sending.CurrentAsync(invoiceId, CancellationToken.None);
        }

        return await sending.AnswerAsync(invoiceId, row, notSent, [], CancellationToken.None);
    }

    // The correction is committed: if even this row cannot be saved, the failure is only logged (GFR-06).
    private async Task RecordNotQueuedAsync(Guid invoiceId)
    {
        try
        {
            unitOfWork.DiscardChanges();
            var invoice = await invoices.GetForSendingAsync(invoiceId, CancellationToken.None);
            messages.AddNotQueued(invoice!);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("The Not sent row of the corrected invoice {InvoiceId} could not be saved: {Exception}", invoiceId, exception.GetType().Name);
        }
    }

    /// <summary>Delivers the cancellation notice queued with the save; null when nothing was to be sent (SALE-08.1).</summary>
    public async Task<SalesInvoiceSent> AnswerCancelledAsync(Guid invoiceId, Notification? notice)
    {
        if (notice is not null)
        {
            await traderMessages.DeliverAsync(notice);
        }

        var view = await sending.ViewOfAsync(invoiceId, CancellationToken.None);
        var status = notice?.DeliveryStatusId ?? StatusIds.MessageDelivery.NotSent;
        return new SalesInvoiceSent(view, status, statusCatalog.DisplayNameOf(status), view.QrUrl, [], null, null);
    }
}
