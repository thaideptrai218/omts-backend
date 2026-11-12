using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Cultivation.Nursery;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// NURSERY (owner decision of 13/11/2026, v2: Ươm → Chu kỳ nuôi → Thả giống): three batches in the first In Use demo zone, each started by its Zone Manager from a demo seed lot that still has stock. Two are already
/// "đã ươm xong" (Completed: 10,000 pieces in, 9,500 survived; 6,500 in, 6,000 survived; different seed lots, nothing seeded yet), so the Owner can start cycles on the empty rafts of <see cref="DemoReadyRaftStep"/> and
/// seed from them straight away; the third (3,500 pieces, started 7 days ago) is still Nursing to try the completion. They go through the real services: the pieces leave the stock in the same save (never below zero:
/// a lot with too little left is passed over, and a lot already used by an earlier batch of this run is avoided when another one will do), and the photos are demo pictures. Runs after the sessions and the harvests,
/// which take their seed first. Each is matched by its own note: a rerun adds nothing and does not bring back a batch the user cancelled, closed or completed. The demo cycles of the old way keep working: they were
/// started by the bootstrap and have no nursery batch.
/// </summary>
internal sealed class DemoNurseryStep : IDemoSeedStep
{
    /// <param name="MinOnHand">What the lot must still have on hand to be used.</param>
    /// <param name="Survivors">When set, the batch is completed right away with these survivors, <paramref name="CompletedDaysAgo"/> days ago.</param>
    private sealed record Plan(string Note, decimal Pieces, int StartedDaysAgo, decimal MinOnHand, decimal? Survivors = null, int CompletedDaysAgo = 0);

    private static readonly Plan[] Plans =
    [
        new("Đợt ươm demo 1: đã ươm xong, sẵn sàng thả (9.500 mảnh sống sót)", 10_000m, 12, 10_000m, 9_500m, 3),
        new("Đợt ươm demo 2: đã ươm xong, sẵn sàng thả (6.000 mảnh sống sót)", 6_500m, 9, 6_500m, 6_000m, 2),
        new("Đợt ươm demo 3: vào ươm 7 ngày trước, chờ ươm xong", 3_500m, 7, 3_500m),
    ];

    public string Name => "nursery batches";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var zoneId = await context.Db.FarmingZones.Where(z => z.Code == DemoFarmingZoneStep.ZoneOne).Select(z => z.Id).SingleAsync(cancellationToken);
        var created = 0;
        var usedLots = new HashSet<Guid>();
        for (var index = 0; index < Plans.Length; index++)
        {
            var plan = Plans[index];
            // Matched by its own note, whatever its status: batches a user made in the zone do not stop it, and a demo batch the user cancelled, closed or completed is not brought back.
            if (await context.Db.NurseryBatches.AnyAsync(b => b.ZoneId == zoneId && b.Note == plan.Note, cancellationToken))
            {
                continue;
            }

            var actor = await DemoCultivationLogStep.ActorAsync(context, PermissionFunctions.CultivationCycles, "quanlyvung1", zoneId, cancellationToken);
            using var acting = context.Get<AmbientActor>().Use(actor);
            // Read again for each batch: the one before took its pieces out of the stock.
            var lots = DemoSeedContext.Ensure(await context.Get<NurseryReadService>().SeedLotsAsync(zoneId, cancellationToken), "seed lots for the demo nursery");
            var start = context.Today.AddDays(-plan.StartedDaysAgo);
            var fitting = lots.Lots.Where(l => l.OnHand >= plan.MinOnHand && l.ReceivedDate <= start).OrderByDescending(l => l.OnHand).ToList();
            var lot = fitting.FirstOrDefault(l => !usedLots.Contains(l.Id)) ?? fitting.FirstOrDefault();
            if (lot is null)
            {
                context.Notes.Add($"Demo nursery: no seed lot has enough stock left for \"{plan.Note}\", so that nursery batch was not started.");
                continue;
            }

            var request = new StartNurseryRequest(lot.Id, plan.Pieces, start, plan.Note, false);
            var started = DemoSeedContext.Ensure(
                await context.Get<NurseryBatchService>().StartAsync(zoneId, request, [DemoFiles.CyclePlanPhoto(90 + index)], cancellationToken), "demo nursery batch");
            usedLots.Add(lot.Id);
            created++;

            if (plan.Survivors is { } survivors)
            {
                var rowVersion = await context.Db.NurseryBatches.AsNoTracking().Where(b => b.Id == started.Batch.Id).Select(b => b.RowVersion).SingleAsync(cancellationToken);
                var complete = new CompleteNurseryRequest(survivors, context.Today.AddDays(-plan.CompletedDaysAgo), RowVersionGuard.Encode(rowVersion));
                DemoSeedContext.Ensure(
                    await context.Get<NurseryCompletionService>().CompleteAsync(started.Batch.Id, complete, [DemoFiles.CyclePlanPhoto(95 + index)], cancellationToken), "completing the demo nursery batch");
            }
        }

        return created;
    }
}
