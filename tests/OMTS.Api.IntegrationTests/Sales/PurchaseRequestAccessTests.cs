using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>§I.4.4: SALE-01, SALE-02, SALE-10 Trader only, own data; SALE-03 Owner Full, Zone Manager R; the Accountant No.</summary>
public class PurchaseRequestAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, System.Text.Json.JsonElement Request)> RequestAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var trader = await factory.TraderAsync();
        var product = await owner.CreateProductAsync();
        return (owner, trader, await trader.Client.PlaceAsync(product.Id()));
    }

    private static IEnumerable<Task<HttpResponseMessage>> StaffWrites(HttpClient client, System.Text.Json.JsonElement request)
    {
        yield return client.AcceptAsync(request);
        yield return client.DeclineAsync(request);
    }

    private static IEnumerable<Task<HttpResponseMessage>> StaffReads(HttpClient client, Guid id)
    {
        yield return client.GetAsync("/api/purchase-requests");
        yield return client.GetAsync($"/api/purchase-requests/{id}");
    }

    private static IEnumerable<Task<HttpResponseMessage>> TraderRoutes(HttpClient client, System.Text.Json.JsonElement request)
    {
        yield return client.GetAsync("/api/me/purchase-requests");
        yield return client.GetAsync($"/api/me/purchase-requests/{request.Id()}");
        yield return client.PostAsJsonAsync("/api/me/purchase-requests", new { productId = Guid.NewGuid(), requestedCages = 1, pickupDate = "2030-01-01" });
        yield return client.CancelAsync(request);
    }

    [Fact]
    public async Task Without_a_token_every_route_answers_401()
    {
        var (_, _, request) = await RequestAsync();
        var anonymous = factory.CreateClient();

        foreach (var call in StaffWrites(anonymous, request).Concat(StaffReads(anonymous, request.Id())).Concat(TraderRoutes(anonymous, request)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await call).StatusCode);
        }
    }

    [Fact]
    public async Task An_accountant_has_no_route_even_at_all_zones()
    {
        var (_, _, request) = await RequestAsync();
        var accountant = await factory.AccountantAsync();

        foreach (var call in StaffWrites(accountant, request).Concat(StaffReads(accountant, request.Id())).Concat(TraderRoutes(accountant, request)))
        {
            await (await call).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }
    }

    // PM 03/11: requests have no zone, so a Zone Manager of any zone reads them all; deciding is the Owner's (BR-SALE-02).
    [Fact]
    public async Task A_zone_manager_of_any_zone_reads_every_request_and_cannot_answer()
    {
        var (_, _, request) = await RequestAsync();
        var manager = await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));

        foreach (var call in StaffReads(manager, request.Id()))
        {
            Assert.Equal(HttpStatusCode.OK, (await call).StatusCode);
        }

        foreach (var call in StaffWrites(manager, request).Concat(TraderRoutes(manager, request)))
        {
            await (await call).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        Assert.True(PermissionFunctions.IsCooperativeWide(PermissionFunctions.PurchaseRequests));
    }

    [Fact]
    public async Task A_revoked_pair_reads_no_request_from_the_next_action()
    {
        var (owner, _, request) = await RequestAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var pair = await factory.GrantAsync(staff, StaffRole.ZoneManager, zone);
        var manager = await factory.LoginAsync(staff.Username);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/purchase-requests/{request.Id()}")).StatusCode);

        (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pair.Id}/revoke", new { reason = "Moved" })).EnsureSuccessStatusCode();

        await (await manager.GetAsync($"/api/purchase-requests/{request.Id()}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task A_trader_reaches_no_staff_route_and_the_owner_no_trader_route()
    {
        var (owner, trader, request) = await RequestAsync();

        foreach (var call in StaffWrites(trader.Client, request).Concat(StaffReads(trader.Client, request.Id())))
        {
            await (await call).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        foreach (var call in TraderRoutes(owner, request))
        {
            await (await call).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }
    }

    [Fact]
    public async Task A_trader_without_a_verified_number_is_stopped_first()
    {
        var (_, _, request) = await RequestAsync();
        var unverified = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader, a => { a.PhoneNumber = null; a.PhoneVerifiedAt = null; });
        var client = await factory.LoginAsync(unverified.Username);

        foreach (var call in TraderRoutes(client, request))
        {
            await (await call).ProblemAsync(HttpStatusCode.Forbidden, "MSG-ACCOUNT-04");
        }
    }

    // BR-ACCOUNT-04, §I.4.4 †: every read and write of a request, by another trader, answers as if it did not exist.
    [Fact]
    public async Task Another_trader_can_neither_read_nor_cancel_a_request_nor_see_it_listed()
    {
        var (_, trader, request) = await RequestAsync();
        var other = await factory.TraderAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/me/purchase-requests/{request.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.CancelAsync(request)).StatusCode);
        var list = await (await other.Client.GetAsync("/api/me/purchase-requests")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(list.GetProperty("requests").EnumerateArray());
        Assert.Equal(request.Id(), (await trader.Client.MineAsync(request.Id())).Id());
    }

    // ACCOUNT-08 steps 4-5, BR-ACCOUNT-12, SALE-03.2, SALE-10.2: opening the record checks the access again.
    [Fact]
    public async Task A_notification_opens_its_request_for_the_owner_and_for_its_trader()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var ownerClient = await factory.LoginAsync(owner.Username);
        var product = await ownerClient.CreateProductAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(product.Id());
        (await ownerClient.AcceptAsync(request)).EnsureSuccessStatusCode();
        var rows = await factory.NotificationRowsAsync(request.Id());

        var forOwner = rows.InList().Single(n => n.RecipientAccountId == owner.Id && n.Type == NotificationTypes.PurchaseRequestNew);
        var opened = await (await ownerClient.GetAsync($"/api/me/notifications/{forOwner.Id}/open")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("PurchaseRequest", request.Id()), (opened.Text("recordType"), opened.GetProperty("recordId").GetGuid()));

        var forTrader = rows.InList().Single(n => n.Type == NotificationTypes.PurchaseRequestResponse);
        var openedByTrader = await (await trader.Client.GetAsync($"/api/me/notifications/{forTrader.Id}/open")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(request.Id(), openedByTrader.GetProperty("recordId").GetGuid());

        // The SMS row is never reachable through the list routes.
        var sending = rows.Sendings().Single();
        Assert.Equal(HttpStatusCode.NotFound, (await trader.Client.GetAsync($"/api/me/notifications/{sending.Id}/open")).StatusCode);
    }

    // BR-ACCOUNT-12: the trader the notification was sent to no longer owns the record (the row is moved by hand here).
    [Fact]
    public async Task A_response_notification_does_not_open_the_request_of_another_trader()
    {
        var (owner, trader, request) = await RequestAsync();
        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
        var other = await factory.TraderAsync();
        var row = (await factory.NotificationRowsAsync(request.Id())).InList().Single(n => n.Type == NotificationTypes.PurchaseRequestResponse);
        await factory.UpdateAsync<OMTS.Domain.Entities.Platform.Notification>(row.Id, n => n.RecipientAccountId = other.Account.Id);

        await (await other.Client.GetAsync($"/api/me/notifications/{row.Id}/open")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.NotEqual(other.Account.Id, trader.Account.Id);
    }

    // BR-ACCOUNT-11: each account is offered the types it can receive.
    [Fact]
    public async Task The_preferences_offer_each_type_to_who_receives_it()
    {
        var (owner, trader, _) = await RequestAsync();

        var ownerTypes = await (await owner.GetAsync("/api/me/notification-preferences")).ReadAsync(HttpStatusCode.OK);
        var traderTypes = await (await trader.Client.GetAsync("/api/me/notification-preferences")).ReadAsync(HttpStatusCode.OK);

        var offeredToOwner = ownerTypes.GetProperty("types").EnumerateArray().ToDictionary(t => t.Text("type")!, t => t.GetProperty("mandatory").GetBoolean());
        Assert.True(offeredToOwner[NotificationTypes.PurchaseRequestNew]);
        Assert.False(offeredToOwner[NotificationTypes.PurchaseRequestCancelled]);
        Assert.DoesNotContain(NotificationTypes.PurchaseRequestResponse, offeredToOwner.Keys);
        // BR-ACCOUNT-11: the trader's types are the response to a request, the withdrawal of a provenance record (TRACE-04), the approval of a Zalo access request and the response to a quality claim (CLAIM-02), none mandatory.
        var offeredToTrader = traderTypes.GetProperty("types").EnumerateArray().Select(t => (t.Text("type"), t.GetProperty("mandatory").GetBoolean())).ToList();
        Assert.Equal([(NotificationTypes.PurchaseRequestResponse, false), (NotificationTypes.ProvenanceWithdrawn, false), (NotificationTypes.TraderAccessApproved, false), (NotificationTypes.QualityClaimResponse, false)], offeredToTrader);
    }
}
