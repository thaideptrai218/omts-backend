using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Cultivation.Nursery;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// NURSERY-01 (owner decision of 12/11/2026, Q21: a new cycle is created by completing a nursery batch): three batches Nursing in the first In Use demo zone, each started by its Zone Manager from a demo
/// seed lot that still has stock, so the Owner can complete them, divide the survivors between the empty rafts of <see cref="DemoReadyRaftStep"/> and start the new flow. The first (1,000 pieces) was
/// started today; the two others (4,000 and 3,500 pieces) 10 and 7 days ago, as a batch about to be completed would be. They go through the real service: the pieces leave the stock in the same save
/// (never below zero: a lot with too little left is passed over, and a lot already used by an earlier batch of this run is avoided when another one will do), and the start photo is a demo picture.
/// Runs after the sessions and the harvests, which take their seed first. Each is matched by its own note: a rerun adds nothing and does not bring back a batch the user cancelled or completed. The demo
/// cycles keep working: they were started by the bootstrap, which the rule that a cycle is created by a nursery batch does not touch.
/// </summary>
internal sealed class DemoNurseryStep : IDemoSeedStep
{
    /// <param name="MinOnHand">What the lot must still have on hand to be used.</param>
    /// <param name="HalfOfStock">The pieces are at most half of what the lot has on hand (the first, small batch); otherwise exactly <paramref name="Pieces"/>.</param>
    private sealed record Plan(string Note, decimal Pieces, int DaysAgo, decimal MinOnHand, bool HalfOfStock);

    private static readonly Plan[] Plans =
    [
        new("Lô ươm mẫu để thử luồng hoàn tất ươm", 1000m, 0, 400m, true),
        new("Lô ươm demo 1: vào ươm 10 ngày trước, chờ hoàn tất ươm", 4000m, 10, 4000m, false),
        new("Lô ươm demo 2: vào ươm 7 ngày trước, chờ hoàn tất ươm", 3500m, 7, 3500m, false),
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
            // Matched by its own note, whatever its status: batches a user made in the zone do not stop it, and a demo batch the user cancelled or completed is not brought back.
            if (await context.Db.NurseryBatches.AnyAsync(b => b.ZoneId == zoneId && b.Note == plan.Note, cancellationToken))
            {
                continue;
            }

            var actor = await DemoCultivationLogStep.ActorAsync(context, PermissionFunctions.CultivationCycles, "quanlyvung1", zoneId, cancellationToken);
            using var acting = context.Get<AmbientActor>().Use(actor);
            // Read again for each batch: the one before took its pieces out of the stock.
            var lots = DemoSeedContext.Ensure(await context.Get<NurseryReadService>().SeedLotsAsync(zoneId, cancellationToken), "seed lots for the demo nursery");
            var start = context.Today.AddDays(-plan.DaysAgo);
            var fitting = lots.Lots.Where(l => l.OnHand >= plan.MinOnHand && l.ReceivedDate <= start).OrderByDescending(l => l.OnHand).ToList();
            var lot = fitting.FirstOrDefault(l => !usedLots.Contains(l.Id)) ?? fitting.FirstOrDefault();
            if (lot is null)
            {
                context.Notes.Add($"Demo nursery: no seed lot has enough stock left for \"{plan.Note}\", so that nursery batch was not started.");
                continue;
            }

            var quantity = plan.HalfOfStock ? Math.Min(plan.Pieces, Math.Floor(lot.OnHand / 2m)) : plan.Pieces;
            var request = new StartNurseryRequest(lot.Id, quantity, plan.DaysAgo == 0 ? null : start, plan.Note, false);
            DemoSeedContext.Ensure(
                await context.Get<NurseryBatchService>().StartAsync(zoneId, request, [DemoFiles.CyclePlanPhoto(90 + index)], cancellationToken), "demo nursery batch");
            usedLots.Add(lot.Id);
            created++;
        }

        return created;
    }
}
