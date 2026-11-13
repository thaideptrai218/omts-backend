using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: §I.4.4 route by route, locked and revoked accounts, what each role's read shows.</summary>
public class SalesCatalogueQaAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly string StaffPassword = NewPassword();

    private async Task<(HttpClient Client, Account Account, AccessGrant Grant)> StaffAsync(StaffRole role, FarmingZoneRef zone)
    {
        var staff = await factory.AddAccountAsync(NewCode(), StaffPassword, SubjectType.Staff);
        var grant = await factory.GrantAsync(staff, role, zone.Zone);
        return (await factory.LoginAsync(staff.Username, StaffPassword), staff, grant);
    }

    private sealed record FarmingZoneRef(OMTS.Domain.Entities.Farming.FarmingZone? Zone);

    private async Task<FarmingZoneRef> ZoneAsync() => new(await factory.AddZoneAsync(NewCode()));

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Every route of the PR, with bodies that would succeed for the Owner.</summary>
    private static IEnumerable<(string Name, Func<Task<HttpResponseMessage>> Call)> Routes(HttpClient client, JsonElement product, JsonElement customer, JsonElement other)
    {
        yield return ("GET products", () => client.GetAsync("/api/products"));
        yield return ("GET product", () => client.GetAsync($"/api/products/{product.Id()}"));
        yield return ("POST product", () => client.PostAsJsonAsync("/api/products", new { name = NewCode(), unitPrice = 1 }));
        yield return ("PUT product", () => client.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = NewCode(), unitPrice = 1, rowVersion = product.RowVersion() }));
        yield return ("discontinue", () => client.PostAsync($"/api/products/{product.Id()}/discontinue", null));
        yield return ("resume", () => client.PostAsync($"/api/products/{product.Id()}/resume", null));
        yield return ("GET customers", () => client.GetAsync("/api/customers"));
        yield return ("GET customer", () => client.GetAsync($"/api/customers/{customer.Id()}"));
        yield return ("POST customer", () => client.PostAsJsonAsync("/api/customers", new { name = NewCode(), phoneNumber = NewPhone() }));
        yield return ("PUT customer", () => client.PutAsJsonAsync($"/api/customers/{customer.Id()}", new { name = NewCode(), phoneNumber = NewPhone(), rowVersion = customer.RowVersion() }));
        yield return ("preview", () => client.GetAsync($"/api/customers/{customer.Id()}/merge-preview?mergedCustomerId={other.Id()}"));
        yield return ("merge", () => client.PostMergeAsync(customer.Id(), other.Id(), customer.RowVersion(), other.RowVersion(), true));
    }

    [Fact]
    public async Task A_locked_zone_manager_gets_401_on_every_route_from_the_next_action()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (product, customer, other) = (await owner.CreateProductAsync(), await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync());
        var (manager, account, _) = await StaffAsync(StaffRole.ZoneManager, await ZoneAsync());
        await (await manager.GetAsync("/api/customers")).ReadAsync(HttpStatusCode.OK);

        await (await owner.PostAsJsonAsync($"/api/accounts/{account.Id}/lock", new { reason = "QA" })).ReadAsync(HttpStatusCode.OK);

        foreach (var (name, call) in Routes(manager, product, customer, other))
        {
            var response = await call();
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name}: {(int)response.StatusCode}");
        }

        Assert.Equal(customer.Text("name"), (await owner.CustomerAsync(customer.Id())).Text("name"));
    }

    // GFR-02: a grant or revoke applies from the account's next action, reads included.
    [Fact]
    public async Task A_revoked_pair_closes_products_and_customers_from_the_next_action()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (product, customer, other) = (await owner.CreateProductAsync(), await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync());
        var (manager, account, grant) = await StaffAsync(StaffRole.ZoneManager, await ZoneAsync());
        await (await manager.GetAsync("/api/products")).ReadAsync(HttpStatusCode.OK);

        await (await owner.PostAsJsonAsync($"/api/accounts/{account.Id}/grants/{grant.Id}/revoke", new { reason = "Moved" })).ReadAsync(HttpStatusCode.OK);

        foreach (var (name, call) in Routes(manager, product, customer, other))
        {
            var response = await call();
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"{name}: {(int)response.StatusCode}");
        }
    }

    // BR-FARM-08: the role in any zone decides; an Accountant pair in one zone adds nothing and takes nothing away.
    [Fact]
    public async Task Pairs_of_both_roles_read_and_sell_through_the_zone_manager_pair()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 41_000);
        var staff = await factory.AddAccountAsync(NewCode(), StaffPassword, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.Accountant, await factory.AddZoneAsync(NewCode()));
        await factory.GrantAsync(staff, StaffRole.ZoneManager, await factory.AddZoneAsync(NewCode()));
        var client = await factory.LoginAsync(staff.Username, StaffPassword);

        var read = await (await client.GetAsync($"/api/products/{product.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(41_000, read.GetProperty("unitPrice").GetDecimal());
        await client.CreateCustomerAsync();
        await (await client.PostAsJsonAsync("/api/products", new { name = NewCode(), unitPrice = 1 })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task An_accountant_of_one_zone_has_no_product_function_and_keeps_customers()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (product, customer, other) = (await owner.CreateProductAsync(), await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync());
        var (accountant, _, _) = await StaffAsync(StaffRole.Accountant, await ZoneAsync());

        foreach (var (name, call) in Routes(accountant, product, customer, other).Where(r => r.Name.Contains("product") || r.Name is "discontinue" or "resume"))
        {
            var response = await call();
            Assert.True(await response.CodeOrStatusAsync() == "403 MSG-GEN-08", $"{name}: {await response.Content.ReadAsStringAsync()}");
        }

        await (await accountant.GetAsync("/api/customers")).ReadAsync(HttpStatusCode.OK);
        await (await accountant.GetAsync($"/api/customers/{customer.Id()}")).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_zone_manager_and_a_trader_write_no_product_and_a_trader_no_customer_on_any_route()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (product, customer, other) = (await owner.CreateProductAsync(), await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync());
        var (manager, _, _) = await StaffAsync(StaffRole.ZoneManager, await ZoneAsync());
        var trader = await factory.AddAccountAsync(NewCode(), StaffPassword, SubjectType.Trader);
        var traderClient = await factory.LoginAsync(trader.Username, StaffPassword);

        foreach (var (name, call) in Routes(manager, product, customer, other).Where(r => r.Name is "POST product" or "PUT product" or "discontinue" or "resume"))
        {
            var response = await call();
            Assert.True(await response.CodeOrStatusAsync() == "403 MSG-GEN-08", name);
        }

        foreach (var (name, call) in Routes(traderClient, product, customer, other).Where(r => r.Name is not ("GET products" or "GET product")))
        {
            var response = await call();
            Assert.True(await response.CodeOrStatusAsync() == "403 MSG-GEN-08", name);
        }
    }

    // BR-ACCOUNT-03: no other function until the number is verified; BR-ACCOUNT-06 likewise for a temporary password.
    [Fact]
    public async Task The_account_state_gate_closes_the_new_routes_too()
    {
        var unverified = await factory.AddAccountAsync(NewCode(), StaffPassword, SubjectType.Trader, a => { a.PhoneNumber = null; a.PhoneVerifiedAt = null; });
        var trader = await factory.LoginAsync(unverified.Username, StaffPassword);
        await (await trader.GetAsync("/api/products")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-ACCOUNT-04");

        var temporary = await factory.AddAccountAsync(NewCode(), StaffPassword, SubjectType.Staff, a => { a.MustChangePassword = true; a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow; });
        await factory.GrantAsync(temporary, StaffRole.ZoneManager, await factory.AddZoneAsync(NewCode()));
        var staff = await factory.LoginAsync(temporary.Username, StaffPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/products")).StatusCode);
    }

    // The trader's own profile is no exception: SALE-04 is No for the Trader in §I.4.4.
    [Fact]
    public async Task A_trader_cannot_open_its_own_profile_through_the_staff_routes()
    {
        var (trader, profile) = await factory.LinkedProfileAsync();
        var client = await factory.LoginAsync(trader.Username);

        await (await client.GetAsync($"/api/customers/{profile.Id}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    // The System Administrator type is gone (BR-ACCOUNT-02); the account with no right now is a staff account with no pair,
    // which logs in to nothing (MSG-ACCOUNT-07), so it never holds a token for these routes.
    [Fact]
    public async Task A_staff_account_without_a_pair_has_neither_function()
    {
        var staff = await factory.AddAccountAsync(NewCode(), StaffPassword, SubjectType.Staff);
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = StaffPassword });

        await login.ProblemAsync(HttpStatusCode.Forbidden, "MSG-ACCOUNT-07");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/customers")).StatusCode);
    }

    // GFR-02: a role with no right gets MSG-GEN-08, whatever it sends. The customer routes do; the product reads check later.
    [Fact]
    public async Task L6_a_role_without_product_rights_gets_msg_gen_08_whatever_the_filter()
    {
        var (accountant, _, _) = await StaffAsync(StaffRole.Accountant, await ZoneAsync());

        await (await accountant.GetAsync("/api/products?statusId=abc")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task The_generated_api_description_holds_the_new_routes()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");

        var body = await response.ReadAsync(HttpStatusCode.OK);
        Assert.True(body.GetProperty("paths").TryGetProperty("/api/customers/{id}/merge", out _));
        Assert.True(body.GetProperty("paths").TryGetProperty("/api/products/{id}/resume", out _));
    }
}
