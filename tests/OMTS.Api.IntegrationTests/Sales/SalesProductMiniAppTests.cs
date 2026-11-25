using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Products.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The Mini App writes products through POST and PUT api/sales-products with the rules of the product catalogue (BR-SALE-05): a price above 0, a unique name and code, a deleted product is not found,
/// and only an In Use product is edited.
/// </summary>
public sealed class SalesProductMiniAppTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Route = "/api/sales-products";

    private async Task<HttpClient> OwnerAsync()
    {
        var account = await factory.AddAccountAsync(NewCode(), Password);
        return await factory.LoginAsync(account.Username);
    }

    private static SaveSalesProduct Body(string? name = null, decimal? price = 60_000m) =>
        new(NewCode(), name ?? "Oysters " + NewCode(), "Large", UnitPrice: price);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_product_is_not_saved_without_a_price_above_zero(int? price)
    {
        var owner = await OwnerAsync();

        var response = await owner.PostAsJsonAsync(Route, Body(price: price));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_saved_product_has_its_price_and_a_second_product_cannot_take_its_name()
    {
        var owner = await OwnerAsync();
        var first = Body();

        var created = await owner.PostAsJsonAsync(Route, first);
        var again = await owner.PostAsJsonAsync(Route, Body(first.Name));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(60_000m, await factory.QueryAsync(db => db.Products.AsNoTracking().Where(p => p.Name == first.Name).Select(p => p.UnitPrice).SingleAsync()));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("MSG-GEN-02", await again.ErrorCodeAsync());
    }

    [Fact]
    public async Task An_edit_without_a_price_keeps_the_price()
    {
        var owner = await OwnerAsync();
        var created = (await (await owner.PostAsJsonAsync(Route, Body())).Content.ReadFromJsonAsync<SalesProductView>())!;

        var edited = await owner.PutAsJsonAsync($"{Route}/{created.Id}", new SaveSalesProduct(created.Code, created.Name + " XL", "Larger", RowVersion: created.RowVersion));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var product = await factory.QueryAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id));
        Assert.Equal((60_000m, created.Name + " XL"), (product.UnitPrice, product.Name));
    }

    [Fact]
    public async Task A_deleted_product_is_not_found_and_stays_hidden()
    {
        var owner = await OwnerAsync();
        var hidden = new Product { Code = NewCode(), Name = "Deleted " + NewCode(), UnitPrice = 50_000m, IsHidden = true, StatusId = StatusIds.Usage.Discontinued };
        await factory.UseDbAsync(db => db.Products.Add(hidden));

        var response = await owner.PutAsJsonAsync($"{Route}/{hidden.Id}", new SaveSalesProduct(hidden.Code, hidden.Name, null, InUse: true, UnitPrice: 50_000m));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var stored = await factory.QueryAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Id == hidden.Id));
        Assert.Equal((true, StatusIds.Usage.Discontinued), (stored.IsHidden, stored.StatusId));
    }
}
