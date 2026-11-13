using System.Text.Json.Nodes;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>
/// NURSERY-02 edit and cancel a nursery batch, for a Zone Manager of the zone and the Cooperative Owner, ONLY while it is Nursing (decision of 12/11/2026: a Completed batch is not cancelled). An edit
/// changes the pieces, the start date, the note or the start photos (still 1 to 3); the stock issue follows the pieces and the date, and a rise is checked against what is on hand (the pieces the
/// batch already holds may be kept). A cancel needs a reason, returns the pieces to the lot and Cancels the issue. A Zone Manager's change notifies the Cooperative Owner. The batch row and the
/// receipt row of the lot are part of the save: of a parallel edit, cancel, completion or receipt change one answers MSG06.
/// </summary>
public sealed class NurseryBatchEditService(
    INurseryBatchRepository nurseries,
    NurseryStockSync stock,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    NurseryBatchViews views,
    NurseryNotifier notifier,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<Result<NurseryBatchSaved>> EditAsync(Guid batchId, EditNurseryRequest request, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(batchId, request.RowVersion, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var batch = opened.Value!;
        var lot = batch.SeedLotLine;
        var check = new FieldCheck();
        var today = timeProvider.Today();
        var quantity = request.Quantity ?? batch.QuantityIn;
        var start = request.StartDate ?? batch.StartDate;
        check.Check(NurseryRules.IsPieces(quantity), NurseryValues.QuantityField);
        check.Check(start <= today && start >= lot.Receipt.ReceiptDate, NurseryValues.StartDateField);
        var note = request.Note is null ? batch.Note : check.Optional(request.Note, NurseryValues.NoteField, NurseryBatch.MaxNoteLength);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        var keep = (request.KeepPhotoIds ?? []).ToHashSet();
        var kept = request.ReplacePhotos ? batch.Photos.Where(p => p.Phase == NurseryPhotoPhase.Start && keep.Contains(p.Id)).ToList() : batch.Photos.Where(p => p.Phase == NurseryPhotoPhase.Start).ToList();
        var files = StepPhotoService.Read(request.ReplacePhotos ? newPhotos : [], kept.Count, request.OtherFilePart, kept.Count == keep.Count || !request.ReplacePhotos);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        if (quantity > batch.QuantityIn && await stock.CheckOnHandAsync(lot, quantity, batch.QuantityIn, cancellationToken) is { } notOnHand)
        {
            return notOnHand;
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.NurseryStartPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var removed = new List<string>();
        var applied = await photoStore.PrepareSaveAsync<NurseryBatch>(uploaded.Value!, async () =>
        {
            var before = NurseryValues.Snapshot(batch);
            var stockChanged = quantity != batch.QuantityIn || start != batch.StartDate;
            (batch.QuantityIn, batch.StartDate, batch.Note) = (quantity, start, note);
            if (request.ReplacePhotos)
            {
                removed.AddRange(NurseryPhotoRows.Replace(nurseries, batch, NurseryPhotoPhase.Start, kept, uploaded.Value!));
            }

            if (stockChanged && await stock.ReplaceAsync(batch, lot, cancellationToken) is { } stockRefused)
            {
                return stockRefused;
            }

            var (changedBefore, changedAfter) = AuditDiff.Changed(before, NurseryValues.Snapshot(batch));
            if (changedAfter is not null || request.ReplacePhotos)
            {
                unitOfWork.MarkChanged(batch);
                unitOfWork.MarkChanged(lot.Receipt);
                audit.Record(NurseryValues.Entry(NurseryValues.Edited, batch) with { Before = changedBefore, After = changedAfter ?? new JsonObject { ["Photos"] = "Đã đổi ảnh" } });
                await notifier.EditedAsync(batch, Actor, cancellationToken);
            }

            return batch;
        });
        if (!applied.IsSuccess)
        {
            return applied.Error!;
        }

        await fileSaver.SaveAsync(uploaded.Value!, removed, cancellationToken);
        var saved = UserMessages.Saved("Nursery batch");
        return new NurseryBatchSaved(await views.ViewAsync(batch, cancellationToken), saved.Text, saved.Code);
    }

    public async Task<Result<NurseryBatchSaved>> CancelAsync(Guid batchId, CancelNurseryRequest request, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(batchId, request.RowVersion, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var batch = opened.Value!;
        var check = new FieldCheck();
        var reason = check.Required(request.Reason, NurseryValues.ReasonField, NurseryValues.MaxReasonLength);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        await stock.CancelAsync(batch, reason, cancellationToken);
        (batch.StatusId, batch.CancelledAt, batch.CancelReason) = (StatusIds.NurseryBatch.Cancelled, timeProvider.GetUtcNow(), reason);
        unitOfWork.MarkChanged(batch);
        unitOfWork.MarkChanged(batch.SeedLotLine.Receipt);
        audit.Record(NurseryValues.Entry(NurseryValues.Cancelled, batch) with
        {
            Before = new { Status = NurseryValues.StatusLabel(StatusIds.NurseryBatch.Nursing) }, After = new { Status = NurseryValues.StatusLabel(StatusIds.NurseryBatch.Cancelled) }, Reason = reason,
        });
        await notifier.CancelledAsync(batch, reason, Actor, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved("Nursery batch");
        return new NurseryBatchSaved(await views.ViewAsync(batch, cancellationToken), saved.Text, saved.Code);
    }

    private Guid Actor => currentUser.AccountId ?? throw new InvalidOperationException("A nursery change needs an authenticated caller.");

    /// <summary>The tracked batch, after the checks both commands start from: 404, 403, 409 when it is not Nursing or its zone is not In Use, then the version the client read (NFR-09).</summary>
    private async Task<Result<NurseryBatch>> OpenAsync(Guid batchId, string? rowVersion, CancellationToken cancellationToken)
    {
        var batch = await nurseries.GetForEditAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, batch.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (batch.StatusId != StatusIds.NurseryBatch.Nursing)
        {
            return NurseryRules.NotNursing(batch.Code, NurseryValues.StatusLabel(batch.StatusId));
        }

        if (batch.Zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.NotUsableChoice($"Farming zone {batch.Zone.Code}", "Discontinued", NurseryValues.ZoneField);
        }

        return await rowVersions.CheckAsync(batch, rowVersion, cancellationToken) is { } conflict ? conflict : batch;
    }
}
