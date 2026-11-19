using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Sales.Products;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// SALE-11: the product catalogue, three products In Use and one Discontinued, which can be resumed, in two groups, with the lồng and thùng units that turn the cages of a harvest into kg. Matched by name; a
/// product the Owner renamed since (found by its Product.Created audit row, which holds the name on databases seeded before product codes) is not made again.
/// </summary>
internal sealed class DemoProductStep : IDemoSeedStep
{
    private const string Whole = "Hàu nguyên con";
    private const string Meat = "Hàu thịt";

    // Species and origin in the name, the commercial size (shell length in cm, SizeMinCm/SizeMaxCm) in the product: the seed's size (3-5 mm) is not the size sold.
    // Pacific oyster 7-9 cm is SOURCED (commercial size 65-75 mm, about 8-9 cm, 10-12 pieces/kg at Van Don). Native Van Don oyster 4-6 cm is an ASSUMPTION: the only source says native estuary oysters are much smaller than Pacific ones;
    // no commercial size is verified, the Owner must confirm it. 25,000 VND/kg for it is an assumption too. No further product is seeded: none can be justified without inventing a species.
    // Prices are the farm-gate range reported for Van Don in 2025 (15,000-35,000 VND/kg by size); the Owner sets the real list price.
    private static readonly (string Name, decimal UnitPrice, bool Discontinued, string Group, string Description, decimal SizeMinCm, decimal? SizeMaxCm, (string Unit, decimal Kg)[] Units)[] Products =
    [
        ("Hàu bản địa Vân Đồn", 25_000, false, Whole, "Hàu bản địa cửa sông nuôi tại Vân Đồn, Quảng Ninh; loại 1.", 4m, 6m, [("Lồng", 50m), ("Tấn", 1000m)]),
        ("Hàu Thái Bình Dương", 35_000, false, Whole, "Hàu Thái Bình Dương (còn gọi là hàu sữa) nuôi tại Vân Đồn, Quảng Ninh; khoảng 10-12 con/kg.", 7m, 9m, [("Lồng", 50m), ("Tấn", 1000m)]),
        ("Hàu thịt tách vỏ", 120_000, true, Meat, "Thịt hàu tách vỏ, đóng thùng.", 0m, null, [("Thùng", 5m)]),
    ];

    public string Name => "products";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<ProductService>();
        var groups = context.Get<ProductCategoryService>();
        var created = 0;
        var groupIds = new Dictionary<string, Guid>();
        foreach (var (name, unitPrice, discontinued, group, description, sizeMin, sizeMax, units) in Products)
        {
            // A product the Owner renamed since is found by its Product.Created audit row: it holds the name in its snapshot (as JSON, the way the audit writes it), and, on databases
            // seeded before product codes, in the record code.
            var quotedName = System.Text.Json.JsonSerializer.Serialize(name);
            if (await context.Db.Products.AnyAsync(p => p.Name == name, cancellationToken)
                || await context.Db.AuditEvents.AnyAsync(e => e.Action == "Product.Created" && (e.RecordCode == name || (e.After != null && e.After.Contains(quotedName))), cancellationToken))
            {
                continue;
            }

            if (!groupIds.TryGetValue(group, out var groupId))
            {
                groupId = await context.Db.ProductCategories.Where(g => g.Name == group).Select(g => g.Id).FirstOrDefaultAsync(cancellationToken);
                if (groupId == Guid.Empty)
                {
                    groupId = DemoSeedContext.Ensure(await groups.CreateAsync(new SaveProductCategoryRequest(group), cancellationToken), $"product group {group}").Id;
                }

                groupIds[group] = groupId;
            }

            var request = new SaveProductRequest(
                name, unitPrice, CategoryId: groupId, Description: description, SizeMinCm: sizeMin > 0 ? sizeMin : null, SizeMaxCm: sizeMax, Units: units.Select(u => new ProductUnitInput(null, null, u.Unit, u.Kg, null)).ToList());
            var saved = DemoSeedContext.Ensure(await service.CreateAsync(request, cancellationToken), $"product {name}");
            if (discontinued)
            {
                DemoSeedContext.Ensure(await service.DiscontinueAsync(saved.Product.Id, cancellationToken), $"discontinuing product {name}");
            }

            created++;
        }

        return created;
    }
}
