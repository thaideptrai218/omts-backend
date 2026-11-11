using System.Net;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SCR-SALE-01 and SCR-SALE-02: what a trader and staff read, in which order; a merge re-points the requests (BR-SALE-04).</summary>
public class PurchaseRequestReadTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static IEnumerable<Guid> RequestIds(System.Text.Json.JsonElement page) =>
        page.GetProperty("requests").EnumerateArray().Select(r => r.Id());

    // SALE-10.0.E1.
    [Fact]
    public async Task A_trader_with_nothing_gets_msg_gen_11()
    {
        var trader = await factory.TraderAsync();

        var page = await (await trader.Client.GetAsync("/api/me/purchases")).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(page.GetProperty("requests").EnumerateArray());
        Assert.Equal((0, "MSG-GEN-11", "There is nothing to show yet."), (page.GetProperty("total").GetInt32(), page.Text("messageCode"), page.Text("message")));
    }

    [Fact]
    public async Task My_purchases_hold_the_traders_own_requests_newest_first_in_pages_with_no_message()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        var first = await trader.Client.PlaceAsync(product.Id());
        await Task.Delay(5);
        var second = await trader.Client.PlaceAsync(product.Id());
        await Task.Delay(5);
        var third = await trader.Client.PlaceAsync(product.Id());
        await (await factory.TraderAsync()).Client.PlaceAsync(product.Id());

        var page = await (await trader.Client.GetAsync("/api/me/purchases?page=1&pageSize=2")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([third.Id(), second.Id()], RequestIds(page));
        Assert.Equal((3, 2), (page.GetProperty("total").GetInt32(), page.GetProperty("pageSize").GetInt32()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, page.GetProperty("messageCode").ValueKind);

        var last = await (await trader.Client.GetAsync("/api/me/purchases?page=2&pageSize=2")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([first.Id()], RequestIds(last));
        Assert.Equal(HttpStatusCode.OK, (await trader.Client.GetAsync("/api/me/purchases?page=2147483647&pageSize=2147483647")).StatusCode);
    }

    // BR-ACCOUNT-04: no other customer's data, no internal field, no price.
    [Fact]
    public async Task A_trader_reads_exactly_the_fields_of_the_screen()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(product.Id());
        (await owner.AcceptAsync(request, payLater: true)).EnsureSuccessStatusCode();

        var view = await trader.Client.MineAsync(request.Id());

        Assert.Equal(
            new[] { "cancelReason", "cancelledAt", "canCancel", "code", "fulfilled", "harvestCompletedAt", "id", "note", "pickupDate", "productId", "productName", "requestedCages", "response", "rowVersion", "sentAt", "status", "statusId" }.Order(),
            view.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(
            new[] { "acceptedCages", "note", "payLaterAgreed", "pickupDate", "respondedAt" }.Order(),
            view.GetProperty("response").EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Staff_read_the_customer_with_its_debt_awaiting_requests_first_and_may_filter()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync("Chị Hoa");
        var answered = await trader.Client.PlaceAsync(product.Id());
        await Task.Delay(5);
        var awaiting = await trader.Client.PlaceAsync(product.Id());
        await Task.Delay(5);
        var newest = await trader.Client.PlaceAsync(product.Id());
        (await owner.DeclineAsync(newest)).EnsureSuccessStatusCode();
        var other = await factory.TraderAsync();
        var ofOther = await other.Client.PlaceAsync(product.Id());

        var ofCustomer = await (await owner.GetAsync($"/api/purchase-requests?customerId={trader.Profile.Id}")).ReadAsync(HttpStatusCode.OK);
        var ids = ofCustomer.GetProperty("items").EnumerateArray().Select(r => r.Id()).ToList();
        Assert.Equal([awaiting.Id(), answered.Id(), newest.Id()], ids);
        var row = ofCustomer.GetProperty("items")[0];
        var customer = row.GetProperty("customer");
        Assert.Equal(("Chị Hoa", trader.Profile.PhoneNumber, 0), (customer.Text("name"), customer.Text("phoneNumber"), customer.GetProperty("currentDebt").GetDecimal()));
        Assert.Equal(trader.Profile.Id, customer.Id());

        var declined = await (await owner.GetAsync($"/api/purchase-requests?customerId={trader.Profile.Id}&statusId={StatusIds.PurchaseRequest.Declined}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([newest.Id()], declined.GetProperty("items").EnumerateArray().Select(r => r.Id()));
        var unknown = await (await owner.GetAsync("/api/purchase-requests?statusId=1")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0, unknown.GetProperty("total").GetInt32());
        var all = await (await owner.GetAsync("/api/purchase-requests?pageSize=100")).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(all.GetProperty("items").EnumerateArray(), r => r.Id() == ofOther.Id());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/purchase-requests/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/purchase-requests?statusId=abc")).StatusCode);
    }

    // PRE-2 of SALE-02, BR-SALE-04: requests of a profile merged into the trader's count as the trader's. They reach an offline
    // profile only in data older than the link rule, so the row is written directly.
    [Fact]
    public async Task A_merge_moves_the_requests_to_the_kept_profile_where_the_trader_reads_and_cancels_them()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        var offline = await owner.CreateCustomerAsync();
        var row = new PurchaseRequest { CustomerId = offline.Id(), ProductId = product.Id(), RequestedCages = 10, PickupDate = PurchaseRequestTestKit.Today, SentAt = DateTimeOffset.UtcNow };
        await factory.UseDbAsync(db => db.PurchaseRequests.Add(row));

        var preview = await (await owner.GetAsync($"/api/customers/{trader.Profile.Id}/merge-preview?mergedCustomerId={offline.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, preview.GetProperty("countedWithKept").EnumerateArray().Single(c => c.Text("name") == "Purchase requests").GetProperty("count").GetInt32());

        (await owner.MergeAsync(trader.Profile.Id, offline.Id())).EnsureSuccessStatusCode();

        var mine = await (await trader.Client.GetAsync("/api/me/purchases")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([row.Id], RequestIds(mine));
        var view = await trader.Client.MineAsync(row.Id);
        (await trader.Client.CancelAsync(view)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await (await owner.GetAsync($"/api/purchase-requests?customerId={offline.Id()}")).ReadAsync(HttpStatusCode.OK)).GetProperty("total").GetInt32());
    }
}
