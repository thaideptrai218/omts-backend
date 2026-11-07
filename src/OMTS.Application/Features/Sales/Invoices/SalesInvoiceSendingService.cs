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
/// it and answers MSG-GEN-09.
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

            if (invoice!.QrDecidedAt is { } decidedAt)
            {
                return AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", "already finished"), new Dictionary<string, object?> { ["qrDecidedAt"] = decidedAt });
            }

            if (request.WantsQrCode is not { } wantsQrCode)
            {
                return AppError.Conflict(UserMessages.WantsQrCode(invoice.Code), new Dictionary<string, object?> { ["confirmationField"] = WantsQrCodeField });
            }

            // SALE-05.9 and step 10: only with a Yes, and the QR code only when a lot is Published now (SALE-05.11: never later).
            var lots = wantsQrCode
                ? await provenance.PublishInvoiceLotsAsync(invoice.Lines.Select(l => l.HarvestLotId).Distinct().ToList(), cancellationToken)
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
                var origins = lots.OrderBy(l => l.LotCode, StringComparer.Ordinal).Select(l => new InvoiceLotOriginView(l.HarvestLotId, l.LotCode, l.State, l.Missing)).ToList();
                return await AnswerAsync(invoice.Id, sending, notSent, origins, cancellationToken);
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

            // PRE-2: not while the latest sending is still being handed to the service.
            if (SalesInvoiceViews.LatestStatus(await views.SendingsAsync(invoice!.Id, cancellationToken)) == StatusIds.MessageDelivery.Sending)
            {
                return AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", statusCatalog.DisplayNameOf(StatusIds.MessageDelivery.Sending)));
            }

            var (sending, notSent) = await messages.QueueAsync(invoice, corrected: false, cancellationToken);
            if (sending is null)
            {
                return await AnswerAsync(invoice.Id, null, notSent, [], cancellationToken);
            }

            unitOfWork.MarkChanged(invoice);
            if (await SavedAsync(attempt, cancellationToken))
            {
                return await AnswerAsync(invoice.Id, sending, null, [], cancellationToken);
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

    // SALE-06 steps 4-5: the result given back is that of this sending; the invoice is read again as saved.
    private async Task<SalesInvoiceSent> AnswerAsync(
        Guid invoiceId, Notification? sending, UserMessage? notSent, IReadOnlyList<InvoiceLotOriginView> lots, CancellationToken cancellationToken)
    {
        if (sending is not null)
        {
            await traderMessages.DeliverAsync(sending);
        }

        var status = sending?.DeliveryStatusId ?? StatusIds.MessageDelivery.NotSent;
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken) ?? throw new InvalidOperationException($"Sales invoice {invoiceId} was just saved and cannot be read.");
        var view = await views.ViewAsync(invoice, cancellationToken);
        return new SalesInvoiceSent(view, status, statusCatalog.DisplayNameOf(status), view.QrUrl, lots, notSent?.Text, notSent?.Code);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
