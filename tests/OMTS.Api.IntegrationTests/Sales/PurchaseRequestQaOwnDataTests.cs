using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223, SALE-01, SALE-02, SALE-10: a trader's own data, what each answer shows, and who is stopped (NFR-03, BR-ACCOUNT-04).</summary>
public class PurchaseRequestQaOwnDataTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId)> WorldAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        return (owner, await factory.TraderAsync(), (await owner.CreateProductAsync()).Id());
    }

    private static string WithoutTrace(string body)
    {
        using var document = JsonDocument.Parse(body);
        return JsonSerializer.Serialize(document.RootElement.EnumerateObject().Where(p => p.Name != "traceId").ToDictionary(p => p.Name, p => p.Value.ToString()));
    }

    [Fact]
    public async Task Enumerating_ids_tells_a_request_of_another_trader_from_no_request_in_no_way()
    {
        var (_, trader, productId) = await WorldAsync();
        var other = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(productId);
        var unknown = Guid.NewGuid();

        var foreignRead = await other.Client.GetAsync($"/api/me/purchase-requests/{request.Id()}");
        var unknownRead = await other.Client.GetAsync($"/api/me/purchase-requests/{unknown}");
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (foreignRead.StatusCode, unknownRead.StatusCode));
        Assert.Equal(WithoutTrace(await unknownRead.Content.ReadAsStringAsync()), WithoutTrace(await foreignRead.Content.ReadAsStringAsync()));

        // With the true version, with none and with a junk one: the answer never says that the id exists.
        foreach (var body in new object[] { new { rowVersion = request.RowVersion() }, new { }, new { rowVersion = "%%%" } })
        {
            var foreign = await other.Client.PostAsJsonAsync($"/api/me/purchase-requests/{request.Id()}/cancel", body);
            var nothing = await other.Client.PostAsJsonAsync($"/api/me/purchase-requests/{unknown}/cancel", body);
            Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (foreign.StatusCode, nothing.StatusCode));
            Assert.Equal(WithoutTrace(await nothing.Content.ReadAsStringAsync()), WithoutTrace(await foreign.Content.ReadAsStringAsync()));
        }

        Assert.Equal(StatusIds.PurchaseRequest.AwaitingResponse, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
        Assert.DoesNotContain(await factory.AuditRowsAsync(request.Id()), e => e.Action == "PurchaseRequest.Cancelled");
        Assert.Equal(0, (await (await other.Client.GetAsync("/api/me/purchases")).ReadAsync(HttpStatusCode.OK)).GetProperty("total").GetInt32());
    }

    // BR-ACCOUNT-04: an account with no linked profile has nothing, and cannot send.
    [Fact]
    public async Task A_trader_with_no_linked_profile_has_an_empty_list_and_cannot_send_or_reach_a_request()
    {
        var (_, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);
        var bare = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var client = await factory.LoginAsync(bare.Username);

        var list = await (await client.GetAsync("/api/me/purchases")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-11", 0), (list.Text("messageCode"), list.GetProperty("total").GetInt32()));
        await (await client.PostRequestAsync(productId)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/me/purchase-requests/{request.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.CancelAsync(request)).StatusCode);
    }

    // BR-ACCOUNT-03, MSG-ACCOUNT-04: an unverified number is stopped on every route, also with a request that exists.
    [Fact]
    public async Task A_trader_who_lost_the_verified_number_is_stopped_on_every_route()
    {
        var (_, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);

        await factory.UpdateAsync<Account>(trader.Account.Id, a => a.PhoneVerifiedAt = null);

        foreach (var call in new[]
        {
            trader.Client.GetAsync("/api/me/purchases"),
            trader.Client.GetAsync($"/api/me/purchase-requests/{request.Id()}"),
            trader.Client.PostRequestAsync(productId),
            trader.Client.CancelAsync(request),
        })
        {
            await (await call).ProblemAsync(HttpStatusCode.Forbidden, "MSG-ACCOUNT-04");
        }
    }

    // ACCOUNT-09: Locked is stopped from the next action; the data stays and the Owner can still answer.
    [Fact]
    public async Task A_locked_trader_is_stopped_on_every_route_from_the_next_action_and_the_request_can_still_be_answered()
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);

        await factory.UpdateAsync<Account>(trader.Account.Id, a => a.StatusId = StatusIds.Account.Locked);

        foreach (var call in new[]
        {
            trader.Client.GetAsync("/api/me/purchases"),
            trader.Client.GetAsync($"/api/me/purchase-requests/{request.Id()}"),
            trader.Client.PostRequestAsync(productId),
            trader.Client.CancelAsync(request),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await call).StatusCode);
        }

        var answered = await (await owner.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, answered.GetProperty("statusId").GetInt32());
    }

    // SALE-10.1 shows the response; BR-SALE-01: no price; NFR-03: nothing of anybody else.
    [Fact]
    public async Task A_trader_sees_the_owners_note_and_no_price_no_phone_no_customer_and_nothing_of_who_answered()
    {
        var (owner, trader, productId) = await WorldAsync();
        var other = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(productId, note: "Sáng sớm");
        await other.Client.PlaceAsync(productId);
        (await owner.AcceptAsync(request, 60, payLater: true, note: "Còn 60 kg")).EnsureSuccessStatusCode();

        var single = await trader.Client.GetAsync($"/api/me/purchase-requests/{request.Id()}");
        var list = await trader.Client.GetAsync("/api/me/purchases");
        foreach (var text in new[] { await single.Content.ReadAsStringAsync(), await list.Content.ReadAsStringAsync() })
        {
            Assert.Contains("Còn 60 kg", text);
            foreach (var secret in new[] { trader.Profile.PhoneNumber!, other.Profile.PhoneNumber!, other.Profile.Name, "unitPrice", "currentDebt", "sendings", "respondedBy", trader.Profile.Id.ToString() })
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        var names = (await single.ReadAsync(HttpStatusCode.OK)).EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(
            new[] { "cancelReason", "cancelledAt", "canCancel", "code", "fulfilled", "harvestCompletedAt", "id", "note", "pickupDate", "productId", "productName", "requestedCages", "response", "rowVersion", "sentAt", "status", "statusId" }.Order().ToList(),
            names);
    }

    // SALE-01 step 1: the products offered carry no price, and a Discontinued one is not offered.
    [Fact]
    public async Task The_products_a_trader_chooses_from_carry_no_price_and_only_those_in_use()
    {
        var (owner, trader, productId) = await WorldAsync();
        var gone = await owner.CreateProductAsync();
        (await owner.PostAsync($"/api/products/{gone.Id()}/discontinue", null)).EnsureSuccessStatusCode();

        var body = await (await trader.Client.GetAsync("/api/products")).ReadAsync(HttpStatusCode.OK);

        var items = body.EnumerateArray().ToList();
        Assert.Contains(items, p => p.Id() == productId);
        Assert.DoesNotContain(items, p => p.Id() == gone.Id());
        Assert.All(items, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("unitPrice").ValueKind));
    }

    // Staff read one customer per request: the number of another trader is in no answer about this one.
    [Fact]
    public async Task A_staff_answer_about_a_request_holds_only_the_number_of_its_own_customer()
    {
        var (owner, trader, productId) = await WorldAsync();
        var other = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(productId);
        await other.Client.PlaceAsync(productId);
        var manager = await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));
        var answered = await owner.AcceptAsync(request);

        var answers = new[]
        {
            answered,
            await manager.GetAsync($"/api/purchase-requests/{request.Id()}"),
            await manager.GetAsync($"/api/purchase-requests?customerId={trader.Profile.Id}"),
        };
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.Contains(trader.Profile.PhoneNumber!, text);
            Assert.DoesNotContain(other.Profile.PhoneNumber!, text);
        }

        var rows = await factory.NotificationRowsAsync(request.Id());
        Assert.All(rows.InList(), n =>
        {
            Assert.DoesNotContain(trader.Profile.PhoneNumber!, n.Body);
            Assert.DoesNotContain(trader.Profile.PhoneNumber!, n.MessageParams);
        });
    }
}
