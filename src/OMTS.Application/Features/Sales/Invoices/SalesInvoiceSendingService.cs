using System.Security.Cryptography;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Traceability.Publishing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The end of the sale (SALE-05 steps 9-11: the QR question, the QR code, Send sales invoice) and Resend (SALE-06.1). Both need SALES_INVOICES Create in the zone of the
/// invoice: the Zone Manager of the zone and the Owner; the Accountant only views (BR-SALE-09, §I.4.4). The message is delivered after the save and its result never undoes
/// it (GFR-06).
/// </summary>
/// <remarks>
/// Each sending marks the invoice changed, so two parallel Resends (or a Resend and the end of the sale) cannot both pass the "latest sending is not Sending" check: the
/// loser reads again and is refused, or, for the end of the sale, saves on the fresh reads. Two parallel ends of the sale both set the decision: one wins, the other reads
/// it and answers the current result. The end of the sale happens once: called again (a mobile retry after a lost answer), it answers the current result and sends
/// nothing. Resend needs the end of the sale first (BR-SALE-08: the QR code is decided at the end of the sale, before any sending).
/// </remarks>
public sealed class SalesInvoiceSendingService(
    ISalesInvoiceRepository invoices,
    IProvenancePublisher provenance,
    SalesInvoiceMessages messages,
    SalesInvoiceViews views,
    TraderMessageService traderMessages,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string FinishedAction = "SalesInvoice.SaleFinished";
    public const string WantsQrCodeField = "wantsQrCode";

    // MSG-GEN-09 "{record} is {status} and cannot be used for this action." for a Resend before the end of the sale.
    private const string NotFinished = "not finished";
    private const int MaxAttempts = 3;

    // BR-ORIGIN-01: 128 random bits cannot be guessed; base64url gives 22 characters.
    private const int QrTokenBytes = 16;

    public async Task<Result<SalesInvoiceSent>> FinishAsync(Guid invoiceId, FinishSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var invoice = await invoices.GetForSendingAsync(invoiceId, cancellationToken);
            if (await RefusedAsync(invoice, cancellationToken) is { } refused)
            {
                return refused;
            }

            // Once only: an end of the sale already made (also by a parallel call) answers its result again, whatever the answer given now; nothing is sent again.
            if (invoice!.QrDecidedAt is not null)
            {
                return await CurrentAsync(invoice.Id, cancellationToken);
            }

            if (request.WantsQrCode is not { } wantsQrCode)
            {
                return AppError.Conflict(UserMessages.WantsQrCode(invoice.Code), new Dictionary<string, object?> { ["confirmationField"] = WantsQrCodeField });
            }

            // SALE-05.9 and step 10: only with a Yes, TRACE-01 publishes the lots that are Not published and ready, in this save; the QR code only when a lot is Published now
            // (SALE-05.11: never later).
            var lots = wantsQrCode
                ? await provenance.PublishInvoiceLotsAsync(invoice.Code, invoice.Lines.Select(l => l.HarvestLotId).Distinct().ToList(), cancellationToken)
                : [];
            var now = timeProvider.GetUtcNow();
            if (lots.Any(l => l.State == HarvestLotOriginState.Published))
            {
                invoice.QrToken = Base64Url(RandomNumberGenerator.GetBytes(QrTokenBytes));
                invoice.QrCreatedAt = now;
            }

            invoice.QrDecidedAt = now;
            audit.Record(new AuditEntry(FinishedAction, SalesInvoiceWriter.RecordType)
            {
                RecordId = invoice.Id,
                RecordCode = invoice.Code,
                ZoneId = invoice.ZoneId,
                // The token itself stays out of the log: whoever holds it opens the page.
                After = new { WantsQrCode = wantsQrCode, QrCodeCreated = invoice.QrToken is not null, PublishedLots = lots.Where(l => l.State == HarvestLotOriginState.Published).Select(l => l.LotCode).Order().ToList() },
            });
            var (sending, notSent) = await messages.QueueAsync(invoice, corrected: false, cancellationToken);
            if (await SavedAsync(attempt, cancellationToken))
            {
                return await AnswerAsync(invoice.Id, sending, notSent, InvoiceLotOriginView.Of(lots), cancellationToken);
            }
        }
    }

    public async Task<Result<SalesInvoiceSent>> ResendAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var invoice = await invoices.GetForSendingAsync(invoiceId, cancellationToken);
            if (await RefusedAsync(invoice, cancellationToken) is { } refused)
            {
                return refused;
            }

            // The first sending is part of the end of the sale, after the QR question: a Resend before it would send an invoice whose QR code is not decided yet.
            if (invoice!.QrDecidedAt is null)
            {
                return AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", NotFinished));
            }

            // PRE-2: not while the latest sending is still being handed to the service.
            if (SalesInvoiceViews.LatestStatus(await views.SendingsAsync(invoice.Id, cancellationToken)) == StatusIds.MessageDelivery.Sending)
            {
                return AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", statusCatalog.DisplayNameOf(StatusIds.MessageDelivery.Sending)));
            }

            // Nothing to send (walk-in, no phone) still writes the sending, Not sent, so the invoice no longer shows an older Sent (SALE-06 POST-1).
            var (sending, notSent) = await messages.QueueAsync(invoice, corrected: false, cancellationToken);
            unitOfWork.MarkChanged(invoice);
            if (await SavedAsync(attempt, cancellationToken))
            {
                return await AnswerAsync(invoice.Id, sending, notSent, [], cancellationToken);
            }
        }
    }

    /// <summary>404, 403 MSG-GEN-08 outside the zones where the caller sells (BR-SALE-08), 409 MSG-GEN-09 for a Cancelled invoice (PRE-1).</summary>
    private async Task<AppError?> RefusedAsync(SalesInvoice? invoice, CancellationToken cancellationToken)
    {
        if (invoice is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, invoice.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return invoice.StatusId == StatusIds.SalesInvoice.Completed
            ? null
            : AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", statusCatalog.DisplayNameOf(invoice.StatusId)));
    }

    /// <summary>False when a parallel save won: nothing of this attempt is kept and the caller reads again. The last attempt lets the conflict answer 409 MSG06.</summary>
    private async Task<bool> SavedAsync(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when ((exception is ConcurrencyConflictException or DuplicateRecordException) && attempt < MaxAttempts)
        {
            unitOfWork.DiscardChanges();
            return false;
        }
    }

    // SALE-06 steps 4-5: the result given back is that of this sending; the invoice is read again as saved. A row that had nothing to send is already Not sent.
    internal async Task<SalesInvoiceSent> AnswerAsync(
        Guid invoiceId, Notification? sending, UserMessage? notSent, IReadOnlyList<InvoiceLotOriginView> lots, CancellationToken cancellationToken)
    {
        if (notSent is null && sending is not null)
        {
            await traderMessages.DeliverAsync(sending);
        }

        var status = sending?.DeliveryStatusId ?? StatusIds.MessageDelivery.NotSent;
        var view = await ViewOfAsync(invoiceId, cancellationToken);
        return new SalesInvoiceSent(view, status, statusCatalog.DisplayNameOf(status), view.QrUrl, lots, notSent?.Text, notSent?.Code);
    }

    // The end of the sale called again: its decision, the QR code and the status of the latest sending as they are now (Sending while a parallel call still delivers it);
    // MSG-SALE-16 again when that sending had nothing to send. The origin states of the lots are not kept, so the lots are empty.
    internal async Task<SalesInvoiceSent> CurrentAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var view = await ViewOfAsync(invoiceId, cancellationToken);
        var nothingSent = view.Sendings.Count > 0 && view.Sendings[^1].PhoneNumber is null && view.DeliveryStatusId == StatusIds.MessageDelivery.NotSent;
        var notSent = nothingSent ? UserMessages.InvoiceNotSent(view.Code, hasCustomerProfile: view.Customer is not null) : null;
        return new SalesInvoiceSent(view, view.DeliveryStatusId, view.DeliveryStatus, view.QrUrl, [], notSent?.Text, notSent?.Code);
    }

    internal async Task<SalesInvoiceView> ViewOfAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken) ?? throw new InvalidOperationException($"Sales invoice {invoiceId} was just saved and cannot be read.");
        return await views.ViewAsync(invoice, cancellationToken);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
