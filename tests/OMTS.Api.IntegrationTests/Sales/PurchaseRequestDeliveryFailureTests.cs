using System.Net;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>GFR-06, BR-SALE-09: an SMS that the service does not accept undoes nothing and changes no answer; the row says Not sent.</summary>
public class PurchaseRequestDeliveryFailureTests(PurchaseRequestStubsFactory factory) : IClassFixture<PurchaseRequestStubsFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, System.Text.Json.JsonElement Request)> RequestAsync(string? secretNote = null)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        return (owner, trader, await trader.Client.PlaceAsync(product.Id(), note: secretNote));
    }

    private async Task AssertSavedAndNotSentAsync(HttpClient owner, TraderActor trader, Guid requestId, string errorCode)
    {
        var view = await owner.ViewedByStaffAsync(requestId);
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, view.GetProperty("statusId").GetInt32());
        Assert.Equal("Not sent", view.GetProperty("sendings").EnumerateArray().Single().Text("deliveryStatus"));

        var rows = (await factory.NotificationRowsAsync(requestId)).OfType(NotificationTypes.PurchaseRequestResponse).ToList();
        var sending = Assert.Single(rows.Sendings());
        Assert.Equal((StatusIds.MessageDelivery.NotSent, errorCode, (string?)null), (sending.DeliveryStatusId, sending.ProviderErrorCode, sending.ProviderMessageId));
        Assert.Single(rows.InList());
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, (await trader.Client.MineAsync(requestId)).GetProperty("statusId").GetInt32());
    }

    [Fact]
    public async Task A_refusal_of_the_service_keeps_the_response_and_says_not_sent()
    {
        var (owner, trader, request) = await RequestAsync();
        factory.Sender.Send = _ => Task.FromResult(DeliveryResult.Failed("PROVIDER_REFUSED"));

        var answer = await (await owner.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Not sent", answer.GetProperty("sendings").EnumerateArray().Single().Text("deliveryStatus"));
        await AssertSavedAndNotSentAsync(owner, trader, request.Id(), "PROVIDER_REFUSED");
    }

    [Fact]
    public async Task An_exception_of_the_service_is_not_a_500_and_keeps_the_response()
    {
        var (owner, trader, request) = await RequestAsync();
        factory.Sender.Send = _ => throw new InvalidOperationException("provider down");

        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        await AssertSavedAndNotSentAsync(owner, trader, request.Id(), "SEND_FAILED");
    }

    // A provider that ignores its cancellation holds the answer no longer than the limit (10 seconds).
    [Fact]
    public async Task A_service_that_never_answers_ends_as_not_sent()
    {
        var (owner, trader, request) = await RequestAsync();
        using var never = new SemaphoreSlim(0);
        factory.Sender.Send = async _ =>
        {
            await never.WaitAsync();
            return DeliveryResult.Sent("late");
        };

        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        never.Release();
        await AssertSavedAndNotSentAsync(owner, trader, request.Id(), "SEND_FAILED");
    }

    [Fact]
    public async Task The_log_of_a_failure_holds_neither_the_number_nor_the_text()
    {
        var (owner, trader, request) = await RequestAsync();
        factory.Sender.Send = _ => Task.FromResult(DeliveryResult.Failed("PROVIDER_REFUSED"));

        (await owner.DeclineAsync(request, "Ghi-chu-rieng-tu-cua-chu-htx")).EnsureSuccessStatusCode();

        Assert.Contains(factory.Logs.Lines, l => l.Contains("PROVIDER_REFUSED", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("Ghi-chu-rieng-tu-cua-chu-htx", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains(trader.Profile.PhoneNumber!, StringComparison.Ordinal));
    }
}
