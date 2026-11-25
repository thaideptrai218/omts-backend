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
    // POST-2, BR-ACCOUNT-11: a mandatory type.
    [Fact]
    public async Task The_owner_gets_the_mandatory_notification_with_msg_sale_01()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var ownerClient = await factory.LoginAsync(owner.Username);
        var product = await ownerClient.CreateProductAsync(name: $"Hàu {NewCode()}");
        var trader = await factory.TraderAsync("Chị Lan");
        var pickup = Today.AddDays(2);

        var request = await trader.Client.PlaceAsync(product.Id(), 300, pickup, "Lấy sớm");

        var expected = $"Chị Lan asks to buy 300 cages of {product.Text("name")}, pickup on {pickup:dd/MM/yyyy}. Lấy sớm Please ask the Zone Manager to check the oysters, then respond.";
        var rows = (await factory.NotificationRowsAsync(request.Id())).Where(n => n.RecipientAccountId == owner.Id).ToList();
        var row = Assert.Single(rows);
        Assert.Equal((NotificationTypes.PurchaseRequestNew, "MSG-SALE-01", expected, true), (row.Type, row.MessageCode, row.Body, row.IsMandatory));
        Assert.Contains("\"requested_cages\":300", row.MessageParams);
    }
}
