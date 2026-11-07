using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// CYCLE-02: seeding on the Open cycles of the first three rafts of each In Use demo zone, by the Zone Manager of the zone, so the raft and progress views show Growing lanes
/// traced to their seed lots and the receipts of those lots can no longer be cancelled. The first raft has two sessions over its lanes, with a lane re-hung, and its seeding is marked
/// complete, so the Owner has a notification to review (MSG-CYCLE-02). The second has one session with a lane left Not seeded, and the third every lane seeded and the offer to mark it
/// complete. The fourth and fifth rafts keep a cycle with no record, which can still be cancelled. Matched by the audit log, as a session has no code: a rerun does not bring back a
/// session the user removed. A raft that changed on an older database, or a cycle younger than its lot, is passed over. Runs after the receipts and the staff.
/// </summary>
internal sealed class DemoSeedingStep : IDemoSeedStep
{
    /// <summary>A zone's lots, by the demo receipt that holds them (BR-CYCLE-02: one seed lot per session).</summary>
    private static readonly (string ZoneCode, string Manager, string[] Receipts)[] Zones =
    [
        (DemoFarmingZoneStep.ZoneOne, "quanlyvung1", ["DEMO-R01", "DEMO-R02", "DEMO-R08"]),
        (DemoFarmingZoneStep.ZoneTwo, "quanlyvung2", ["DEMO-R02", "DEMO-R07", "DEMO-R13"]),
    ];

    public string Name => "seeding sessions";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var created = 0;
        foreach (var (zoneCode, manager, receipts) in Zones)
        {
            var zoneId = await context.Db.FarmingZones.Where(z => z.Code == zoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
            var actor = await DemoCultivationLogStep.ActorAsync(context, PermissionFunctions.CultivationCycles, manager, zoneId, cancellationToken);
            using var acting = context.Get<AmbientActor>().Use(actor);
            for (var number = 1; number <= receipts.Length; number++)
            {
                created += await SeedRaftAsync(context, zoneId, DemoRaftStep.RaftCode(zoneCode, number), number, receipts[number - 1], cancellationToken);
            }
        }

        return created;
    }

    private static async Task<int> SeedRaftAsync(DemoSeedContext context, Guid zoneId, string raftCode, int number, string receiptCode, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var cycle = await db.CultivationCycles.AsNoTracking()
            .Where(c => c.Raft.ZoneId == zoneId && c.Raft.Code == raftCode && c.StatusId == StatusIds.CultivationCycle.Open
                && c.Raft.StatusId == StatusIds.Usage.InUse && c.Raft.Zone.StatusId == StatusIds.Usage.InUse)
            .Select(c => new { c.Id, c.RaftId, c.StartDate })
            .FirstOrDefaultAsync(cancellationToken);
        var prefix = $"{receiptCode} ";
        var lot = await db.PurchaseReceiptLines.AsNoTracking()
            .Where(l => l.Receipt.Notes != null && l.Receipt.Notes.StartsWith(prefix) && l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed && l.Item != null && l.Item.Group == InputItemGroup.Seed)
            .OrderBy(l => l.LineNumber)
            .Select(l => new { l.Id, l.Receipt.ReceiptDate })
            .FirstOrDefaultAsync(cancellationToken);
        if (cycle is null || lot is null
            || await db.AuditEvents.AnyAsync(e => e.Action == SeedingValues.Created && e.ZoneId == zoneId && e.RecordCode == raftCode, cancellationToken))
        {
            return 0;
        }

        // The first day the session may have (BR-CYCLE-02), a few days into the cycle as in BF-02.1.
        var first = (cycle.StartDate > lot.ReceiptDate ? cycle.StartDate : lot.ReceiptDate).AddDays(2);
        var lanes = await db.Lanes.AsNoTracking().Where(l => l.RaftId == cycle.RaftId && l.StatusId == StatusIds.Lane.InUse).OrderBy(l => l.LaneNumber).Select(l => l.LaneNumber).ToListAsync(cancellationToken);
        if (first.AddDays(3) > context.Today || lanes.Count < 2)
        {
            return 0;
        }

        var service = context.Get<SeedingRecordService>();
        var half = lanes.Count / 2;
        var sessions = number switch
        {
            // Two sessions; the second re-hangs the last lane of the first (BR-CYCLE-02).
            1 => new[] { (first, lanes.Take(half).ToList(), 2000m), (first.AddDays(3), lanes.Skip(half - 1).ToList(), 3000m) },
            2 => [(first, lanes.Take(lanes.Count - 1).ToList(), 4000m)],
            _ => [(first, lanes, 6000m)],
        };
        foreach (var (date, numbers, quantity) in sessions)
        {
            DemoSeedContext.Ensure(await service.RecordAsync(cycle.Id, new RecordSeedingRequest(date, lot.Id, numbers, quantity), cancellationToken), $"seeding of raft {raftCode}");
        }

        if (number == 1)
        {
            var rowVersion = await db.CultivationCycles.AsNoTracking().Where(c => c.Id == cycle.Id).Select(c => c.RowVersion).SingleAsync(cancellationToken);
            DemoSeedContext.Ensure(
                await context.Get<SeedingCompletionService>().CompleteAsync(cycle.Id, new MarkSeedingCompleteRequest(RowVersionGuard.Encode(rowVersion)), cancellationToken),
                $"completing the seeding of raft {raftCode}");
        }

        return sessions.Length;
    }
}
