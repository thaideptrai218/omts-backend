using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// §I.4.4: SALE-11 Owner only; SALE-04 Owner and Zone Manager (no zone, BR-FARM-08), Accountant and Trader no. Who sells or
/// sends requests reads the products offered (SALE-05 step 1, SALE-01 step 1).
/// </summary>
public class SalesCatalogueAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<HttpClient> ZoneManagerAsync() => await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));

    private async Task<HttpClient> TraderAsync() =>
        await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username);

    [Fact]
    public async Task Only_the_owner_writes_products_and_prices()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 60_000);

        foreach (var client in new[] { await ZoneManagerAsync(), await factory.AccountantAsync(), await TraderAsync() })
        {
            await (await client.PostAsJsonAsync("/api/products", new { name = NewCode(), unitPrice = 1 })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await client.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = "Cheaper", unitPrice = 1, rowVersion = product.RowVersion() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await client.PostAsync($"/api/products/{product.Id()}/discontinue", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await client.PostAsync($"/api/products/{product.Id()}/resume", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        var stored = await owner.GetAsync($"/api/products/{product.Id()}");
        Assert.Equal(60_000, (await stored.ReadAsync(HttpStatusCode.OK)).GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task The_owner_lists_every_product_and_may_filter_by_status_and_name()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var inUse = await owner.CreateProductAsync();
        var off = await owner.CreateProductAsync();
        (await owner.PostAsync($"/api/products/{off.Id()}/discontinue", null)).EnsureSuccessStatusCode();

        var all = await (await owner.GetAsync("/api/products")).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(all.EnumerateArray(), p => p.Id() == off.Id());
        Assert.Contains(all.EnumerateArray(), p => p.Id() == inUse.Id());

        var discontinued = await (await owner.GetAsync($"/api/products?statusId={StatusIds.Usage.Discontinued}")).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(discontinued.EnumerateArray(), p => p.Id() == off.Id());
        Assert.DoesNotContain(discontinued.EnumerateArray(), p => p.Id() == inUse.Id());

        var byName = await (await owner.GetAsync($"/api/products?search={off.Text("name")}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(off.Id(), Assert.Single(byName.EnumerateArray()).Id());
    }

    // SALE-05 step 1: the products In Use with their list prices. A Discontinued one is only viewed by id (GBR-01).
    [Fact]
    public async Task A_zone_manager_reads_only_products_in_use_with_their_prices_whatever_the_filter()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var inUse = await owner.CreateProductAsync(unitPrice: 55_000);
        var off = await owner.CreateProductAsync();
        (await owner.PostAsync($"/api/products/{off.Id()}/discontinue", null)).EnsureSuccessStatusCode();
        var manager = await ZoneManagerAsync();

        var list = await (await manager.GetAsync($"/api/products?statusId={StatusIds.Usage.Discontinued}")).ReadAsync(HttpStatusCode.OK);

        Assert.DoesNotContain(list.EnumerateArray(), p => p.Id() == off.Id());
        Assert.Equal(55_000, list.EnumerateArray().Single(p => p.Id() == inUse.Id()).GetProperty("unitPrice").GetDecimal());
        Assert.Equal(StatusIds.Usage.Discontinued, (await (await manager.GetAsync($"/api/products/{off.Id()}")).ReadAsync(HttpStatusCode.OK)).GetProperty("statusId").GetInt32());
    }

    // SALE-01 step 1 and BR-SALE-01: the trader chooses among those In Use, and a request has no price.
    [Fact]
    public async Task A_trader_reads_products_in_use_with_no_price()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var inUse = await owner.CreateProductAsync();
        var off = await owner.CreateProductAsync();
        (await owner.PostAsync($"/api/products/{off.Id()}/discontinue", null)).EnsureSuccessStatusCode();
        var trader = await TraderAsync();

        var list = await (await trader.GetAsync("/api/products")).ReadAsync(HttpStatusCode.OK);

        Assert.DoesNotContain(list.EnumerateArray(), p => p.Id() == off.Id());
        var row = list.EnumerateArray().Single(p => p.Id() == inUse.Id());
        Assert.False(row.TryGetProperty("unitPrice", out var price) && price.ValueKind != System.Text.Json.JsonValueKind.Null);
        var one = await (await trader.GetAsync($"/api/products/{inUse.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.False(one.TryGetProperty("unitPrice", out price) && price.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task An_accountant_cannot_read_products()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var accountant = await factory.AccountantAsync();

        await (await accountant.GetAsync("/api/products")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.GetAsync($"/api/products/{product.Id()}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task Anonymous_callers_get_401_on_products_and_customers()
    {
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/products", new { name = "X", unitPrice = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/customers/{Guid.NewGuid()}/merge", new { })).StatusCode);
    }

    [Fact]
    public async Task An_accountant_or_a_trader_has_no_customer_function()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var customer = await owner.CreateCustomerAsync();
        var other = await owner.CreateCustomerAsync();

        foreach (var client in new[] { await factory.AccountantAsync(), await TraderAsync() })
        {
            await (await client.GetAsync("/api/customers")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await client.GetAsync($"/api/customers/{customer.Id()}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await client.PostAsJsonAsync("/api/customers", new { name = "X", phoneNumber = NewPhone() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await client.PutAsJsonAsync($"/api/customers/{customer.Id()}", new { name = "X", phoneNumber = NewPhone(), rowVersion = customer.RowVersion() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await client.GetAsync($"/api/customers/{customer.Id()}/merge-preview?mergedCustomerId={other.Id()}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await client.PostMergeAsync(customer.Id(), other.Id(), customer.RowVersion(), other.RowVersion(), true)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }
    }

    // BR-FARM-08: profiles have no zone, so a pair in one zone opens all of them, whoever created them.
    [Fact]
    public async Task A_zone_manager_of_one_zone_works_on_every_profile()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var customer = await owner.CreateCustomerAsync();
        var manager = await ZoneManagerAsync();

        await (await manager.GetAsync($"/api/customers/{customer.Id()}")).ReadAsync(HttpStatusCode.OK);
        var edit = await manager.PutAsJsonAsync($"/api/customers/{customer.Id()}", new
        {
            name = "Edited", phoneNumber = customer.Text("phoneNumber"), address = "Cái Rồng", rowVersion = customer.RowVersion(),
        });
        await edit.ReadAsync(HttpStatusCode.OK);
        await manager.CreateCustomerAsync();
    }
}
