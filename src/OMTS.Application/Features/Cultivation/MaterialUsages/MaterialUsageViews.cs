using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>The usage answers, with the cycle worked out from each date (BR-CYCLE-03) and the commands the caller has. Every caller has already passed the zone check on the raft.</summary>
public sealed class MaterialUsageViews(
    IMaterialUsageRepository usages,
    ICultivationCycleRepository cycles,
    IAccountRepository accounts,
    CultivationCycleViews cycleViews,
    ZoneAccessGuard zoneAccess)
{
    /// <summary>SCR-CYCLE-03: by cycle, the latest first and the dates with no cycle last, then by repair (date and reason). The raft comes with its zone.</summary>
    public async Task<MaterialUsageList> ListAsync(Raft raft, CancellationToken cancellationToken)
    {
        var records = await usages.ListByRaftAsync(raft.Id, cancellationToken);
        var ofRaft = await cycles.ListByRaftAsync(raft.Id, cancellationToken);
        var views = await ToViewsAsync(raft, records, ofRaft, cancellationToken);
        var groups = records.Zip(views)
            .GroupBy(r => CycleOn(ofRaft, r.First.UsageDate))
            .OrderBy(g => g.Key is null)
            .ThenByDescending(g => g.Key?.StartDate)
            .Select(g => new MaterialUsageGroup(
                g.Key is { } cycle ? cycleViews.ListItem(cycle) : null,
                g.Key?.CloseDate is not null,
                g.GroupBy(r => (r.First.UsageDate, r.First.RepairReason))
                    .Select(repair => new MaterialUsageRepair(repair.Key.UsageDate, repair.Key.RepairReason, repair.Select(r => r.Second).ToList()))
                    .ToList()))
            .ToList();
        var canRecord = IsInUse(raft) && await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, raft.ZoneId, cancellationToken);
        return new MaterialUsageList(raft.Id, raft.Code, canRecord, groups);
    }

    /// <summary>These records of the raft, in the order of the list.</summary>
    public async Task<IReadOnlyList<MaterialUsageView>> RecordsAsync(Raft raft, IReadOnlySet<Guid> usageIds, CancellationToken cancellationToken)
    {
        var records = (await usages.ListByRaftAsync(raft.Id, cancellationToken)).Where(u => usageIds.Contains(u.Id)).ToList();
        return await ToViewsAsync(raft, records, await cycles.ListByRaftAsync(raft.Id, cancellationToken), cancellationToken);
    }

    /// <summary>One record that exists.</summary>
    public async Task<MaterialUsageView> RecordAsync(Guid usageId, CancellationToken cancellationToken)
    {
        var usage = await usages.GetAsync(usageId, cancellationToken) ?? throw new InvalidOperationException("The material usage record was just read.");
        return (await ToViewsAsync(usage.Raft, [usage], await cycles.ListByRaftAsync(usage.RaftId, cancellationToken), cancellationToken))[0];
    }

    private async Task<IReadOnlyList<MaterialUsageView>> ToViewsAsync(
        Raft raft, IReadOnlyList<MaterialUsage> records, IReadOnlyList<CultivationCycle> ofRaft, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync(records.Select(u => u.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        var mayEdit = IsInUse(raft) && await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, raft.ZoneId, cancellationToken);
        return records.Select(u =>
        {
            var cycle = CycleOn(ofRaft, u.UsageDate);
            return new MaterialUsageView(
                u.Id, raft.Id, raft.Code, cycle?.Id, u.UsageDate, u.RepairReason, u.ItemId, u.Item.Code, u.Item.Name, u.Item.Unit, u.Quantity,
                u.RecordedByAccountId, names.GetValueOrDefault(u.RecordedByAccountId), u.CreatedAt,
                mayEdit && cycle?.CloseDate is null,
                RowVersionGuard.Encode(u.RowVersion));
        }).ToList();
    }

    private static CultivationCycle? CycleOn(IReadOnlyList<CultivationCycle> ofRaft, DateOnly date) =>
        ofRaft.FirstOrDefault(c => new CycleSpan(c.Id, c.RaftId, c.StartDate, c.CloseDate).IsOpenOn(date));

    private static bool IsInUse(Raft raft) => raft.StatusId == StatusIds.Usage.InUse && raft.Zone.StatusId == StatusIds.Usage.InUse;
}
