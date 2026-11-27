using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>
/// CYCLE-04.0 record a cultivation log, for a Zone Manager of the raft's zone and the Cooperative Owner: in an Open cycle, or on a raft whatever its cycles (the cycle is then worked
/// out from the visit date, as for material usage). A raft with no cycle or one that is Discontinued still gets logs, so the repair work on it is kept (BR-CYCLE-05). The photos are
/// stored first: if one cannot be uploaded nothing is saved and nobody is told (GFR-01). The cycle or raft the save checks is read after the uploads, which can take
/// minutes on a phone at sea, so another log saved meanwhile refuses nothing (BR-CYCLE-05). The Cooperative Owners are notified once, in the same save,
/// except the Owner who recorded it (PM decision OMTS-216).
/// </summary>
public sealed class CultivationLogService(
    ICultivationCycleRepository cycles,
    IRaftRepository rafts,
    ICultivationLogRepository logs,
    CultivationLogWriteGuard writes,
    CultivationLogCycleResolver resolver,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    CultivationLogViews views,
    ZoneAccessGuard zoneAccess,
    IZoneRecipientQuery recipients,
    OwnerActorRecipientFilter ownerActorFilter,
    INotificationDispatcher notifications,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const string NotificationTitle = "New cultivation log";

    /// <summary>Records a visit in the cycle, which must be Open: the visit is from its start date to today.</summary>
    public async Task<Result<CultivationLogSaved>> RecordAsync(
        Guid cycleId, RecordCultivationLogRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken)
    {
        // A first look, not tracked: a request that will be refused stores nothing.
        var seen = await cycles.GetAsync(cycleId, cancellationToken);
        if (seen is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationLogs, PermissionAction.Create, seen.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (writes.Refusal(seen) is { } refused)
        {
            return refused;
        }

        return await SaveAsync(request, photos, seen.StartDate, (values, keys, recorder, token) => PrepareInCycleAsync(cycleId, values, keys, recorder, token), cancellationToken);
    }

    /// <summary>Records a visit to the raft: the cycle is the Open cycle the visit date falls in, none when it falls in no cycle, and a date inside a Closed cycle is refused (409).</summary>
    public async Task<Result<CultivationLogSaved>> RecordOnRaftAsync(
        Guid raftId, RecordCultivationLogRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken)
    {
        var seen = await rafts.GetForReadAsync(raftId, cancellationToken);
        if (seen is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationLogs, PermissionAction.Create, seen.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (writes.Refusal(seen) is { } refused)
        {
            return refused;
        }

        return await SaveAsync(request, photos, earliest: null, (values, keys, recorder, token) => PrepareOnRaftAsync(raftId, values, keys, recorder, token), cancellationToken);
    }

    private async Task<Result<CultivationLogSaved>> SaveAsync(
        RecordCultivationLogRequest request,
        IReadOnlyList<IncomingFile> photos,
        DateOnly? earliest,
        Func<CultivationLogValues, IReadOnlyList<string>, Guid, CancellationToken, Task<Result<CultivationLog>>> prepare,
        CancellationToken cancellationToken)
    {
        if (CultivationLogValues.Read(
                request.VisitDate, request.LogType, request.Notes, photos, request.OtherFilePart, kept: 0, keysAreKnown: true, reason: null, reasonRequired: false,
                earliest, timeProvider.Today(), out var values) is { } invalid)
        {
            return invalid;
        }

        var recorder = currentUser.AccountId ?? throw new InvalidOperationException("A cultivation log needs an authenticated caller.");
        var uploaded = await photoStore.UploadAllAsync(FileCategories.CultivationLogPhotos, values.NewPhotos, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var keys = uploaded.Value!;
        var prepared = await photoStore.PrepareSaveAsync(keys, () => prepare(values, keys, recorder, cancellationToken));
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        await fileSaver.SaveAsync(keys, [], cancellationToken);

        var saved = UserMessages.Saved("Cultivation log");
        return new CultivationLogSaved(await views.ViewAsync(prepared.Value!, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>The cycle as it is after the uploads decides: closed or moved meanwhile refuses, and only a save in the same moment as this one can still conflict.</summary>
    private async Task<Result<CultivationLog>> PrepareInCycleAsync(
        Guid cycleId, CultivationLogValues values, IReadOnlyList<string> keys, Guid recorder, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetForEditAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        if ((writes.Refusal(cycle) ?? CultivationLogValues.ReadAgain(values.VisitDate, cycle.StartDate)) is { } refused)
        {
            return refused;
        }

        // The cycle row is part of this save: a parallel cancel, close or new start date read the cycle before this log, so one of them fails with MSG06.
        unitOfWork.MarkChanged(cycle);
        return await AddAsync(cycle.Raft, cycle, values, keys, recorder, cancellationToken);
    }

    /// <summary>The raft as it is after the uploads decides: its status and the cycle of the date, whose row is part of the save, so a parallel close, cancel or discontinue fails with MSG06 or this save does.</summary>
    private async Task<Result<CultivationLog>> PrepareOnRaftAsync(
        Guid raftId, CultivationLogValues values, IReadOnlyList<string> keys, Guid recorder, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (writes.Refusal(raft) is { } refused)
        {
            return refused;
        }

        var resolved = await resolver.ResolveAsync(raft, values.VisitDate, cancellationToken);
        return resolved.Refusal is { } closed ? closed : await AddAsync(raft, resolved.Cycle, values, keys, recorder, cancellationToken);
    }

    private async Task<CultivationLog> AddAsync(
        Raft raft, CultivationCycle? cycle, CultivationLogValues values, IReadOnlyList<string> keys, Guid recorder, CancellationToken cancellationToken)
    {
        var log = new CultivationLog
        {
            RaftId = raft.Id,
            Raft = raft,
            CycleId = cycle?.Id,
            Cycle = cycle,
            VisitDate = values.VisitDate,
            LogType = values.LogType ?? CultivationLogTypes.Other,
            Notes = values.Notes,
            RecordedByAccountId = recorder,
            Photos = keys.Select((key, index) => new CultivationLogPhoto { SortOrder = index + 1, FileKey = key }).ToList(),
        };
        logs.Add(log);

        audit.Record(CultivationLogAudit.Entry(CultivationLogAudit.Created, log) with { After = CultivationLogValues.Snapshot(log) });
        await NotifyOwnersAsync(log, recorder, cancellationToken);
        return log;
    }

    private async Task NotifyOwnersAsync(CultivationLog log, Guid recorder, CancellationToken cancellationToken)
    {
        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        var told = await ownerActorFilter.WithoutOwnerActorAsync(owners, recorder, cancellationToken);
        var raftCode = log.Raft.Code;
        await notifications.NotifyAsync(
            told,
            new NotificationContent(
                NotificationTypes.CultivationLogRecorded,
                NotificationTitle,
                UserMessages.CultivationLogRecorded(raftCode, log.VisitDate),
                new RelatedRecord(RelatedRecordTypes.CultivationLog, log.Id, raftCode)),
            cancellationToken);
    }
}
