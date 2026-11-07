using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>The log answers, with signed photo links and the commands the caller has. Every caller has already passed the zone check on the record.</summary>
public sealed class CultivationLogViews(
    ICultivationLogRepository logs,
    IAccountRepository accounts,
    IFileStorage storage,
    PermissionService permissions,
    ICurrentUser currentUser)
{
    /// <summary>The logs of the cycle, the latest visit first. The cycle comes with its raft and zone.</summary>
    public async Task<IReadOnlyList<CultivationLogView>> ListAsync(CultivationCycle cycle, CancellationToken cancellationToken)
    {
        var ofCycle = await logs.ListByCycleAsync(cycle.Id, cancellationToken);
        var names = await accounts.GetDisplayNamesAsync(ofCycle.Select(l => l.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        var access = await AccessAsync(cancellationToken);
        var views = new List<CultivationLogView>(ofCycle.Count);
        foreach (var log in ofCycle)
        {
            views.Add(await ViewAsync(log, cycle, names.GetValueOrDefault(log.RecordedByAccountId), access, cancellationToken));
        }

        return views;
    }

    /// <summary>One log, with its cycle, raft and zone.</summary>
    public async Task<CultivationLogView> ViewAsync(CultivationLog log, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync([log.RecordedByAccountId], cancellationToken);
        return await ViewAsync(log, log.Cycle, names.GetValueOrDefault(log.RecordedByAccountId), await AccessAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// BR-CYCLE-05: the Cooperative Owner corrects any log, a Zone Manager of the zone only a log they recorded. Whether the cycle still
    /// allows it is <see cref="CycleWriteGuard"/>'s question.
    /// </summary>
    public static bool MayCorrect(AccountAccess? access, CultivationLog log, Guid zoneId) =>
        access is not null
        && access.Allows(PermissionFunctions.CultivationLogs, PermissionAction.Edit, PermissionScope.ZoneScoped, zoneId)
        && (access.IsCooperativeOwner || log.RecordedByAccountId == access.AccountId);

    public async Task<AccountAccess?> AccessAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId ? await permissions.GetAccessAsync(accountId, cancellationToken) : null;

    private async Task<CultivationLogView> ViewAsync(
        CultivationLog log, CultivationCycle cycle, string? recordedBy, AccountAccess? access, CancellationToken cancellationToken)
    {
        var photos = new List<CultivationLogPhotoView>(log.Photos.Count);
        foreach (var photo in log.Photos.OrderBy(p => p.SortOrder))
        {
            photos.Add(new CultivationLogPhotoView(photo.FileKey, photo.SortOrder, await storage.GetReadUrlAsync(photo.FileKey, cancellationToken: cancellationToken)));
        }

        var raft = cycle.Raft;
        var zoneInUse = raft.Zone.StatusId == StatusIds.Usage.InUse;
        return new CultivationLogView(
            log.Id,
            cycle.Id,
            raft.Id,
            raft.Code,
            log.VisitDate,
            log.Notes,
            photos,
            log.RecordedByAccountId,
            recordedBy,
            log.CreatedAt,
            CultivationCycleViews.CanEdit(cycle, raft, zoneInUse) && MayCorrect(access, log, raft.ZoneId),
            RowVersionGuard.Encode(log.RowVersion));
    }
}
