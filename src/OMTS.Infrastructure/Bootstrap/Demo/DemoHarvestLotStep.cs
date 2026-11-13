using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// HARVEST-01: harvest lots on the first raft of each In Use demo zone and the second raft of the first, by the Zone Manager of the zone, dated today as a lot always is. Each first raft has
/// two trips on one day (lot codes without and with -2): one lane ends Being harvested and one Harvested. The second raft has every seeded lane finished, so its manager has the reminder to close
/// the cycle (MSG-CYCLE-08), and the Owner has the harvest notifications to review (MSG-HARVEST-01). The third raft of each zone stays Growing, with sessions that can still be corrected. Matched by
/// the audit log, as a lot code holds the day it was first run: a rerun adds nothing and does not bring back a lot the user removed. A raft with too little seed on an older database is passed over.
/// </summary>
internal sealed class DemoHarvestLotStep : IDemoSeedStep
{
    private const int CagesOfATrip = 8;

    private static readonly (string ZoneCode, string Manager, int[] RaftNumbers)[] Zones =
    [
        (DemoFarmingZoneStep.ZoneOne, "quanlyvung1", [1, 2]),
        (DemoFarmingZoneStep.ZoneTwo, "quanlyvung2", [1]),
    ];

    public string Name => "harvest lots";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        // A lot is recorded for a purchase request (decision of 11/11/2026): the demo trader's Accepted one, whose harvest is not marked done.
        var order = await context.Db.PurchaseRequests.AsNoTracking()
            .Where(r => r.StatusId == StatusIds.PurchaseRequest.Accepted && r.HarvestCompletedAt == null)
            .OrderBy(r => r.AgreedPickupDate).ThenBy(r => r.SentAt)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            context.Notes.Add("Demo harvest lots: no Accepted purchase request to harvest for, so no lot was recorded.");
            return 0;
        }

        var created = 0;
        foreach (var (zoneCode, manager, raftNumbers) in Zones)
        {
            var zoneId = await context.Db.FarmingZones.Where(z => z.Code == zoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
            var actor = await DemoCultivationLogStep.ActorAsync(context, PermissionFunctions.Harvests, manager, zoneId, cancellationToken);
            using var acting = context.Get<AmbientActor>().Use(actor);
            foreach (var number in raftNumbers)
            {
                created += await HarvestRaftAsync(context, zoneId, zoneCode, DemoRaftStep.RaftCode(zoneCode, number), number, order.Value, cancellationToken);
            }
        }

        return created;
    }

    private static async Task<int> HarvestRaftAsync(
        DemoSeedContext context, Guid zoneId, string zoneCode, string raftCode, int number, Guid order, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var cycle = await db.CultivationCycles.AsNoTracking()
            .Where(c => c.Raft.ZoneId == zoneId && c.Raft.Code == raftCode && c.StatusId == StatusIds.CultivationCycle.Open
                && c.Raft.StatusId == StatusIds.Usage.InUse && c.Raft.Zone.StatusId == StatusIds.Usage.InUse)
            .Select(c => new { c.Id, c.RaftId })
            .FirstOrDefaultAsync(cancellationToken);
        var codesOfRaft = $"{HarvestLotCodes.RaftPart(zoneCode, raftCode)}-";
        if (cycle is null
            || await db.AuditEvents.AnyAsync(e => e.Action == HarvestLotAudit.Created && e.ZoneId == zoneId && e.RecordCode != null && e.RecordCode.StartsWith(codesOfRaft), cancellationToken))
        {
            return 0;
        }

        var lanes = await db.SeedingRecordLanes.AsNoTracking()
            .Where(l => l.SeedingRecord.CycleId == cycle.Id && l.Lane.StatusId == StatusIds.Lane.InUse)
            .Select(l => l.Lane.LaneNumber).Distinct().OrderBy(n => n).ToListAsync(cancellationToken);
        if (lanes.Count < 2)
        {
            return 0;
        }

        var trips = number == 1
            ? new[]
            {
                new RecordHarvestRequest(lanes.Take(2).ToList(), [], CagesOfATrip, null, PurchaseRequestId: order),
                new RecordHarvestRequest([lanes[1]], [lanes[1]], CagesOfATrip / 2, 180m, PurchaseRequestId: order),
            }
            : [new RecordHarvestRequest(lanes, lanes, CagesOfATrip * lanes.Count, null, PurchaseRequestId: order)];
        var service = context.Get<HarvestLotRecordingService>();
        for (var trip = 0; trip < trips.Length; trip++)
        {
            DemoSeedContext.Ensure(await service.RecordAsync(cycle.RaftId, trips[trip], [DemoFiles.HarvestLotPhoto(number * 10 + trip)], cancellationToken), $"harvest of raft {raftCode}");
        }

        return trips.Length;
    }
}
