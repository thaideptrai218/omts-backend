using System.Net.Http.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Sales.Products.Dtos;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Application.Features.Sales;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-01/02/03. User correction 31/10/2026: ordering uses whole cages, never kg.</summary>
public sealed class PurchaseRequestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);

    [Fact]
    public async Task Submit_persists_cages_owner_can_receive_and_retry_does_not_duplicate()
    {
        var (trader, customer, product) = await BuyerAsync();
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var input = Input(product);
        var response = await trader.PostAsJsonAsync("/api/me/purchase-requests", input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = (await response.Content.ReadFromJsonAsync<PurchaseRequestView>())!;
        Assert.Equal("Cage", row.QuantityUnit); Assert.Equal(12, row.RequestedCages); Assert.Equal("AWAITING_RESPONSE", row.Status);
        Assert.EndsWith(row.Id.ToString(), response.Headers.Location!.ToString());
        var saved = await factory.QueryAsync(db => db.Set<PurchaseRequest>().SingleAsync(r => r.Id == row.Id));
        Assert.Equal(customer.Id, saved.CustomerId); Assert.Equal(product.Name, saved.ProductName);
        Assert.True(await factory.QueryAsync(db => db.Set<Notification>().AnyAsync(n => n.RecipientAccountId == owner.Id && n.RelatedRecordCode == row.Code && n.Type == "NEW_ORDER")));
        // Retrying is safe even after a catalogue change: return the original persisted operation.
        await factory.UpdateAsync<Product>(product.Id, p => { p.StatusId = StatusIds.Usage.Discontinued; p.Name = "Renamed"; });
        var replay = await trader.PostAsJsonAsync("/api/me/purchase-requests", input);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(row.Id, (await replay.Content.ReadFromJsonAsync<PurchaseRequestView>())!.Id);
        Assert.Equal(1, await factory.QueryAsync(db => db.Set<PurchaseRequest>().CountAsync(r => r.CustomerId == customer.Id)));
        Assert.Equal(1, await factory.QueryAsync(db => db.Set<Notification>().CountAsync(n => n.RecipientAccountId == owner.Id && n.RelatedRecordCode == row.Code && n.Type == "NEW_ORDER")));
        Assert.Equal(HttpStatusCode.BadRequest, (await trader.PostAsJsonAsync("/api/me/purchase-requests", input with { RequestedCages = 13 })).StatusCode);
        var list = await trader.GetFromJsonAsync<PurchaseRequestPage>("/api/me/purchase-requests");
        Assert.Equal(row.ProductName, Assert.Single(list!.Items).ProductName);
        var manager = await factory.LoginAsync(owner.Username);
        var inbox = await manager.GetFromJsonAsync<PurchaseRequestPage>("/api/purchase-requests?status=AWAITING_RESPONSE");
        Assert.Contains(inbox!.Items, r => r.Id == row.Id);
        // Requesting does not fabricate a weighed sale or invoice.
        Assert.False(await factory.QueryAsync(db => db.SalesOrders.AnyAsync(o => o.CustomerId == customer.Id)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(1.5)]
    [InlineData(2147483648d)]
    public async Task Api_rejects_non_positive_fractional_or_overflow_cage_counts(double cages)
    {
        var (trader, _, product) = await BuyerAsync();
        var response = await trader.PostAsJsonAsync("/api/me/purchase-requests", new { productId = product.Id, requestedCages = cages, pickupDate = Today, clientOperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Api_rejects_unavailable_products_past_date_and_missing_operation()
    {
        var (trader, _, product) = await BuyerAsync();
        var input = Input(product);
        foreach (var invalid in new[] { input with { ProductId = Guid.NewGuid() }, input with { PickupDate = Today.AddDays(-1) }, input with { ClientOperationId = Guid.Empty }, input with { Note = new string('x', 501) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await trader.PostAsJsonAsync("/api/me/purchase-requests", invalid)).StatusCode);
        await factory.UpdateAsync<Product>(product.Id, p => p.StatusId = StatusIds.Usage.Discontinued);
        Assert.Equal(HttpStatusCode.BadRequest, (await trader.PostAsJsonAsync("/api/me/purchase-requests", input)).StatusCode);
        await factory.UpdateAsync<Product>(product.Id, p => { p.StatusId = StatusIds.Usage.InUse; p.ValidFrom = Today.AddDays(1); });
        Assert.Equal(HttpStatusCode.BadRequest, (await trader.PostAsJsonAsync("/api/me/purchase-requests", input)).StatusCode);
        await factory.UpdateAsync<Product>(product.Id, p => { p.ValidFrom = null; p.ValidTo = Today.AddDays(-1); });
        Assert.Equal(HttpStatusCode.BadRequest, (await trader.PostAsJsonAsync("/api/me/purchase-requests", input)).StatusCode);
    }

    [Fact]
    public async Task Trader_can_only_read_cancel_their_own_and_cannot_respond_or_pick_another_customer()
    {
        var (trader, customer, product) = await BuyerAsync(); var (other, otherCustomer, _) = await BuyerAsync();
        var response = await trader.PostAsJsonAsync("/api/me/purchase-requests", new { productId = product.Id, requestedCages = 3, pickupDate = Today, clientOperationId = Guid.NewGuid(), customerId = otherCustomer.Id });
        var row = (await response.Content.ReadFromJsonAsync<PurchaseRequestView>())!;
        Assert.Equal(customer.Id, await factory.QueryAsync(db => db.Set<PurchaseRequest>().Where(r => r.Id == row.Id).Select(r => r.CustomerId).SingleAsync()));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/me/purchase-requests/{row.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"/api/me/purchase-requests/{row.Id}/cancel", new CancelPurchaseRequest(null, row.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync("/api/purchase-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/response", Accept(row))).StatusCode);
        var others = await other.GetFromJsonAsync<PurchaseRequestPage>("/api/me/purchase-requests"); Assert.Empty(others!.Items);
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/me/purchase-requests", Input(product))).StatusCode);
    }

    [Fact]
    public async Task Owner_accepts_cages_and_date_trader_reads_persisted_response_and_can_cancel()
    {
        var (trader, _, product) = await BuyerAsync(); var row = await SubmitAsync(trader, product);
        var owner = await OwnerClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/me/purchase-requests", Input(product))).StatusCode);
        var reply = Accept(row) with { AcceptedCages = 8, PickupDate = Today.AddDays(2), AllowPayLater = true, Note = "Morning" };
        var response = await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/response", reply);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var confirmed = (await trader.GetFromJsonAsync<PurchaseRequestView>($"/api/me/purchase-requests/{row.Id}"))!;
        Assert.Equal("ACCEPTED", confirmed.Status); Assert.Equal(8, confirmed.AcceptedCages); Assert.Equal(Today.AddDays(2), confirmed.AcceptedPickupDate); Assert.True(confirmed.AllowPayLater);
        Assert.True(await factory.QueryAsync(db => db.Set<Notification>().AnyAsync(n => n.RelatedRecordCode == row.Code && n.Type == "PURCHASE_REQUEST_RESPONDED")));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/response", reply)).StatusCode);
        var cancelled = await trader.PostAsJsonAsync($"/api/me/purchase-requests/{row.Id}/cancel", new CancelPurchaseRequest("Changed pickup", confirmed.RowVersion));
        Assert.Equal("CANCELLED", (await cancelled.Content.ReadFromJsonAsync<PurchaseRequestView>())!.Status);
        Assert.Equal(HttpStatusCode.OK, (await trader.PostAsJsonAsync($"/api/me/purchase-requests/{row.Id}/cancel", new CancelPurchaseRequest(null, confirmed.RowVersion))).StatusCode);
        var ownerId = await factory.QueryAsync(db => db.Set<PurchaseRequest>().Where(r => r.Id == row.Id).Select(r => r.RespondedByAccountId!.Value).SingleAsync());
        Assert.Equal(1, await factory.QueryAsync(db => db.Set<Notification>().CountAsync(n => n.RelatedRecordCode == row.Code && n.Type == "PURCHASE_REQUEST_CANCELLED" && n.RecipientAccountId == ownerId)));
    }

    [Fact]
    public async Task Owner_cannot_accept_more_than_requested_zero_past_pickup_or_decline_without_reason()
    {
        var (trader, _, product) = await BuyerAsync(); var row = await SubmitAsync(trader, product); var owner = await OwnerClientAsync();
        var valid = Accept(row);
        foreach (var invalid in new[] { valid with { AcceptedCages = 13 }, valid with { AcceptedCages = 0 }, valid with { AcceptedCages = null }, valid with { PickupDate = Today.AddDays(-1) }, valid with { PickupDate = null }, valid with { Accept = false, Note = " " }, valid with { RowVersion = null }, valid with { RowVersion = "invalid!" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/response", invalid)).StatusCode);
        var declined = await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/response", valid with { Accept = false, Note = "Unavailable" });
        var result = (await declined.Content.ReadFromJsonAsync<PurchaseRequestView>())!;
        Assert.Equal("DECLINED", result.Status); Assert.Equal("Unavailable", result.ResponseNote); Assert.Null(result.AcceptedCages);
        Assert.Equal(HttpStatusCode.BadRequest, (await trader.PostAsJsonAsync($"/api/me/purchase-requests/{row.Id}/cancel", new CancelPurchaseRequest(null, result.RowVersion))).StatusCode);
    }

    [Fact]
    public async Task Cancelled_and_stale_requests_cannot_be_accepted()
    {
        var (trader, _, product) = await BuyerAsync(); var row = await SubmitAsync(trader, product); var owner = await OwnerClientAsync();
        await factory.UpdateAsync<PurchaseRequest>(row.Id, r => r.RowVersion = [1]);
        var stale = await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/response", Accept(row) with { RowVersion = Convert.ToBase64String([2]) });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        var current = (await trader.GetFromJsonAsync<PurchaseRequestView>($"/api/me/purchase-requests/{row.Id}"))!;
        Assert.Equal(HttpStatusCode.OK, (await trader.PostAsJsonAsync($"/api/me/purchase-requests/{row.Id}/cancel", new CancelPurchaseRequest(null, current.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/response", Accept(current))).StatusCode);
    }

    [Fact]
    public async Task Staff_with_view_can_read_but_only_owner_can_respond_and_filters_validate()
    {
        var (trader, _, product) = await BuyerAsync(); var row = await SubmitAsync(trader, product);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var role = await factory.AddRoleAsync(NewCode(), (PermissionFunctions.PurchaseRequests, true, true, true));
        await factory.GrantAsync(staff, role, null); var client = await factory.LoginAsync(staff.Username);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/purchase-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/response", Accept(row))).StatusCode);
        foreach (var query in new[] { "page=0", "pageSize=101", "pageSize=0", "status=bogus", "page=2147483647" })
            Assert.Equal(HttpStatusCode.BadRequest, (await trader.GetAsync("/api/me/purchase-requests?" + query)).StatusCode);
        var page = await trader.GetFromJsonAsync<PurchaseRequestPage>("/api/me/purchase-requests?status=AWAITING_RESPONSE&pageSize=1");
        Assert.Equal(1, page!.Total); Assert.Single(page.Items);
        Assert.Empty((await trader.GetFromJsonAsync<PurchaseRequestPage>("/api/me/purchase-requests?status=DECLINED"))!.Items);
    }

    [Fact]
    public async Task Catalogue_is_real_owner_managed_and_traders_only_see_active_products()
    {
        var owner = await OwnerClientAsync(); var code = NewCode(); var body = new SaveSalesProduct(code, "Oyster cages", "Large oysters");
        var created = await owner.PostAsJsonAsync("/api/sales-products", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var product = (await created.Content.ReadFromJsonAsync<SalesProductView>())!;
        var (trader, _, _) = await BuyerAsync();
        Assert.Contains((await trader.GetFromJsonAsync<List<SalesProductView>>("/api/sales-products"))!, p => p.Id == product.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.PostAsJsonAsync("/api/sales-products", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync("/api/sales-products?includeUnavailable=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/sales-products", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/sales-products/{product.Id}", body with { InUse = false, RowVersion = product.RowVersion })).StatusCode);
        Assert.DoesNotContain((await trader.GetFromJsonAsync<List<SalesProductView>>("/api/sales-products"))!, p => p.Id == product.Id);
        Assert.Contains((await owner.GetFromJsonAsync<List<SalesProductView>>("/api/sales-products?includeUnavailable=true"))!, p => p.Id == product.Id);
    }

    private async Task<(HttpClient Client, Customer Customer, Product Product)> BuyerAsync()
    {
        var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var cooperative = await factory.EnsureCooperativeAsync();
        var customer = new Customer { AccountId = account.Id, CooperativeId = cooperative.Id, Name = account.DisplayName };
        var product = new Product { Code = NewCode(), Name = "Oysters " + NewCode() };
        await factory.UseDbAsync(db => { db.Customers.Add(customer); db.Products.Add(product); });
        return (await factory.LoginAsync(account.Username), customer, product);
    }
    private async Task<HttpClient> OwnerClientAsync()
    {
        var account = await factory.AddAccountAsync(NewCode(), Password); return await factory.LoginAsync(account.Username);
    }
    private static CreatePurchaseRequest Input(Product product) => new(product.Id, 12, Today.AddDays(1), "Morning pickup", Guid.NewGuid());
    private static RespondPurchaseRequest Accept(PurchaseRequestView row) => new(true, row.RequestedCages, row.RequestedPickupDate, false, null, row.RowVersion);
    private static async Task<PurchaseRequestView> SubmitAsync(HttpClient trader, Product product)
    {
        var response = await trader.PostAsJsonAsync("/api/me/purchase-requests", Input(product));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<PurchaseRequestView>())!;
    }
}
