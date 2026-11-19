using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>
/// Owner rule of 18/11/2026 (NUR-22): a batch whose pool a seeding record has just used up closes by itself, with nothing written off (loss 0), and no manual "Đóng đợt ươm" is needed. When a later
/// correction or removal of a record puts pieces back, a batch that closed by itself (Closed with a loss of 0; a manual close always writes off more than 0) opens again as Completed. A batch closed by hand
/// stays closed. Called by the seeding commands before they save, so the status moves in the same transaction as the record. No notice: the Owner is told about manual closes only; the audit keeps both.
/// </summary>
public sealed class NurseryPoolAutoClose(INurseryBatchRepository nurseries, AuditLogService audit, IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider)
{
    public const string AutoCloseReason = "tự đóng khi thả hết";
    public const string ReopenReason = "mở lại do kho ươm có mảnh trở lại";

    /// <param name="batchId">The batch the seeding record draws from.</param>
    /// <param name="recordId">The record being added, corrected or removed: its saved quantity is set aside.</param>
    /// <param name="newQuantity">What the record will hold once saved; null when it is removed.</param>
    public async Task SyncAsync(Guid batchId, Guid recordId, decimal? newQuantity, CancellationToken cancellationToken)
    {
        var batch = await nurseries.GetForPoolAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return;
        }

        var seededByOthers = (await nurseries.SeededByBatchAsync([batch.Id], recordId, cancellationToken)).GetValueOrDefault(batch.Id);
        var seeded = seededByOthers + (newQuantity ?? 0m);
        var remaining = NurseryRules.Remaining(batch.Survivors ?? 0m, seeded);
        var actor = currentUser.AccountId;
        if (actor is null)
        {
            return;
        }

        if (batch.StatusId == StatusIds.NurseryBatch.Completed && remaining <= 0m && seeded > 0m)
        {
            var before = NurseryValues.Snapshot(batch);
            (batch.StatusId, batch.ClosedOn, batch.CloseLossPieces, batch.ClosedByAccountId, batch.CloseReason) = (StatusIds.NurseryBatch.Closed, timeProvider.Today(), 0m, actor, AutoCloseReason);
            unitOfWork.MarkChanged(batch);
            audit.Record(NurseryValues.Entry(NurseryValues.Closed, batch) with { Before = before, After = NurseryValues.Snapshot(batch), Reason = AutoCloseReason });
        }
        else if (batch.StatusId == StatusIds.NurseryBatch.Closed && (batch.CloseLossPieces ?? 0m) == 0m && remaining > 0m)
        {
            var before = NurseryValues.Snapshot(batch);
            (batch.StatusId, batch.ClosedOn, batch.CloseLossPieces, batch.ClosedByAccountId, batch.CloseReason) = (StatusIds.NurseryBatch.Completed, null, null, null, null);
            unitOfWork.MarkChanged(batch);
            audit.Record(NurseryValues.Entry(NurseryValues.Reopened, batch) with { Before = before, After = NurseryValues.Snapshot(batch), Reason = ReopenReason });
        }
    }
}
