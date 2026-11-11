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

    private static readonly (string Name, decimal UnitPrice, bool Discontinued, string Group, (string Unit, decimal Kg)[] Units)[] Products =
    [
        ("Hàu sữa", 60_000, false, Whole, [("Lồng", 25m), ("Thùng", 10m)]),
        ("Hàu béo", 75_000, false, Whole, [("Lồng", 25m), ("Thùng", 10m)]),
        ("Hàu sống loại nhỏ", 45_000, false, Whole, [("Lồng", 20m)]),
        ("Hàu thịt tách vỏ", 120_000, true, Meat, [("Thùng", 5m)]),
    ];

    public string Name => "products";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<ProductService>();
        var groups = context.Get<ProductCategoryService>();
        var created = 0;
        var groupIds = new Dictionary<string, Guid>();
        foreach (var (name, unitPrice, discontinued, group, units) in Products)
        {
            if (await context.Db.Products.AnyAsync(p => p.Name == name, cancellationToken)
                || await context.Db.AuditEvents.AnyAsync(e => e.Action == "Product.Created" && e.RecordCode == name, cancellationToken))
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
                name, unitPrice, CategoryId: groupId, Units: units.Select(u => new ProductUnitInput(null, null, u.Unit, u.Kg, null)).ToList());
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
