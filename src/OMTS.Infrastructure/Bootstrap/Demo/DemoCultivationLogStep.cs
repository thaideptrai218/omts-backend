using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// CYCLE-04: monthly visits on the Open cycles of the first three rafts of each In Use demo zone, recorded by the Zone Manager of the zone, so the Owner has
/// notifications to review (MSG-CYCLE-04) and the manager logs to correct. The fourth and fifth rafts keep a cycle with no log, which can still be cancelled.
/// Matched by the audit log, as a log has no code: a rerun does not bring back a log the user removed. Runs after the staff step. A manager who lost the zone
/// on an older database leaves the visit to the Owner.
/// </summary>
internal sealed class DemoCultivationLogStep : IDemoSeedStep
{
    private const int RaftsWithLogs = 3;
    private const int DaysBetweenVisits = 30;

    private static readonly (string ZoneCode, string Manager)[] Zones =
    [
        (DemoFarmingZoneStep.ZoneOne, "quanlyvung1"),
        (DemoFarmingZoneStep.ZoneTwo, "quanlyvung2"),
    ];

    public string Name => "cultivation logs";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var created = 0;
        foreach (var (zoneCode, manager) in Zones)
        {
            var zoneId = await context.Db.FarmingZones.Where(z => z.Code == zoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
            var actor = await ActorAsync(context, manager, zoneId, cancellationToken);
            using var acting = context.Get<AmbientActor>().Use(actor);
            for (var number = 1; number <= RaftsWithLogs; number++)
            {
                created += await RecordVisitsAsync(context, zoneId, DemoRaftStep.RaftCode(zoneCode, number), number, cancellationToken);
            }
        }

        return created;
    }

    private static async Task<int> RecordVisitsAsync(DemoSeedContext context, Guid zoneId, string raftCode, int number, CancellationToken cancellationToken)
    {
        var cycle = await context.Db.CultivationCycles.AsNoTracking()
            .Where(c => c.Raft.ZoneId == zoneId && c.Raft.Code == raftCode && c.StatusId == StatusIds.CultivationCycle.Open
                && c.Raft.StatusId == StatusIds.Usage.InUse && c.Raft.Zone.StatusId == StatusIds.Usage.InUse)
            .Select(c => new { c.Id, c.StartDate })
            .FirstOrDefaultAsync(cancellationToken);
        if (cycle is null
            || await context.Db.AuditEvents.AnyAsync(e => e.Action == CultivationLogAudit.Created && e.ZoneId == zoneId && e.RecordCode == raftCode, cancellationToken))
        {
            return 0;
        }

        var service = context.Get<CultivationLogService>();
        var created = 0;
        for (var visit = cycle.StartDate.AddDays(DaysBetweenVisits); visit <= context.Today; visit = visit.AddDays(DaysBetweenVisits))
        {
            created++;
            var request = new RecordCultivationLogRequest(visit, created == 1 ? "Hàu bám tốt, dây treo chắc, không có hàu chết." : "Hàu lớn đều, đã vệ sinh dây treo và phao.");
            DemoSeedContext.Ensure(await service.RecordAsync(cycle.Id, request, [DemoFiles.CultivationLogPhoto(number)], cancellationToken), $"log {created} of raft {raftCode}");
        }

        return created;
    }

    /// <summary>The Zone Manager of the zone while they can record there, else the Owner.</summary>
    private static async Task<Guid> ActorAsync(DemoSeedContext context, string username, Guid zoneId, CancellationToken cancellationToken)
    {
        var managerId = await context.Db.Accounts.Where(a => a.Username == username).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(cancellationToken);
        var access = managerId is { } id ? await context.Get<PermissionService>().GetAccessAsync(id, cancellationToken) : null;
        return access is not null && access.Allows(PermissionFunctions.CultivationLogs, PermissionAction.Create, PermissionScope.ZoneScoped, zoneId)
            ? access.AccountId
            : context.OwnerId;
    }
}
