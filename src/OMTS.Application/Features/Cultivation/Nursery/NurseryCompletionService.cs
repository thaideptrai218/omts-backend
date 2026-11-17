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
/// NURSERY-03 (v2, 13/11/2026) complete a Nursing batch, for a Zone Manager of the zone and the Cooperative Owner: only the survivors (counted by eye), the completion date and 1 to 3 photos. No shares, no
/// cycle: the batch becomes a pool ("kho ươm", Completed = "đã ươm xong") that cycles chosen by hand seed from, piece by piece. NURSERY-04 corrects the survivors (not below what is seeded, old to
/// new in the history); NURSERY-05 closes the batch, writing the pieces left in the pool off as a loss estimate. A refusal or a failed upload saves nothing and removes the uploads. A Zone
/// Manager's completion, correction or close notifies the Cooperative Owner.
/// </summary>
public sealed class NurseryCompletionService(
    INurseryBatchRepository nurseries,
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
    public async Task<Result<NurseryBatchSaved>> CompleteAsync(Guid batchId, CompleteNurseryRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(batchId, PermissionAction.Create, StatusIds.NurseryBatch.Nursing, request.RowVersion, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var batch = opened.Value!;
        var today = timeProvider.Today();
        var completedOn = request.CompletedOn ?? today;
        var check = new FieldCheck();
        check.Check(NurseryRules.IsSurvivors(request.Survivors, batch.QuantityIn), NurseryValues.SurvivorsField);
        check.Check(completedOn >= batch.StartDate && completedOn <= today, NurseryValues.CompletedOnField);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        var files = StepPhotoService.Read(photos, otherFilePart: request.OtherFilePart);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        var actor = currentUser.AccountId ?? throw new InvalidOperationException("A nursery completion needs an authenticated caller.");
        var before = NurseryValues.Snapshot(batch);
        var uploaded = await photoStore.UploadAllAsync(FileCategories.NurseryCompletionPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var prepared = await photoStore.PrepareSaveAsync<NurseryBatch>(uploaded.Value!, async () =>
        {
            (batch.StatusId, batch.CompletedOn, batch.Survivors, batch.CompletedByAccountId) = (StatusIds.NurseryBatch.Completed, completedOn, request.Survivors!.Value, actor);
            NurseryPhotoRows.Add(nurseries, batch, NurseryPhotoPhase.Completion, uploaded.Value!);
            unitOfWork.MarkChanged(batch);
            audit.Record(NurseryValues.Entry(NurseryValues.Completed, batch) with { Before = before, After = NurseryValues.Snapshot(batch) });
            await notifier.CompletedAsync(batch, actor, cancellationToken);
            return batch;
        });
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        await fileSaver.SaveAsync(uploaded.Value!, [], cancellationToken);
        return await SavedAsync(batch, cancellationToken);
    }

    /// <summary>NURSERY-04 correct the survivors (old to new in the history), not below the seed already taken from the pool. Sending the same number changes nothing.</summary>
    public async Task<Result<NurseryBatchSaved>> CorrectSurvivorsAsync(Guid batchId, CorrectSurvivorsRequest request, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(batchId, PermissionAction.Edit, StatusIds.NurseryBatch.Completed, request.RowVersion, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var batch = opened.Value!;
        var check = new FieldCheck();
        var reason = check.Required(request.Reason, NurseryValues.ReasonField, NurseryValues.MaxReasonLength);
        check.Check(NurseryRules.IsSurvivors(request.Survivors, batch.QuantityIn), NurseryValues.SurvivorsField);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        var survivors = request.Survivors!.Value;
        var seeded = (await nurseries.SeededByBatchAsync([batch.Id], null, cancellationToken)).GetValueOrDefault(batch.Id);
        if (survivors < seeded)
        {
            return NurseryRules.Refused(
                $"Đã thả {seeded:0.###} mảnh từ đợt ươm này, số sống sót không thể nhỏ hơn ({survivors:0.###} mảnh).", NurseryReasonCodes.SurvivorsBelowSeeded,
                new Dictionary<string, object?> { ["field"] = NurseryValues.SurvivorsField, ["seeded"] = seeded });
        }

        var old = batch.Survivors ?? 0m;
        if (survivors == old)
        {
            return await SavedAsync(batch, cancellationToken);
        }

        var before = NurseryValues.Snapshot(batch);
        batch.Survivors = survivors;
        unitOfWork.MarkChanged(batch);
        audit.Record(NurseryValues.Entry(NurseryValues.SurvivorsCorrected, batch) with { Before = before, After = NurseryValues.Snapshot(batch), Reason = reason });
        var actor = currentUser.AccountId ?? throw new InvalidOperationException("A nursery change needs an authenticated caller.");
        await notifier.SurvivorsCorrectedAsync(batch, old, reason, actor, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(batch, cancellationToken);
    }

    /// <summary>NURSERY-05 close a Completed batch with pieces left in its pool: they are written off as a loss estimate, and nothing is seeded from it any more.</summary>
    public async Task<Result<NurseryBatchSaved>> CloseAsync(Guid batchId, CloseNurseryRequest request, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(batchId, PermissionAction.Edit, StatusIds.NurseryBatch.Completed, request.RowVersion, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var batch = opened.Value!;
        var check = new FieldCheck();
        var reason = check.Optional(request.Reason, NurseryValues.ReasonField, NurseryValues.MaxReasonLength);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        var seeded = (await nurseries.SeededByBatchAsync([batch.Id], null, cancellationToken)).GetValueOrDefault(batch.Id);
        var remaining = NurseryRules.Remaining(batch.Survivors ?? 0m, seeded);
        if (remaining <= 0m)
        {
            return NurseryRules.Refused(
                $"Đợt ươm {batch.Code} không còn mảnh nào trong kho ươm, không cần đóng.", NurseryReasonCodes.NothingToClose,
                new Dictionary<string, object?> { ["seeded"] = seeded, ["remaining"] = remaining });
        }

        var actor = currentUser.AccountId ?? throw new InvalidOperationException("A nursery close needs an authenticated caller.");
        var before = NurseryValues.Snapshot(batch);
        (batch.StatusId, batch.ClosedOn, batch.CloseLossPieces, batch.ClosedByAccountId, batch.CloseReason) = (StatusIds.NurseryBatch.Closed, timeProvider.Today(), remaining, actor, reason);
        unitOfWork.MarkChanged(batch);
        audit.Record(NurseryValues.Entry(NurseryValues.Closed, batch) with { Before = before, After = NurseryValues.Snapshot(batch), Reason = reason });
        await notifier.ClosedAsync(batch, reason, actor, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(batch, cancellationToken);
    }

    private async Task<NurseryBatchSaved> SavedAsync(NurseryBatch batch, CancellationToken cancellationToken)
    {
        var saved = UserMessages.Saved("Nursery batch");
        return new NurseryBatchSaved(await views.ViewAsync(batch, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>The tracked batch after the checks every command starts from: 404, 403, 409 when it is not in the status the command needs or its zone is not In Use, then the version (NFR-09).</summary>
    private async Task<Result<NurseryBatch>> OpenAsync(Guid batchId, PermissionAction action, int statusId, string? rowVersion, CancellationToken cancellationToken)
    {
        var batch = await nurseries.GetForEditAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, action, batch.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (batch.StatusId != statusId)
        {
            return statusId == StatusIds.NurseryBatch.Nursing
                ? NurseryRules.NotNursing(batch.Code, NurseryValues.StatusLabel(batch.StatusId))
                : NurseryRules.NotCompleted(batch.Code, NurseryValues.StatusLabel(batch.StatusId));
        }

        if (batch.Zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.NotUsableChoice($"Farming zone {batch.Zone.Code}", "Discontinued", NurseryValues.ZoneField);
        }

        return await rowVersions.CheckAsync(batch, rowVersion, cancellationToken) is { } conflict ? conflict : batch;
    }
}
