using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Cultivation.Cycles;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// CYCLE-01: an Open cycle on the first five rafts of each In Use demo zone, started weeks ago, so seeding, logs and the GFR-04
/// water reminder have data; the other rafts In Use can start one. The even rafts have no plan estimates, which are optional.
/// Matched by the start in the audit log, as a cycle has no code: a rerun does not bring back a cycle the user cancelled.
/// </summary>
internal sealed class DemoCultivationCycleStep : IDemoSeedStep
{
    private const int RaftsWithCycle = 5;

    public string Name => "cultivation cycles";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<CultivationCycleService>();
        var created = 0;
        foreach (var zoneCode in new[] { DemoFarmingZoneStep.ZoneOne, DemoFarmingZoneStep.ZoneTwo })
        {
            var zoneId = await context.Db.FarmingZones.Where(z => z.Code == zoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
            for (var number = 1; number <= RaftsWithCycle; number++)
            {
                var code = DemoRaftStep.RaftCode(zoneCode, number);
                var raftId = await context.Db.Rafts.Where(r => r.ZoneId == zoneId && r.Code == code).Select(r => r.Id).SingleAsync(cancellationToken);
                if (await context.Db.AuditEvents.AnyAsync(e => e.Action == "CultivationCycle.Started" && e.ZoneId == zoneId && e.RecordCode == code, cancellationToken))
                {
                    continue;
                }

                DemoSeedContext.Ensure(await service.StartAsync(raftId, Request(context.Today, number), cancellationToken), $"cycle of raft {code}");
                created++;
            }
        }

        return created;
    }

    private static StartCycleRequest Request(DateOnly today, int number)
    {
        var start = today.AddDays(-30 - 10 * number);
        return number % 2 == 0
            ? new StartCycleRequest(start, null, null, null)
            : new StartCycleRequest(start, 200m + 50m * number, start.AddMonths(10), 8000m + 500m * number);
    }
}
