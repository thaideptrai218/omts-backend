using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: §I.4.4 for SALE-01, SALE-02, SALE-03 on every route, and what allZones on PURCHASE_REQUESTS opens (GFR-02).</summary>
public class PurchaseRequestQaAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    public enum Caller
    {
        Owner,
        ManagerOfOneZone,
        ManagerOfAllZones,
        AccountantOfOneZone,
        AccountantOfAllZones,
        StaffWhosePairWasRevoked,
        Trader,
        Anonymous,
    }

    private async Task<HttpClient> ClientAsync(Caller caller, HttpClient owner)
    {
        switch (caller)
        {
            case Caller.Owner:
                return owner;
            case Caller.ManagerOfOneZone:
                return await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));
            case Caller.ManagerOfAllZones:
                return await factory.PurchasingStaffAsync(null, StaffRole.ZoneManager);
            case Caller.AccountantOfOneZone:
                return await factory.AccountantAsync(await factory.AddZoneAsync(NewCode()));
            case Caller.AccountantOfAllZones:
                return await factory.AccountantAsync();
            case Caller.Trader:
                return (await factory.TraderAsync()).Client;
            case Caller.Anonymous:
                return factory.CreateClient();
            case Caller.StaffWhosePairWasRevoked:
                var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
                var pair = await factory.GrantAsync(staff, StaffRole.ZoneManager, await factory.AddZoneAsync(NewCode()));
                var client = await factory.LoginAsync(staff.Username);
                (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pair.Id}/revoke", new { reason = "Moved" })).EnsureSuccessStatusCode();
                return client;
            default:
                throw new ArgumentOutOfRangeException(nameof(caller));
        }
    }

    // "200" a reader of SALE-03 (Owner Full, Zone Manager R); "403" the §I.4.4 No; "401" no token.
    [Theory]
    [InlineData(Caller.Owner, 200, 200, 403)]
    [InlineData(Caller.ManagerOfOneZone, 200, 403, 403)]
    [InlineData(Caller.ManagerOfAllZones, 200, 403, 403)]
    [InlineData(Caller.AccountantOfOneZone, 403, 403, 403)]
    [InlineData(Caller.AccountantOfAllZones, 403, 403, 403)]
    [InlineData(Caller.StaffWhosePairWasRevoked, 403, 403, 403)]
    [InlineData(Caller.Trader, 403, 403, 404)]
    [InlineData(Caller.Anonymous, 401, 401, 401)]
    public async Task Every_route_answers_as_the_matrix_says(Caller caller, int read, int answer, int traderRoute)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var trader = await factory.TraderAsync();
        var productId = (await owner.CreateProductAsync()).Id();
        var accepted = await trader.Client.PlaceAsync(productId);
        var declined = await trader.Client.PlaceAsync(productId);
        var cancelled = await trader.Client.PlaceAsync(productId);
        var client = await ClientAsync(caller, owner);

        var reads = new[]
        {
            await client.GetAsync("/api/purchase-requests"),
            await client.GetAsync($"/api/purchase-requests/{accepted.Id()}"),
            await client.GetAsync($"/api/purchase-requests?customerId={trader.Profile.Id}"),
        };
        Assert.All(reads, r => Assert.Equal(read, (int)r.StatusCode));
        Assert.Equal(answer, (int)(await client.AcceptAsync(accepted)).StatusCode);
        Assert.Equal(answer, (int)(await client.DeclineAsync(declined)).StatusCode);

        // A foreign trader gets 404 on a request by id, and the Owner and staff 403.
        Assert.Equal(traderRoute, (int)(await client.GetAsync($"/api/me/purchase-requests/{cancelled.Id()}")).StatusCode);
        Assert.Equal(traderRoute, (int)(await client.CancelAsync(cancelled)).StatusCode);

        // The two without an id: the trader's own list and form, 403 or 401 for everybody else.
        var ownRouteOutsideTrader = caller == Caller.Trader ? 200 : traderRoute;
        Assert.Equal(ownRouteOutsideTrader, (int)(await client.GetAsync("/api/me/purchases")).StatusCode);
        Assert.Equal(caller == Caller.Trader ? 201 : traderRoute, (int)(await client.PostRequestAsync(productId)).StatusCode);

        if (traderRoute == 403 && caller != Caller.Anonymous)
        {
            await (await client.GetAsync("/api/me/purchases")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }
    }

    // Mixed pairs: the Zone Manager pair opens the read, the Accountant pair adds nothing, and nobody but the Owner answers.
    [Fact]
    public async Task A_manager_of_one_zone_who_is_accountant_of_another_reads_and_still_cannot_answer()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync((await owner.CreateProductAsync()).Id());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, await factory.AddZoneAsync(NewCode()));
        await factory.GrantAsync(staff, StaffRole.Accountant, null);
        var client = await factory.LoginAsync(staff.Username);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/purchase-requests/{request.Id()}")).StatusCode);
        await (await client.AcceptAsync(request)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await client.DeclineAsync(request)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    // ACCOUNT-09: a Locked Owner or manager is stopped from the next action, before anything is answered.
    [Fact]
    public async Task A_locked_owner_or_manager_answers_and_reads_nothing_from_the_next_action()
    {
        var ownerAccount = await factory.AddAccountAsync(NewCode(), Password);
        await factory.EnsureCooperativeAsync();
        var owner = await factory.LoginAsync(ownerAccount.Username);
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync((await owner.CreateProductAsync()).Id());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, null);
        var manager = await factory.LoginAsync(staff.Username);

        foreach (var id in new[] { ownerAccount.Id, staff.Id })
        {
            await factory.UpdateAsync<OMTS.Domain.Entities.Identity.Account>(id, a => a.StatusId = OMTS.Domain.Constants.StatusIds.Account.Locked);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.AcceptAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/purchase-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.GetAsync("/api/purchase-requests")).StatusCode);
        Assert.Equal(3201, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
    }

    // The refusal comes before the body is read: a role with no right gets MSG-GEN-08 for any body (GFR-02).
    [Fact]
    public async Task A_role_without_the_right_gets_msg_gen_08_whatever_the_body_holds()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync((await owner.CreateProductAsync()).Id());
        var manager = await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));

        foreach (var response in new[]
        {
            await manager.PostAsJsonRawAsync($"/api/purchase-requests/{request.Id()}/accept", "{ nonsense"),
            await manager.PostAsJsonRawAsync($"/api/purchase-requests/{request.Id()}/decline", "[]"),
            await owner.PostAsJsonRawAsync("/api/me/purchase-requests", "{ nonsense"),
            await owner.PostAsJsonRawAsync($"/api/me/purchase-requests/{request.Id()}/cancel", "[]"),
            await trader.Client.PostAsJsonRawAsync($"/api/purchase-requests/{request.Id()}/accept", "{ nonsense"),
        })
        {
            await response.ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }
    }
}
