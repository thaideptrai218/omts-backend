using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>
/// CYCLE-04.0 record a cultivation log, for a Zone Manager of the raft's zone and the Cooperative Owner, while the cycle is Open. The photos are
/// stored first: if one cannot be uploaded nothing is saved and nobody is told (GFR-01). The cycle the save checks is read after the uploads, which can take
/// minutes on a phone at sea, so another log saved meanwhile refuses nothing (BR-CYCLE-05). The Cooperative Owners are notified once, in the same save,
/// except the Owner who recorded it (PM decision OMTS-216).
/// </summary>
public sealed class CultivationLogService(
    ICultivationCycleRepository cycles,
    ICultivationLogRepository logs,
    CycleWriteGuard writes,
    CultivationLogPhotoStore photoStore,
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

        if (CultivationLogValues.Read(
                request.VisitDate, request.Notes, photos, request.OtherFilePart, kept: 0, keysAreKnown: true, reason: null, reasonRequired: false,
                seen.StartDate, timeProvider.Today(), out var values) is { } invalid)
        {
            return invalid;
        }

        var recorder = currentUser.AccountId ?? throw new InvalidOperationException("A cultivation log needs an authenticated caller.");
        var uploaded = await photoStore.UploadAllAsync(values.NewPhotos, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var keys = uploaded.Value!;
        var prepared = await photoStore.PrepareSaveAsync(keys, () => PrepareAsync(cycleId, values, keys, recorder, cancellationToken));
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        await fileSaver.SaveAsync(keys, [], cancellationToken);

        var saved = UserMessages.Saved("Cultivation log");
        return new CultivationLogSaved(await views.ViewAsync(prepared.Value!, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>The cycle as it is after the uploads decides: closed or moved meanwhile refuses, and only a save in the same moment as this one can still conflict.</summary>
    private async Task<Result<CultivationLog>> PrepareAsync(
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

        var log = new CultivationLog
        {
            CycleId = cycle.Id,
            Cycle = cycle,
            VisitDate = values.VisitDate,
            Notes = values.Notes,
            RecordedByAccountId = recorder,
            Photos = keys.Select((key, index) => new CultivationLogPhoto { SortOrder = index + 1, FileKey = key }).ToList(),
        };
        logs.Add(log);

        // The cycle row is part of this save: a parallel cancel, close or new start date read the cycle before this log, so one of them fails with MSG06.
        unitOfWork.MarkChanged(cycle);
        audit.Record(CultivationLogAudit.Entry(CultivationLogAudit.Created, log) with { After = CultivationLogValues.Snapshot(log) });
        await NotifyOwnersAsync(log, recorder, cancellationToken);
        return log;
    }

    private async Task NotifyOwnersAsync(CultivationLog log, Guid recorder, CancellationToken cancellationToken)
    {
        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        var told = await ownerActorFilter.WithoutOwnerActorAsync(owners, recorder, cancellationToken);
        var raftCode = log.Cycle.Raft.Code;
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
