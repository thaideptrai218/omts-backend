using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>BR-SALE-03, BR-SALE-09: how the response reaches the trader, and what is kept of each sending.</summary>
public class PurchaseRequestResponseMessageTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId, string ProductName)> WorldAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(name: $"Hàu {NewCode()}");
        return (owner, await factory.TraderAsync(), product.Id(), product.Text("name")!);
    }

    // BR-SALE-03: the trader account and the phone number of the profile; the SMS row never appears in a list (BR-SALE-09).
    [Fact]
    public async Task The_response_reaches_the_trader_by_push_and_sms_and_each_sending_is_kept()
    {
        var (owner, trader, productId, productName) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId, 300m);
        var pickup = Today.AddDays(4);

        var view = await (await owner.AcceptAsync(request, 250m, pickup, payLater: true, note: "Trả sau")).ReadAsync(HttpStatusCode.OK);

        var expected = $"HTX Trung Nam: your request for 300 kg of {productName} is accepted for 250 kg, pickup on {pickup:dd/MM/yyyy}; you may pay later. Trả sau";
        var rows = (await factory.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestResponse).ToList();
        var listRow = Assert.Single(rows.InList());
        Assert.Equal((trader.Account.Id, "MSG-SALE-03", expected), (listRow.RecipientAccountId, listRow.MessageCode, listRow.Body));
        var sending = Assert.Single(rows.Sendings());
        Assert.Equal((NotificationChannel.Sms, trader.Profile.PhoneNumber, StatusIds.MessageDelivery.Sent), (sending.Channel, sending.PhoneNumber, sending.DeliveryStatusId));
        Assert.NotNull(sending.ProviderMessageId);
        Assert.NotNull(sending.DeliveryStatusAt);

        Assert.Equal(expected, factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(trader.Profile.PhoneNumber)[0].Text);
        Assert.Contains(factory.Services.GetRequiredService<FakePushSender>().Inbox.List(trader.Account.Id.ToString()), m => m.NotificationId == listRow.Id);

        var shown = view.GetProperty("sendings").EnumerateArray().Single();
        Assert.Equal(("Sms", "Sent"), (shown.Text("channel"), shown.Text("deliveryStatus")));

        var list = await (await trader.Client.GetAsync("/api/me/notifications")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(listRow.Id, list.GetProperty("items").EnumerateArray().Single(n => n.GetProperty("relatedRecordId").GetGuid() == request.Id()).Id());
    }

    [Fact]
    public async Task A_decline_says_declined_with_the_note_and_no_pay_later_text_is_sent_without_the_agreement()
    {
        var (owner, trader, productId, productName) = await WorldAsync();
        var declined = await trader.Client.PlaceAsync(productId, 120.5m);
        (await owner.DeclineAsync(declined, "Chưa đủ hàu")).EnsureSuccessStatusCode();
        var accepted = await trader.Client.PlaceAsync(productId, 80m);
        var pickup = Today.AddDays(1);
        (await owner.AcceptAsync(accepted, pickupDate: pickup)).EnsureSuccessStatusCode();

        var first = Assert.Single((await factory.NotificationRowsAsync(declined.Id())).Sendings());
        Assert.Equal($"HTX Trung Nam: your request for 120.5 kg of {productName} is declined. Chưa đủ hàu", first.Body);
        var second = Assert.Single((await factory.NotificationRowsAsync(accepted.Id())).Sendings());
        Assert.Equal($"HTX Trung Nam: your request for 80 kg of {productName} is accepted for 80 kg, pickup on {pickup:dd/MM/yyyy}.", second.Body);
    }

    [Fact]
    public async Task A_long_note_is_cut_in_the_sms_and_kept_whole_in_the_list_and_on_the_request()
    {
        var (owner, trader, productId, _) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);
        var note = new string('n', 1000);

        var view = await (await owner.DeclineAsync(request, note)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(note, view.GetProperty("response").Text("note"));
        var rows = await factory.NotificationRowsAsync(request.Id());
        Assert.EndsWith(note, rows.InList().Single(n => n.Type == NotificationTypes.PurchaseRequestResponse).Body);
        Assert.EndsWith(new string('n', 80) + "...", rows.Sendings().Single().Body);
    }

    // BR-SALE-09: nothing is sent when the profile has no phone number.
    [Fact]
    public async Task A_profile_with_no_number_gets_the_push_and_no_sms_row()
    {
        var (owner, _, productId, _) = await WorldAsync();
        var trader = await factory.TraderAsync(configure: p => p.PhoneNumber = null);
        var request = await trader.Client.PlaceAsync(productId);

        var view = await (await owner.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(view.GetProperty("sendings").EnumerateArray());
        var rows = (await factory.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestResponse).ToList();
        Assert.Single(rows.InList());
        Assert.Empty(rows.Sendings());
    }

    // BR-SALE-03: the Owner also receives what the Owner did; a response is sent only to the trader, so no row reaches the Owner.
    [Fact]
    public async Task The_owner_who_responds_gets_no_notification_because_the_response_is_for_the_trader()
    {
        var (owner, trader, productId, _) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);

        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        var rows = (await factory.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestResponse).ToList();
        Assert.All(rows, n => Assert.Equal(trader.Account.Id, n.RecipientAccountId));
    }
}
