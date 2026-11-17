using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>A Seed item must name the product it sells as ("Loại hàu bán ra"), on create and on update (MSG-GEN-01 on Product). Only the refusals run here: they stop before anything is written.</summary>
public sealed class SeedItemProductTests
{
    private readonly Product milky = new() { Code = "SP000001", Name = "Hàu sữa" };

    private FarmingInputItemService Service(Product? product, FarmingInputItem? stored = null, bool seedInUse = false) => new(
        StubProxy.Create<IFarmingInputItemRepository>(h =>
        {
            h["GetForEditAsync"] = _ => stored;
            h["IsSeedInUseAsync"] = _ => seedInUse;
        }),
        null!, null!, null!,
        StubProxy.Create<IProductRepository>(h => h["GetForEditAsync"] = a => product is not null && (Guid)a[0]! == product.Id ? product : null),
        null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static SaveFarmingInputItemRequest Request(InputItemGroup group, Guid? productId, string name = "Hàu giống 3 cm") =>
        new("GI000001", name, group, "con", null, ProductId: productId);

    [Fact]
    public async Task A_new_seed_item_without_a_product_is_refused_on_Product()
    {
        var result = await Service(milky).CreateAsync(Request(InputItemGroup.Seed, null), CancellationToken.None);

        Assert.Equal("Product", result.Error!.Details!["field"]);
        Assert.Equal("MSG-GEN-01", result.Error.Message.Code);
    }

    [Fact]
    public async Task A_seed_item_naming_an_unknown_product_is_refused_on_Product()
    {
        var result = await Service(milky).CreateAsync(Request(InputItemGroup.Seed, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal("Product", result.Error!.Details!["field"]);
    }

    [Fact]
    public async Task A_seed_item_naming_a_product_that_is_discontinued_or_deleted_is_refused_on_Product()
    {
        milky.StatusId = StatusIds.Usage.Discontinued;
        var discontinued = await Service(milky).CreateAsync(Request(InputItemGroup.Seed, milky.Id), CancellationToken.None);

        milky.StatusId = StatusIds.Usage.InUse;
        milky.IsHidden = true;
        var hidden = await Service(milky).CreateAsync(Request(InputItemGroup.Seed, milky.Id), CancellationToken.None);

        Assert.Equal("Product", discontinued.Error!.Details!["field"]);
        Assert.Equal("Product", hidden.Error!.Details!["field"]);
    }

    [Fact]
    public async Task Updating_a_seed_item_needs_its_product_too()
    {
        var stored = new FarmingInputItem { Code = "GI000001", Name = "Hàu giống 3 cm", Group = InputItemGroup.Seed, Unit = "con" };

        var result = await Service(milky, stored).UpdateAsync(stored.Id, Request(InputItemGroup.Seed, null), CancellationToken.None);

        Assert.Equal("Product", result.Error!.Details!["field"]);
    }

    [Fact]
    public async Task The_field_is_named_with_the_other_invalid_fields_of_the_form()
    {
        var result = await Service(milky).CreateAsync(Request(InputItemGroup.Seed, null, name: ""), CancellationToken.None);

        Assert.Equal(["Name", "Product"], ((IReadOnlyList<string>)result.Error!.Details!["fields"]!).ToArray());
    }

    [Fact]
    public async Task The_product_of_a_seed_in_use_cannot_change()
    {
        var other = new Product { Code = "SP000002", Name = "Hàu Thái Bình Dương" };
        var stored = new FarmingInputItem { Code = "GI000001", Name = "Hàu giống 3 cm", Group = InputItemGroup.Seed, Unit = "con", ProductId = other.Id, Product = other };

        var result = await Service(milky, stored, seedInUse: true).UpdateAsync(stored.Id, Request(InputItemGroup.Seed, milky.Id), CancellationToken.None);

        Assert.Equal("Product", result.Error!.Details!["field"]);
        Assert.Equal("MSG-GEN-01", result.Error.Message.Code);
        Assert.Equal("The oyster of a seed in use cannot change", result.Error.Details["hint"]);
    }
}
