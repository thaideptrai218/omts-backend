using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>The log answers, with signed photo links and the commands the caller has. Every caller has already passed the zone check on the record.</summary>
public sealed class CultivationLogViews(
    ICultivationLogRepository logs,
    IAccountRepository accounts,
    IFileStorage storage,
    PermissionService permissions,
    ICurrentUser currentUser)
{
    /// <summary>The logs of the cycle, the latest visit first, only of <paramref name="logType"/> when given (a code already normalized). The cycle comes with its raft and zone.</summary>
    public async Task<IReadOnlyList<CultivationLogView>> ListAsync(CultivationCycle cycle, string? logType, CancellationToken cancellationToken) =>
        await ViewsAsync(await logs.ListByCycleAsync(cycle.Id, logType, cancellationToken), _ => cycle, cycle.Raft, cancellationToken);

    /// <summary>
    /// The logs of the raft in every cycle it had and in none, the latest visit first, only of <paramref name="logType"/> when given (a code already normalized).
    /// The raft comes with its zone; each log comes with its cycle.
    /// </summary>
    public async Task<IReadOnlyList<CultivationLogView>> ListAsync(Raft raft, string? logType, CancellationToken cancellationToken) =>
        await ViewsAsync(await logs.ListByRaftAsync(raft.Id, logType, cancellationToken), l => l.Cycle, raft, cancellationToken);

    /// <summary>The logs of a page, each with its raft, zone and cycle: the names of the recorders are read once for the page.</summary>
    public async Task<IReadOnlyList<CultivationLogView>> ListAsync(IReadOnlyList<CultivationLog> page, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync(page.Select(l => l.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        var access = await AccessAsync(cancellationToken);
        var views = new List<CultivationLogView>(page.Count);
        foreach (var log in page)
        {
            views.Add(await ViewAsync(log, log.Raft, log.Cycle, names.GetValueOrDefault(log.RecordedByAccountId), access, cancellationToken));
        }

        return views;
    }

    /// <summary>One log, with its raft, zone and cycle.</summary>
    public async Task<CultivationLogView> ViewAsync(CultivationLog log, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync([log.RecordedByAccountId], cancellationToken);
        return await ViewAsync(log, log.Raft, log.Cycle, names.GetValueOrDefault(log.RecordedByAccountId), await AccessAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// BR-CYCLE-05: the Cooperative Owner corrects any log, a Zone Manager of the zone only a log they recorded. Whether the cycle and the raft still
    /// allow it is <see cref="CultivationLogWriteGuard"/>'s question.
    /// </summary>
    public static bool MayCorrect(AccountAccess? access, CultivationLog log, Guid zoneId) =>
        access is not null
        && access.Allows(PermissionFunctions.CultivationLogs, PermissionAction.Edit, PermissionScope.ZoneScoped, zoneId)
        && (access.IsCooperativeOwner || log.RecordedByAccountId == access.AccountId);

    public async Task<AccountAccess?> AccessAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId ? await permissions.GetAccessAsync(accountId, cancellationToken) : null;

    private async Task<IReadOnlyList<CultivationLogView>> ViewsAsync(
        IReadOnlyList<CultivationLog> rows, Func<CultivationLog, CultivationCycle?> cycleOf, Raft raft, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync(rows.Select(l => l.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        var access = await AccessAsync(cancellationToken);
        var views = new List<CultivationLogView>(rows.Count);
        foreach (var log in rows)
        {
            views.Add(await ViewAsync(log, raft, cycleOf(log), names.GetValueOrDefault(log.RecordedByAccountId), access, cancellationToken));
        }

        return views;
    }

    private async Task<CultivationLogView> ViewAsync(
        CultivationLog log, Raft raft, CultivationCycle? cycle, string? recordedBy, AccountAccess? access, CancellationToken cancellationToken)
    {
        var photos = new List<CultivationLogPhotoView>(log.Photos.Count);
        foreach (var photo in log.Photos.OrderBy(p => p.SortOrder))
        {
            photos.Add(new CultivationLogPhotoView(photo.FileKey, photo.SortOrder, await storage.GetReadUrlAsync(photo.FileKey, cancellationToken: cancellationToken)));
        }

        return new CultivationLogView(
            log.Id,
            cycle?.Id,
            cycle?.StartDate,
            raft.Id,
            raft.Code,
            raft.ZoneId,
            raft.Zone.Code,
            raft.Zone.Name,
            log.VisitDate,
            log.LogType,
            log.Notes,
            photos,
            log.RecordedByAccountId,
            recordedBy,
            log.CreatedAt,
            (cycle is null || cycle.StatusId == StatusIds.CultivationCycle.Open) && RaftViews.AcceptsCareRecords(raft) && MayCorrect(access, log, raft.ZoneId),
            RowVersionGuard.Encode(log.RowVersion));
    }
}
