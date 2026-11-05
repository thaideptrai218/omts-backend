using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>The edges of the rules: the business day at UTC+7, a date or a product that changed after the request, no SMS provider.</summary>
public class PurchaseRequestEdgeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ClockedFactory : AuthApiFactory
    {
        public MovableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 4, 17, 30, 0, TimeSpan.Zero));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }
    }

    // The services directly: an API token would not outlive a clock set by hand.
    [Fact]
    public async Task Today_is_the_business_day_at_utc_plus_7_for_the_request_and_for_the_response()
    {
        await using var clocked = new ClockedFactory();
        var (trader, _) = await clocked.LinkedProfileAsync();
        var product = new Product { Name = NewCode(), UnitPrice = 1 };
        await clocked.UseDbAsync(db => db.Products.Add(product));
        var fifth = new DateOnly(2026, 11, 5);
        using var scope = clocked.Services.CreateScope();
        var traders = scope.ServiceProvider.GetRequiredService<TraderPurchaseRequestService>();
        var responses = scope.ServiceProvider.GetRequiredService<PurchaseRequestResponseService>();

        // 00:30 on 05/11 in Vietnam, still 04/11 by UTC.
        var yesterday = await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(product.Id, 1m, fifth.AddDays(-1), null), CancellationToken.None);
        Assert.Equal("MSG-GEN-01", yesterday.Error!.Message.Code);
        var today = await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(product.Id, 1m, fifth, null), CancellationToken.None);
        Assert.True(today.IsSuccess);

        // 23:59 on 05/11 in Vietnam, then a minute later it is the 6th there, and still the 5th by UTC.
        clocked.Clock.Now = new DateTimeOffset(2026, 11, 5, 16, 59, 0, TimeSpan.Zero);
        var request = today.Value!.Request;
        Assert.True((await responses.AcceptAsync(request.Id, new AcceptPurchaseRequest(1m, fifth, false, null, request.RowVersion), CancellationToken.None)).IsSuccess);
        clocked.Clock.Now = new DateTimeOffset(2026, 11, 5, 17, 0, 0, TimeSpan.Zero);
        var late = await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(product.Id, 1m, fifth, null), CancellationToken.None);
        Assert.Equal("MSG-GEN-01", late.Error!.Message.Code);
    }

    // SALE-03 step 6 checks the date of the response, and SALE-02 has no date rule.
    [Fact]
    public async Task A_pickup_date_that_has_passed_needs_a_new_date_to_accept_and_does_not_stop_a_cancel()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        var first = await trader.Client.PlaceAsync(product.Id());
        var second = await trader.Client.PlaceAsync(product.Id());
        foreach (var id in new[] { first.Id(), second.Id() })
        {
            await factory.UpdateAsync<PurchaseRequest>(id, r => r.PickupDate = Today.AddDays(-5));
        }

        Assert.Equal(["Pickup date"], await (await owner.AcceptAsync(first, pickupDate: Today.AddDays(-5))).InvalidFieldsAsync());
        (await owner.AcceptAsync(first, pickupDate: Today)).EnsureSuccessStatusCode();
        (await trader.Client.CancelAsync(second)).EnsureSuccessStatusCode();
    }

    // Pinned: only SALE-01 checks the product (SALE-03 does not), and a discontinued product can still be viewed (GBR-01).
    [Fact]
    public async Task A_request_whose_product_was_discontinued_since_can_still_be_answered_and_read()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(product.Id());
        (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).EnsureSuccessStatusCode();

        var view = await (await owner.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(product.Text("name"), view.Text("productName"));
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, view.GetProperty("statusId").GetInt32());
    }

    // Sms:Provider=None, the default outside Development until a provider is chosen.
    [Fact]
    public async Task With_no_sms_provider_the_response_is_saved_and_the_sending_says_not_sent()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        await factory.EnsureCooperativeAsync();
        var product = await (await factory.LoginAsync(owner.Username)).CreateProductAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(product.Id());
        await using var withoutProvider = factory.WithWebHostBuilder(b => b.UseSetting("Sms:Provider", "None"));
        var client = withoutProvider.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginTokensAsync(client, owner.Username)).GetProperty("accessToken").GetString());

        var view = await (await client.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Not sent", view.GetProperty("sendings").EnumerateArray().Single().Text("deliveryStatus"));
        var sending = Assert.Single((await factory.NotificationRowsAsync(request.Id())).Sendings());
        Assert.Equal("NOT_CONFIGURED", sending.ProviderErrorCode);
    }

    // BR-SALE-09: a number the fake refuses, through the whole route.
    [Fact]
    public async Task A_number_the_service_refuses_keeps_the_response()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        factory.RefuseSmsTo(trader.Profile.PhoneNumber!);
        var request = await trader.Client.PlaceAsync(product.Id());

        var view = await (await owner.DeclineAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(StatusIds.PurchaseRequest.Declined, view.GetProperty("statusId").GetInt32());
        Assert.Equal("Not sent", view.GetProperty("sendings").EnumerateArray().Single().Text("deliveryStatus"));
        Assert.Equal("FAKE_CONFIGURED_FAILURE", Assert.Single((await factory.NotificationRowsAsync(request.Id())).Sendings()).ProviderErrorCode);
    }

    // A push the trader turned off is not pushed; the notification stays (BR-ACCOUNT-12). The type is not mandatory.
    [Fact]
    public async Task A_trader_may_turn_off_the_push_of_a_response()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        var saved = await trader.Client.PutAsJsonAsyncRaw("/api/me/notification-preferences", """{ "items": [{ "type": "PURCHASE_REQUEST_RESPONSE", "push": false }] }""");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var request = await trader.Client.PlaceAsync(product.Id());

        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        Assert.Single((await factory.NotificationRowsAsync(request.Id())).InList().Where(n => n.RecipientAccountId == trader.Account.Id));
        var inbox = factory.Services.GetRequiredService<OMTS.Infrastructure.Messaging.Fakes.FakePushSender>().Inbox.List(trader.Account.Id.ToString());
        Assert.Empty(inbox);
    }
}
