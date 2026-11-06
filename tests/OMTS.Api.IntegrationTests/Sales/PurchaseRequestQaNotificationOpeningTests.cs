using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: opening each notification by the right and the wrong person, and the sending row (BR-ACCOUNT-12, BR-SALE-09).</summary>
public class PurchaseRequestQaNotificationOpeningTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private sealed record World(Account OwnerAccount, HttpClient Owner, TraderActor Trader, Guid ProductId, string ProductName);

    private async Task<World> WorldAsync()
    {
        await factory.EnsureCooperativeAsync();
        var account = await factory.AddAccountAsync(NewCode(), Password);
        var owner = await factory.LoginAsync(account.Username);
        var product = await owner.CreateProductAsync("Hàu " + NewCode());
        return new World(account, owner, await factory.TraderAsync("Chị Hoa"), product.Id(), product.Text("name")!);
    }

    // BR-ACCOUNT-12: another account's notification is not found; the record opens with its current status after every change.
    [Fact]
    public async Task Each_notification_opens_for_its_recipient_only_and_still_after_the_record_changed()
    {
        var w = await WorldAsync();
        var otherOwner = await factory.AddAccountAsync(NewCode(), Password);
        var otherOwnerClient = await factory.LoginAsync(otherOwner.Username);
        var otherTrader = await factory.TraderAsync();
        var manager = await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));
        var request = await w.Trader.Client.PlaceAsync(w.ProductId);
        (await w.Owner.AcceptAsync(request)).EnsureSuccessStatusCode();
        (await w.Trader.Client.CancelAsync(await w.Trader.Client.MineAsync(request.Id()))).EnsureSuccessStatusCode();
        var rows = (await factory.NotificationRowsAsync(request.Id())).InList();
        var news = rows.Single(n => n.RecipientAccountId == w.OwnerAccount.Id && n.Type == NotificationTypes.PurchaseRequestNew);
        var cancelled = rows.Single(n => n.RecipientAccountId == w.OwnerAccount.Id && n.Type == NotificationTypes.PurchaseRequestCancelled);
        var response = rows.Single(n => n.Type == NotificationTypes.PurchaseRequestResponse);

        foreach (var id in new[] { news.Id, cancelled.Id })
        {
            var opened = await (await w.Owner.GetAsync($"/api/me/notifications/{id}/open")).ReadAsync(HttpStatusCode.OK);
            Assert.Equal((request.Id(), "PurchaseRequest"), (opened.GetProperty("recordId").GetGuid(), opened.Text("recordType")));
        }

        Assert.Equal(request.Id(), (await (await w.Trader.Client.GetAsync($"/api/me/notifications/{response.Id}/open")).ReadAsync(HttpStatusCode.OK)).GetProperty("recordId").GetGuid());
        Assert.Equal("Cancelled", (await w.Owner.ViewedByStaffAsync(request.Id())).Text("status"));
        Assert.Equal("Cancelled", (await w.Trader.Client.MineAsync(request.Id())).Text("status"));

        foreach (var wrong in new[] { otherOwnerClient, otherTrader.Client, manager, w.Trader.Client })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await wrong.GetAsync($"/api/me/notifications/{news.Id}/open")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await wrong.PostAsync($"/api/me/notifications/{news.Id}/read", null)).StatusCode);
        }

        foreach (var wrong in new[] { otherOwnerClient, otherTrader.Client, manager, w.Owner })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await wrong.GetAsync($"/api/me/notifications/{response.Id}/open")).StatusCode);
        }
    }

    // BR-SALE-09: the SMS row is not a notification: not listed, not counted, not opened, not marked.
    [Fact]
    public async Task The_sending_row_is_in_no_list_no_count_and_cannot_be_opened_or_marked()
    {
        var w = await WorldAsync();
        var request = await w.Trader.Client.PlaceAsync(w.ProductId);
        (await w.Owner.AcceptAsync(request)).EnsureSuccessStatusCode();
        var sending = Assert.Single((await factory.NotificationRowsAsync(request.Id())).Sendings());

        var list = await (await w.Trader.Client.GetAsync("/api/me/notifications")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((1, 1), (list.GetProperty("total").GetInt32(), list.GetProperty("unreadCount").GetInt32()));
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), n => n.Id() == sending.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Trader.Client.GetAsync($"/api/me/notifications/{sending.Id}/open")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Trader.Client.PostAsync($"/api/me/notifications/{sending.Id}/read", null)).StatusCode);

        (await w.Trader.Client.PostAsync("/api/me/notifications/read-all", null)).EnsureSuccessStatusCode();
        var after = Assert.Single((await factory.NotificationRowsAsync(request.Id())).Sendings());
        Assert.Null(after.ReadAt);
    }
}
