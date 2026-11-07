using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Withdrawal;

/// <summary>
/// TRACE-04 Withdraw traceability record, Cooperative Owner only (BR-TRACE-06; the route checks it). Only a Published lot is withdrawn, with a reason. The withdrawal time and the reason
/// are saved on the lot, which makes it Withdrawn (BR-TRACE-02: the state is worked out from the two times), with one audit row; the provenance page then shows the withdrawal date only.
/// Each trader of the current invoices of the lot gets one notice, by SMS and, with a trader account, in the app (BR-TRACE-07). The notices are delivered after the save, and a failed one
/// undoes nothing (GFR-06): it is listed for the Owner to call. The sales, returns and yield figures of the lot do not change (POST-3).
/// </summary>
/// <remarks>
/// The withdrawal changes the lot row, so of two withdrawals of one lot one saves and the other reads the lot again and is refused (Withdrawn). A sale of the lot in the same moment
/// marks the lot changed (SALE-05), so the withdrawal reads the invoices again and its trader is notified too. The end of a sale does not write a lot that is already Published, so
/// parallel ends of the sales of one lot never conflict: a QR code decided just before a withdrawal saves is the serial order "end of the sale, then withdrawal" (BR-TRACE-02), and
/// that invoice's trader is told by the withdrawal, which reads the invoices, not the QR codes.
/// </remarks>
public sealed class LotWithdrawalService(
    IHarvestLotRepository lots,
    ILotTraceQuery traces,
    IWithdrawalNoticeQuery history,
    WithdrawalNotices notices,
    TraderMessageService traderMessages,
    HarvestLotViews views,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string WithdrawnAction = "HarvestLot.Withdrawn";

    private const int MaxAttempts = 3;

    /// <summary>Step 1 with TRACE-04.2: 404, 409 MSG-GEN-09 for a lot that is not Published. Changes nothing.</summary>
    public async Task<Result<LotWithdrawalPreview>> PreviewAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        if (NotPublished(lot) is { } refused)
        {
            return refused;
        }

        var audience = WithdrawalAudience.Of(await traces.SaleLinesAsync(lot.Id, cancellationToken));
        var question = UserMessages.ConfirmAction(ConfirmActionOf(lot.LotCode));
        var notice = audience.NoInvoice ? UserMessages.NoTraderToNotify(lot.LotCode) : null;
        return new LotWithdrawalPreview(
            lot.Id, lot.LotCode, lot.OriginState, lot.PublishedAt, audience.Traders, audience.WalkInInvoiceCodes, question.Text, question.Code, notice?.Text, notice?.Code);
    }

    /// <summary>Steps 2-7: 404; 409 MSG-GEN-09 for a lot that is not Published (also after a parallel withdrawal); 400 MSG-GEN-01 for a missing reason (TRACE-04.0.E1).</summary>
    public async Task<Result<LotWithdrawn>> WithdrawAsync(Guid lotId, WithdrawLotRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var lot = await lots.GetForEditAsync(lotId, cancellationToken);
            if (lot is null)
            {
                return AppError.NotFound;
            }

            if (NotPublished(lot) is { } refused)
            {
                return refused;
            }

            var check = new FieldCheck();
            var reason = check.Required(request.Reason, HarvestLotFieldReader.ReasonField, HarvestLotFieldReader.MaxReasonLength);
            if (!check.IsValid)
            {
                return AppError.InvalidFields(check.Invalid);
            }

            // Step 1 again on current data: the traders notified are those of the invoices at the time of the save.
            var audience = WithdrawalAudience.Of(await traces.SaleLinesAsync(lot.Id, cancellationToken));
            var before = new { OriginState = lot.OriginState.ToString(), lot.PublishedAt, lot.WithdrawnAt };
            lot.WithdrawnAt = timeProvider.GetUtcNow();
            lot.WithdrawalReason = reason;
            var queued = await notices.QueueAsync(lot, audience.Traders, cancellationToken);
            Audit(lot, before, reason, audience, queued);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                unitOfWork.DiscardChanges();
                continue;
            }

            return await AnswerAsync(lot, audience, queued, cancellationToken);
        }
    }

    /// <summary>
    /// Step 7 read again: the notices of the latest withdrawal of the lot, from its audit row and the saved sendings, with their delivery status now. 404 for an unknown lot; a lot
    /// never withdrawn has none. Changes nothing.
    /// </summary>
    public async Task<Result<LotWithdrawalNotices>> NoticesAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        if (WithdrawalAudit.Read(await history.LatestWithdrawalAsync(lot.Id, cancellationToken)) is not { } latest)
        {
            return new LotWithdrawalNotices(lot.Id, lot.LotCode, lot.OriginState, null, [], []);
        }

        var sendings = (await history.SendingsAsync(lot.Id, latest.NotifiedTraders.Select(t => t.SendingId).ToList(), cancellationToken)).ToDictionary(s => s.Id);
        var now = timeProvider.GetUtcNow();
        var results = latest.NotifiedTraders.Select(t => WithdrawalAudit.NoticeOf(t, sendings.GetValueOrDefault(t.SendingId), statusCatalog, now)).ToList();
        return new LotWithdrawalNotices(lot.Id, lot.LotCode, lot.OriginState, latest.WithdrawnAt, results, latest.WalkInInvoiceCodes);
    }

    /// <summary>{action} of MSG-GEN-04 (TRACE-04 step 3).</summary>
    public static string ConfirmActionOf(string lotCode) => $"withdraw the origin information of harvest lot {lotCode}";

    // Step 5: the lot comes tracked with its raft. Each trader is kept with its sending, so that the notices can be read again (NoticesAsync).
    private void Audit(HarvestLot lot, object before, string reason, WithdrawalAudience audience, IReadOnlyList<QueuedWithdrawalNotice> queued) =>
        audit.Record(HarvestLotAudit.Entry(WithdrawnAction, lot, lot.Raft.ZoneId) with
        {
            Before = before,
            After = new WithdrawalAuditAfter(lot.OriginState.ToString(), lot.WithdrawnAt, queued.Select(WithdrawalAudit.TraderOf).ToList(), audience.WalkInInvoiceCodes),
            Reason = reason,
        });

    // Steps 6-7 after the commit: the sendings are handed to the service at the same time (the answer waits about one send), each keeps its own result.
    private async Task<LotWithdrawn> AnswerAsync(
        HarvestLot lot, WithdrawalAudience audience, IReadOnlyList<QueuedWithdrawalNotice> queued, CancellationToken cancellationToken)
    {
        await traderMessages.DeliverAllAsync(queued.Select(q => q.Sending).Where(s => s.DeliveryStatusId == StatusIds.MessageDelivery.Sending).ToList());
        var now = timeProvider.GetUtcNow();
        var results = queued.Select(q => WithdrawalAudit.NoticeOf(WithdrawalAudit.TraderOf(q), q.Sending, statusCatalog, now)).ToList();

        var saved = UserMessages.Saved("Harvest lot");
        var notice = audience.NoInvoice ? UserMessages.NoTraderToNotify(lot.LotCode) : null;
        return new LotWithdrawn(
            await views.ViewAsync(lot, lot.Raft, cancellationToken), results, audience.WalkInInvoiceCodes, saved.Text, saved.Code, notice?.Text, notice?.Code);
    }

    // PRE-2: only a Published lot is withdrawn (BR-TRACE-06); MSG-GEN-09 names its state.
    private static AppError? NotPublished(HarvestLot lot) => lot.OriginState == HarvestLotOriginState.Published
        ? null
        : AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", lot.OriginState == HarvestLotOriginState.Withdrawn ? "Withdrawn" : "Not published"));
}
