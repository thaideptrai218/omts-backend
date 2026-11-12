using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Cultivation.Nursery;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// NURSERY-01 (owner decision of 12/11/2026, Q21: a new cycle is created by completing a nursery batch): one batch Nursing in the first In Use demo zone, started today by its Zone Manager from a demo seed lot that
/// still has stock, so the Owner can complete it, divide it between rafts and start the new flow. It goes through the real service: the pieces leave the stock in the same save (never below zero: a lot with too little
/// left is passed over), and the start photo is a demo picture. Runs after the sessions and the harvests, which take their seed first. Matched by any batch of the zone: a rerun adds nothing and does not bring back a
/// batch the user cancelled or completed. The demo cycles keep working: they were started by the bootstrap, which the rule that a cycle is created by a nursery batch does not touch.
/// </summary>
internal sealed class DemoNurseryStep : IDemoSeedStep
{
    private const decimal MinOnHand = 400m;
    private const decimal Pieces = 1000m;

    public string Name => "nursery batch";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var zoneId = await context.Db.FarmingZones.Where(z => z.Code == DemoFarmingZoneStep.ZoneOne).Select(z => z.Id).SingleAsync(cancellationToken);
        if (await context.Db.NurseryBatches.AnyAsync(b => b.ZoneId == zoneId, cancellationToken))
        {
            return 0;
        }

        var actor = await DemoCultivationLogStep.ActorAsync(context, PermissionFunctions.CultivationCycles, "quanlyvung1", zoneId, cancellationToken);
        using var acting = context.Get<AmbientActor>().Use(actor);
        var lots = DemoSeedContext.Ensure(await context.Get<NurseryReadService>().SeedLotsAsync(zoneId, cancellationToken), "seed lots for the demo nursery");
        var lot = lots.Lots.OrderByDescending(l => l.OnHand).FirstOrDefault(l => l.OnHand >= MinOnHand);
        if (lot is null)
        {
            context.Notes.Add("Demo nursery: no seed lot has enough stock left, so no nursery batch was started.");
            return 0;
        }

        var quantity = Math.Min(Pieces, Math.Floor(lot.OnHand / 2m));
        var request = new StartNurseryRequest(lot.Id, quantity, null, "Lô ươm mẫu để thử luồng hoàn tất ươm", false);
        DemoSeedContext.Ensure(await context.Get<NurseryBatchService>().StartAsync(zoneId, request, [DemoFiles.CyclePlanPhoto(90)], cancellationToken), "demo nursery batch");
        return 1;
    }
}
