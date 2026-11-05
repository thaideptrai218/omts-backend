using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: requests and a merge (SALE-04.3, BR-SALE-04, SALE-02 PRE-2, CustomerReferences).</summary>
public class PurchaseRequestQaMergeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<PurchaseRequest> RowOnAsync(Guid customerId, Guid productId)
    {
        var row = new PurchaseRequest { CustomerId = customerId, ProductId = productId, RequestedKg = 10, PickupDate = PurchaseRequestTestKit.Today, SentAt = DateTimeOffset.UtcNow };
        await factory.UseDbAsync(db => db.PurchaseRequests.Add(row));
        return row;
    }

    private static int CountOf(JsonElement preview, string name) =>
        preview.GetProperty("countedWithKept").EnumerateArray().Single(c => c.Text("name") == name).GetProperty("count").GetInt32();

    // SALE-04.3 step 3: the preview counts what will move, also the requests a profile got from an earlier merge.
    [Fact]
    public async Task The_preview_counts_the_requests_of_the_profile_to_merge_and_of_those_merged_into_it()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var productId = (await owner.CreateProductAsync()).Id();
        var (_, kept) = await factory.LinkedProfileAsync();
        var middle = await owner.CreateCustomerAsync();
        var earlier = await owner.CreateCustomerAsync();
        await RowOnAsync(middle.Id(), productId);
        await RowOnAsync(earlier.Id(), productId);
        await RowOnAsync(earlier.Id(), productId);

        var before = await (await owner.GetAsync($"/api/customers/{kept.Id}/merge-preview?mergedCustomerId={middle.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, CountOf(before, "Purchase requests"));

        (await owner.MergeAsync(middle.Id(), earlier.Id())).EnsureSuccessStatusCode();

        var after = await (await owner.GetAsync($"/api/customers/{kept.Id}/merge-preview?mergedCustomerId={middle.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((3, 1), (CountOf(after, "Purchase requests"), CountOf(after, "Merged customer profiles")));

        // A Zone Manager is asked with the same count (MSG-SALE-04).
        var manager = await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));
        var asked = await (await manager.MergeAsync(kept.Id, middle.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-04");
        Assert.Equal(3, CountOf(asked, "Purchase requests"));
    }

    // SALE-04 POST-2: only the trader of the kept profile sees them. Another trader never does, and the merged number stays staff's.
    [Fact]
    public async Task After_a_merge_the_trader_of_the_kept_profile_reads_and_cancels_the_requests_and_nobody_else()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var productId = (await owner.CreateProductAsync()).Id();
        var trader = await factory.TraderAsync();
        var other = await factory.TraderAsync();
        var offline = await owner.CreateCustomerAsync();
        var row = await RowOnAsync(offline.Id(), productId);

        (await owner.MergeAsync(trader.Profile.Id, offline.Id())).EnsureSuccessStatusCode();

        var list = await trader.Client.GetAsync("/api/me/purchases");
        Assert.DoesNotContain(offline.Text("phoneNumber")!, await list.Content.ReadAsStringAsync());
        Assert.Contains((await list.ReadAsync(HttpStatusCode.OK)).GetProperty("requests").EnumerateArray(), r => r.Id() == row.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/me/purchase-requests/{row.Id}")).StatusCode);
        Assert.Equal(0, (await (await other.Client.GetAsync("/api/me/purchases")).ReadAsync(HttpStatusCode.OK)).GetProperty("total").GetInt32());
        Assert.Equal(0, (await (await owner.GetAsync($"/api/purchase-requests?customerId={offline.Id()}")).ReadAsync(HttpStatusCode.OK)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await (await owner.GetAsync($"/api/purchase-requests?customerId={trader.Profile.Id}")).ReadAsync(HttpStatusCode.OK)).GetProperty("total").GetInt32());

        var audit = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "Customer.Merged" && e.RecordId == offline.Id()));
        Assert.Contains(row.Id.ToString(), audit.After);

        var mine = await trader.Client.MineAsync(row.Id);
        (await trader.Client.CancelAsync(mine)).EnsureSuccessStatusCode();
    }

    // BR-SALE-03: a moved request answers to the kept profile: its account and its number, never the merged number.
    [Fact]
    public async Task The_response_to_a_moved_request_goes_to_the_kept_account_and_number()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var productId = (await owner.CreateProductAsync()).Id();
        var trader = await factory.TraderAsync();
        var offline = await owner.CreateCustomerAsync();
        var row = await RowOnAsync(offline.Id(), productId);
        (await owner.MergeAsync(trader.Profile.Id, offline.Id())).EnsureSuccessStatusCode();

        var answer = await (await owner.AcceptAsync(await owner.ViewedByStaffAsync(row.Id), 5m)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(trader.Profile.Id, answer.GetProperty("customer").GetProperty("id").GetGuid());
        var rows = await factory.NotificationRowsAsync(row.Id);
        Assert.Equal(trader.Account.Id, rows.InList().OfType(NotificationTypes.PurchaseRequestResponse).Single().RecipientAccountId);
        Assert.Equal(trader.Profile.PhoneNumber, rows.Sendings().Single().PhoneNumber);
        var notification = rows.InList().OfType(NotificationTypes.PurchaseRequestResponse).Single();
        Assert.Equal(HttpStatusCode.OK, (await trader.Client.GetAsync($"/api/me/notifications/{notification.Id}/open")).StatusCode);
    }

    // The merge of a linked profile is refused, so a request of a trader never leaves its account.
    [Fact]
    public async Task A_profile_with_a_trader_and_requests_cannot_be_merged_away_and_its_requests_stay()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync((await owner.CreateProductAsync()).Id());
        var offline = await owner.CreateCustomerAsync();

        await (await owner.MergeAsync(offline.Id(), trader.Profile.Id)).ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-05");

        Assert.Equal(StatusIds.PurchaseRequest.AwaitingResponse, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
    }

    // Unreachable today (a merged profile never had an account, so never a request), pinned: no account means no notification and no SMS row.
    [Fact]
    public async Task A_moved_request_on_a_kept_profile_with_no_account_is_answered_and_nothing_is_sent()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var productId = (await owner.CreateProductAsync()).Id();
        var kept = await owner.CreateCustomerAsync();
        var offline = await owner.CreateCustomerAsync();
        var row = await RowOnAsync(offline.Id(), productId);
        (await owner.MergeAsync(kept.Id(), offline.Id())).EnsureSuccessStatusCode();

        var answer = await (await owner.AcceptAsync(await owner.ViewedByStaffAsync(row.Id), 5m)).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(answer.GetProperty("sendings").EnumerateArray());
        Assert.Empty((await factory.NotificationRowsAsync(row.Id)).Where(n => n.Type == NotificationTypes.PurchaseRequestResponse));
    }
}
