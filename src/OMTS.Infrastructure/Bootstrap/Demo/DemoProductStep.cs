using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Sales.Products;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// SALE-11: the price list, three products In Use and one Discontinued, which can be resumed. Matched by name; a product the
/// Owner renamed since (found by its Product.Created audit row) is not made again.
/// </summary>
internal sealed class DemoProductStep : IDemoSeedStep
{
    private static readonly (string Name, decimal UnitPrice, bool Discontinued)[] Products =
    [
        ("Hàu sữa", 60_000, false),
        ("Hàu béo", 75_000, false),
        ("Hàu sống loại nhỏ", 45_000, false),
        ("Hàu thịt tách vỏ", 120_000, true),
    ];

    public string Name => "products";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<ProductService>();
        var created = 0;
        foreach (var (name, unitPrice, discontinued) in Products)
        {
            if (await context.Db.Products.AnyAsync(p => p.Name == name, cancellationToken)
                || await context.Db.AuditEvents.AnyAsync(e => e.Action == "Product.Created" && e.RecordCode == name, cancellationToken))
            {
                continue;
            }

            var saved = DemoSeedContext.Ensure(await service.CreateAsync(new SaveProductRequest(name, unitPrice), cancellationToken), $"product {name}");
            if (discontinued)
            {
                DemoSeedContext.Ensure(await service.DiscontinueAsync(saved.Product.Id, cancellationToken), $"discontinuing product {name}");
            }

            created++;
        }

        return created;
    }
}
