using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// The product a cycle farms (owner decision of 13/11/2026): a cycle started in the app always has one, but the demo cycles of <see cref="DemoCultivationCycleStep"/> are started by the bootstrap path before the products
/// and the purchase requests exist, so they are given theirs here, after every request. A cycle that already harvested for a purchase request takes the product of that request (so the lots stay valid); the others
/// alternate between "Hàu béo" and "Hàu sữa", so the open requests of the demo (mostly "Hàu béo", one "Hàu sữa") match some rafts and not others, and a harvest for the wrong product can be shown refused (HARVEST_PRODUCT_MISMATCH).
/// Only a cycle with no product is touched, so a rerun changes nothing and a product chosen later is kept. Written directly, like the rest of the demo data.
/// </summary>
internal sealed class DemoCycleProductStep : IDemoSeedStep
{
    private const string MainProduct = "Hàu béo";
    private const string OtherProduct = "Hàu sữa";

    public string Name => "cycle products";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var cycles = await db.CultivationCycles.Where(c => c.ProductId == null).OrderBy(c => c.Raft.Code).ThenBy(c => c.StartDate).ToListAsync(cancellationToken);
        if (cycles.Count == 0)
        {
            return 0;
        }

        var products = await db.Products.Where(p => p.StatusId == StatusIds.Usage.InUse).ToDictionaryAsync(p => p.Name, p => p.Id, cancellationToken);
        if (!products.TryGetValue(MainProduct, out var main) || !products.TryGetValue(OtherProduct, out var other))
        {
            context.Notes.Add("Demo cycle products: the products \"Hàu béo\" and \"Hàu sữa\" are not both In Use, so the demo cycles were left without a product.");
            return 0;
        }

        var ofLots = await db.HarvestLots.AsNoTracking()
            .Where(l => l.PurchaseRequestId != null)
            .Select(l => new { l.CycleId, l.HarvestedAt, ProductId = l.PurchaseRequest!.ProductId })
            .ToListAsync(cancellationToken);
        var firstProduct = ofLots.GroupBy(l => l.CycleId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.HarvestedAt).First().ProductId);
        var alternate = 0;
        foreach (var cycle in cycles)
        {
            cycle.ProductId = firstProduct.TryGetValue(cycle.Id, out var harvested) ? harvested : alternate++ % 2 == 0 ? main : other;
        }

        await db.SaveChangesAsync(cancellationToken);
        return cycles.Count;
    }
}
