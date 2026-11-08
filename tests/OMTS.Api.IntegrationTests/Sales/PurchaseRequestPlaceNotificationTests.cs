using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-01 POST-2, BR-ACCOUNT-11, GFR-06: who hears of a new request.</summary>
public class PurchaseRequestPlaceNotificationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // POST-2, BR-ACCOUNT-11: a mandatory type, always pushed.
    [Fact]
    public async Task The_owner_gets_the_mandatory_notification_with_msg_sale_01_and_its_push()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var ownerClient = await factory.LoginAsync(owner.Username);
        var product = await ownerClient.CreateProductAsync(name: $"Hàu {NewCode()}");
        var trader = await factory.TraderAsync("Chị Lan");
        var pickup = Today.AddDays(2);

        var request = await trader.Client.PlaceAsync(product.Id(), 301, pickup, "Lấy sớm");

        var expected = $"Chị Lan asks to buy about 300.5 kg of {product.Text("name")}, pickup on {pickup:dd/MM/yyyy}. Lấy sớm Please ask the Zone Manager to check the oysters, then respond.";
        var rows = (await factory.NotificationRowsAsync(request.Id())).Where(n => n.RecipientAccountId == owner.Id).ToList();
        var row = Assert.Single(rows);
        Assert.Equal((NotificationTypes.PurchaseRequestNew, "MSG-SALE-01", expected, true), (row.Type, row.MessageCode, row.Body, row.IsMandatory));
        Assert.Contains("\"requested_cages\":300.5", row.MessageParams);
        var push = factory.Services.GetRequiredService<FakePushSender>().Inbox.List(owner.Id.ToString()).Single(m => m.NotificationId == row.Id);
        Assert.Equal("MSG-SALE-01", push.MessageCode);

        var types = await (await ownerClient.GetAsync("/api/me/notification-preferences")).ReadAsync(HttpStatusCode.OK);
        var offered = types.GetProperty("types").EnumerateArray().Single(t => t.Text("type") == NotificationTypes.PurchaseRequestNew);
        Assert.True(offered.GetProperty("mandatory").GetBoolean());
    }

    // GFR-06: a notification that cannot be pushed stays in the list, and nothing saved is undone.
    [Fact]
    public async Task A_push_that_fails_undoes_nothing_and_the_notification_stays_in_the_list()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var ownerClient = await factory.LoginAsync(owner.Username);
        var product = await ownerClient.CreateProductAsync();
        factory.RefusePushTo(owner.Id);
        var trader = await factory.TraderAsync();

        var request = await trader.Client.PlaceAsync(product.Id());

        Assert.Single((await factory.NotificationRowsAsync(request.Id())).Where(n => n.RecipientAccountId == owner.Id));
        Assert.DoesNotContain(factory.Services.GetRequiredService<FakePushSender>().Inbox.List(owner.Id.ToString()), m => m.Text.StartsWith(NotificationTypes.PurchaseRequestNew, StringComparison.Ordinal));
    }
}
