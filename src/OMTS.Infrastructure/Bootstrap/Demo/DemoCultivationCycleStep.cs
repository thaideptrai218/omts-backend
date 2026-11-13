using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// CYCLE-01: an Open cycle on the first five rafts of each In Use demo zone, started weeks ago, so seeding, logs and the GFR-04
/// water reminder have data; the other rafts In Use can start one. The even rafts have no plan estimates, which are optional. The planned seed (5,000, 7,000 and 9,000 pieces) is about what the demo seeding sessions hang, so no demo session is over its plan (BR-CYCLE-01).
/// Matched by the start in the audit log, as a cycle has no code: a rerun does not bring back a cycle the user cancelled.
/// A raft that exists is left as it is: an older database can have one Discontinued, or one with a cycle and no audit row, and the seed goes on past it.
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
                var raft = await context.Db.Rafts
                    .Where(r => r.ZoneId == zoneId && r.Code == code)
                    .Select(r => new { r.Id, Usable = r.StatusId == StatusIds.Usage.InUse && r.Zone.StatusId == StatusIds.Usage.InUse })
                    .SingleAsync(cancellationToken);
                if (!raft.Usable
                    || await context.Db.CultivationCycles.AnyAsync(c => c.RaftId == raft.Id, cancellationToken)
                    || await context.Db.AuditEvents.AnyAsync(e => e.Action == "CultivationCycle.Started" && e.ZoneId == zoneId && e.RecordCode == code, cancellationToken))
                {
                    continue;
                }

                DemoSeedContext.Ensure(
                    await service.StartAsync(raft.Id, Request(context.Today, number), [DemoFiles.CyclePlanPhoto(number)], cancellationToken, bootstrap: true), $"cycle of raft {code}");
                created++;
            }
        }

        return created;
    }

    private static StartCycleRequest Request(DateOnly today, int number)
    {
        var start = today.AddDays(-30 - 10 * number);
        return number % 2 == 0
            ? new StartCycleRequest(start, null, null)
            : new StartCycleRequest(start, 4000m + 1000m * number, 8000m + 500m * number);
    }
}
