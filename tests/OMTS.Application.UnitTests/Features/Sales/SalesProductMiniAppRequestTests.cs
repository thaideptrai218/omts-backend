using OMTS.Application.Features.Sales.Products;
using OMTS.Application.Features.Sales.Products.Dtos;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The Mini App writes a product through the product catalogue: what the form does not hold stays as the product has it, and the price is the one sent or the one the product has.</summary>
public class SalesProductMiniAppRequestTests
{
    private static readonly Guid Category = Guid.NewGuid();

    private static Product Existing() => new()
    {
        Code = "SP000007",
        Name = "Oysters",
        UnitPrice = 55_000m,
        CategoryId = Category,
        Note = "Sold by the cage",
        MinKg = 10m,
        MaxKg = 500m,
        SizeMinCm = 8m,
        SizeMaxCm = 12m,
    };

    [Fact]
    public void A_new_product_takes_the_fields_of_the_form_and_the_price_sent()
    {
        var request = SalesProductService.ToProductRequest(null, new SaveSalesProduct("sp1", "Oysters", "Large", UnitPrice: 60_000m));

        Assert.Equal(("Oysters", 60_000m, "sp1", "Large"), (request.Name, request.UnitPrice, request.Code, request.Description));
        Assert.Null(request.CategoryId);
        Assert.Null(request.Units);
    }

    [Fact]
    public void A_new_product_without_a_price_has_none_so_the_catalogue_refuses_it()
    {
        var request = SalesProductService.ToProductRequest(null, new SaveSalesProduct("sp1", "Oysters", null));

        Assert.Null(request.UnitPrice);
    }

    [Fact]
    public void An_edit_without_a_price_keeps_the_price_and_the_fields_the_form_does_not_have()
    {
        var existing = Existing();

        var request = SalesProductService.ToProductRequest(existing, new SaveSalesProduct("SP000007", "Oysters XL", "Bigger", RowVersion: "AAAA"));

        Assert.Equal(("Oysters XL", 55_000m, "AAAA", Category, "Sold by the cage"), (request.Name, request.UnitPrice, request.RowVersion, request.CategoryId, request.Note));
        Assert.Equal((10m, 500m, 8m, 12m), (request.MinKg, request.MaxKg, request.SizeMinCm, request.SizeMaxCm));
        Assert.Null(request.Units);
    }

    [Fact]
    public void A_price_of_zero_is_passed_on_as_it_is_so_the_catalogue_refuses_it()
    {
        var request = SalesProductService.ToProductRequest(Existing(), new SaveSalesProduct("SP000007", "Oysters", null, UnitPrice: 0m));

        Assert.Equal(0m, request.UnitPrice);
    }
}
