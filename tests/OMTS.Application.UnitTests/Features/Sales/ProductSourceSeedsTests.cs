using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Sales.Products;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The product detail names the seed items it is grown from ("Giống nguồn"): the seed items whose product it is, read-only; a trader sees none.</summary>
public sealed class ProductSourceSeedsTests
{
    private sealed class Catalog : IStatusCatalog
    {
        public string CodeOf(int statusId) => $"S{statusId}";

        public string DisplayNameOf(int statusId) => $"Status {statusId}";
    }

    private readonly Product milky = new() { Code = "SP000001", Name = "Hàu sữa", UnitPrice = 90_000m };
    private int reads;

    private ProductViews Views(params ProductSourceSeed[] seeds) => new(
        StubProxy.Create<IProductRepository>(h =>
        {
            h["IsUsedAsync"] = _ => false;
            h["SourceSeedsAsync"] = a =>
            {
                reads++;
                return seeds.Where(_ => (Guid)a[0]! == milky.Id).ToList();
            };
        }),
        StubProxy.Create<IAccountRepository>(_ => { }),
        StubProxy.Create<IFileStorage>(_ => { }),
        new Catalog());

    [Fact]
    public async Task The_detail_lists_the_seed_items_of_the_product_by_the_order_the_repository_gives()
    {
        var views = Views(new ProductSourceSeed(Guid.NewGuid(), "GI000001", "Hàu giống 3 cm"), new ProductSourceSeed(Guid.NewGuid(), "GI000007", "Hàu giống 5 cm"));

        var detail = await views.DetailAsync(milky, 10m, hidePrice: false, hideStock: false, CancellationToken.None);

        Assert.Equal(["GI000001", "GI000007"], detail.SourceSeeds.Select(s => s.Code).ToArray());
        Assert.Equal("Hàu giống 3 cm", detail.SourceSeeds[0].Name);
    }

    [Fact]
    public async Task A_product_no_seed_item_names_has_an_empty_list()
    {
        var detail = await Views().DetailAsync(milky, 0m, hidePrice: false, hideStock: false, CancellationToken.None);

        Assert.Empty(detail.SourceSeeds);
    }

    [Fact]
    public async Task A_trader_sees_no_source_seed_and_the_repository_is_not_asked()
    {
        var views = Views(new ProductSourceSeed(Guid.NewGuid(), "GI000001", "Hàu giống 3 cm"));

        var detail = await views.DetailAsync(milky, 0m, hidePrice: true, hideStock: true, CancellationToken.None);

        Assert.Empty(detail.SourceSeeds);
        Assert.Equal(0, reads);
    }
}
