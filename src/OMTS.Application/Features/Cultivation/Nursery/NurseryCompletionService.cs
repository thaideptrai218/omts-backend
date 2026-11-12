using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
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
/// NURSERY-03 complete a Nursing batch, for a Zone Manager of the zone and the Cooperative Owner: the survivors (counted by eye), 1 to 3 photos and the shares of the survivors between rafts of the
/// zone (<see cref="NurseryAllocationApplier"/>); in one save the batch is Completed and every raft gets its cycle, starting on the completion date, with no expected harvest date. A refusal, a failed
/// upload or a lost race (a parallel start of a cycle on a raft, an edit of the batch) saves nothing and removes the uploads. A Zone Manager's completion notifies the Cooperative Owner.
/// </summary>
public sealed class NurseryCompletionService(
    INurseryBatchRepository nurseries,
    NurseryAllocationApplier applier,
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
        check.Check(!request.AllocationsUnreadable, NurseryValues.AllocationsField);
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
        var survivors = request.Survivors!.Value;
        if (await applier.ApplyAsync(batch, survivors, request.Allocations ?? [], completedOn, cancellationToken) is { } refused)
        {
            return refused;
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.NurseryCompletionPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var prepared = await photoStore.PrepareSaveAsync<NurseryBatch>(uploaded.Value!, async () =>
        {
            (batch.StatusId, batch.CompletedOn, batch.Survivors, batch.CompletedByAccountId) = (StatusIds.NurseryBatch.Completed, completedOn, survivors, actor);
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

        try
        {
            await fileSaver.SaveAsync(uploaded.Value!, [], cancellationToken);
        }
        catch (DuplicateRecordException exception)
        {
            // Two completions took the same raft at once: the filtered unique index of the Open cycle held, and the loser answers as when it changed the raft first (MSG06).
            throw new ConcurrencyConflictException(exception);
        }

        return await SavedAsync(batch, cancellationToken);
    }

    /// <summary>NURSERY-04 correct the survivors (old to new in the history), not below the seed hung. The shares follow: one raft takes the new number, several need the new shares.</summary>
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
        var seeded = (await nurseries.SeededByCycleAsync(batch.Allocations.Select(a => a.CycleId).ToList(), cancellationToken)).Values.Sum();
        if (survivors < seeded)
        {
            return NurseryRules.Refused(
                $"Đã thả {seeded:0.###} mảnh, số sống sót không thể nhỏ hơn ({survivors:0.###} mảnh).", NurseryReasonCodes.SurvivorsBelowSeeded,
                new Dictionary<string, object?> { ["field"] = NurseryValues.SurvivorsField, ["seeded"] = seeded });
        }

        var old = batch.Survivors ?? 0m;
        IReadOnlyList<NurseryAllocationInput>? inputs = request.Allocations;
        if (inputs is null && survivors == 0)
        {
            inputs = [];
        }
        else if (inputs is null && batch.Allocations.Count == 1)
        {
            inputs = [new NurseryAllocationInput(batch.Allocations.Single().RaftId, survivors)];
        }

        if (inputs is null && survivors == old)
        {
            return await SavedAsync(batch, cancellationToken);
        }

        return await ChangeAsync(batch, survivors, inputs, reason, survivors == old ? NurseryValues.AllocationsChanged : NurseryValues.SurvivorsCorrected, old, cancellationToken);
    }

    /// <summary>NURSERY-04 move the shares of the survivors between the rafts (one raft, or many), the same total.</summary>
    public async Task<Result<NurseryBatchSaved>> ChangeAllocationsAsync(Guid batchId, ChangeNurseryAllocationsRequest request, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(batchId, PermissionAction.Edit, StatusIds.NurseryBatch.Completed, request.RowVersion, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var batch = opened.Value!;
        var check = new FieldCheck();
        var reason = check.Required(request.Reason, NurseryValues.ReasonField, NurseryValues.MaxReasonLength);
        check.Check(request.Allocations is not null, NurseryValues.AllocationsField);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        return await ChangeAsync(batch, batch.Survivors ?? 0m, request.Allocations, reason, NurseryValues.AllocationsChanged, batch.Survivors ?? 0m, cancellationToken);
    }

    private async Task<Result<NurseryBatchSaved>> ChangeAsync(
        NurseryBatch batch, decimal survivors, IReadOnlyList<NurseryAllocationInput>? inputs, string reason, string action, decimal old, CancellationToken cancellationToken)
    {
        if (inputs is null)
        {
            return AppError.Invalid(NurseryValues.AllocationsField, "Chia lại số mảnh sống sót cho các bè (tổng phải bằng số sống sót mới).");
        }

        var before = NurseryValues.Snapshot(batch);
        if (await applier.ApplyAsync(batch, survivors, inputs, timeProvider.Today(), cancellationToken) is { } refused)
        {
            return refused;
        }

        batch.Survivors = survivors;
        unitOfWork.MarkChanged(batch);
        audit.Record(NurseryValues.Entry(action, batch) with { Before = before, After = NurseryValues.Snapshot(batch), Reason = reason });
        var actor = currentUser.AccountId ?? throw new InvalidOperationException("A nursery change needs an authenticated caller.");
        await (action == NurseryValues.SurvivorsCorrected ? notifier.SurvivorsCorrectedAsync(batch, old, reason, actor, cancellationToken) : notifier.AllocationsChangedAsync(batch, reason, actor, cancellationToken));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException exception)
        {
            throw new ConcurrencyConflictException(exception);
        }

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
