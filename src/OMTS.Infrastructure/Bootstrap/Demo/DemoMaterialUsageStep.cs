using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Cultivation.MaterialUsages;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// CYCLE-03: repairs on the first two rafts of each In Use demo zone, inside their Open cycles, and one on the sixth raft, which has no cycle, so the screen shows both kinds of
/// group; recorded by the Zone Manager of the zone. Items are found by their group, not by code. Matched by the audit log, as a record has no code: a rerun adds nothing. A raft
/// that has changed on an older database (Discontinued, no Open cycle, a cycle too young or a cycle on the date of the repair with no cycle) is passed over. Runs after the receipt step: a repair issues from the stock the receipts brought in.
/// </summary>
internal sealed class DemoMaterialUsageStep : IDemoSeedStep
{
    private const int RaftWithoutCycle = 6;
    private const int ItemsNeeded = 2;

    private static readonly (string ZoneCode, string Manager)[] Zones =
    [
        (DemoFarmingZoneStep.ZoneOne, "quanlyvung1"),
        (DemoFarmingZoneStep.ZoneTwo, "quanlyvung2"),
    ];

    public string Name => "material usage";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var items = await context.Db.FarmingInputItems.AsNoTracking()
            .Where(i => i.Group == MaterialUsage.ItemGroup && i.StatusId == StatusIds.Usage.InUse)
            .OrderBy(i => i.Code)
            .Select(i => i.Id)
            .Take(ItemsNeeded)
            .ToListAsync(cancellationToken);
        if (items.Count < ItemsNeeded)
        {
            return 0;
        }

        var created = 0;
        foreach (var (zoneCode, manager) in Zones)
        {
            var zoneId = await context.Db.FarmingZones.Where(z => z.Code == zoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
            var actor = await DemoCultivationLogStep.ActorAsync(context, PermissionFunctions.CultivationCycles, manager, zoneId, cancellationToken);
            using var acting = context.Get<AmbientActor>().Use(actor);
            for (var number = 1; number <= 2; number++)
            {
                created += await RecordInCycleAsync(context, zoneId, DemoRaftStep.RaftCode(zoneCode, number), items, cancellationToken);
            }

            created += await RecordWithoutCycleAsync(context, zoneId, DemoRaftStep.RaftCode(zoneCode, RaftWithoutCycle), items[0], cancellationToken);
        }

        return created;
    }

    /// <summary>Two repairs of the Open cycle: one with both items, one with the first.</summary>
    private static async Task<int> RecordInCycleAsync(DemoSeedContext context, Guid zoneId, string raftCode, IReadOnlyList<Guid> items, CancellationToken cancellationToken)
    {
        var cycle = await context.Db.CultivationCycles.AsNoTracking()
            .Where(c => c.Raft.ZoneId == zoneId && c.Raft.Code == raftCode && c.StatusId == StatusIds.CultivationCycle.Open
                && c.Raft.StatusId == StatusIds.Usage.InUse && c.Raft.Zone.StatusId == StatusIds.Usage.InUse)
            .Select(c => new { c.RaftId, c.StartDate })
            .FirstOrDefaultAsync(cancellationToken);
        if (cycle is null || cycle.StartDate.AddDays(20) > context.Today || !await IsFreeAsync(context, zoneId, raftCode, cancellationToken))
        {
            return 0;
        }

        var service = context.Get<MaterialUsageService>();
        var first = new RecordMaterialUsageRequest(cycle.StartDate.AddDays(10), "Phao bị vỡ do sóng lớn", [new(items[0], 4m), new(items[1], 1.5m)]);
        var second = new RecordMaterialUsageRequest(cycle.StartDate.AddDays(20), "Dây neo bị đứt", [new(items[0], 2m)]);
        DemoSeedContext.Ensure(await service.RecordAsync(cycle.RaftId, first, cancellationToken), $"first repair of raft {raftCode}");
        DemoSeedContext.Ensure(await service.RecordAsync(cycle.RaftId, second, cancellationToken), $"second repair of raft {raftCode}");
        return 3;
    }

    /// <summary>A repair while the raft has no cycle: the separate group of BR-CYCLE-03.</summary>
    private static async Task<int> RecordWithoutCycleAsync(DemoSeedContext context, Guid zoneId, string raftCode, Guid item, CancellationToken cancellationToken)
    {
        var raftId = await context.Db.Rafts.Where(r => r.ZoneId == zoneId && r.Code == raftCode && r.StatusId == StatusIds.Usage.InUse).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
        var date = context.Today.AddDays(-7);
        if (raftId is not { } id
            || !await IsFreeAsync(context, zoneId, raftCode, cancellationToken)
            || await context.Get<ICultivationCycleQuery>().CycleOnDateAsync(id, date, cancellationToken) is not null)
        {
            return 0;
        }

        var request = new RecordMaterialUsageRequest(date, "Thay phao cũ trước khi vào vụ", [new(item, 3m)]);
        DemoSeedContext.Ensure(await context.Get<MaterialUsageService>().RecordAsync(id, request, cancellationToken), $"repair of raft {raftCode}");
        return 1;
    }

    private static async Task<bool> IsFreeAsync(DemoSeedContext context, Guid zoneId, string raftCode, CancellationToken cancellationToken) =>
        !await context.Db.AuditEvents.AnyAsync(e => e.Action == "MaterialUsage.Created" && e.ZoneId == zoneId && e.RecordCode == raftCode, cancellationToken);
}
