using System.Net;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-02, BR-SALE-01: the trader cancels a request Awaiting response, or Accepted and not Fulfilled; a reason is optional.</summary>
public class PurchaseRequestCancelTests(PurchaseRequestStubsFactory factory) : IClassFixture<PurchaseRequestStubsFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId)> WorldAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        return (owner, await factory.TraderAsync("Anh Tùng"), (await owner.CreateProductAsync(name: "Hàu " + AcsTestKit.NewCode())).Id());
    }

    [Fact]
    public async Task An_awaiting_request_is_cancelled_with_its_reason_and_time_and_the_owner_hears_of_it()
    {
        var (_, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId, 200m);

        var cancelled = await (await trader.Client.CancelAsync(request, "  Khách đổi lịch ")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(StatusIds.PurchaseRequest.Cancelled, cancelled.GetProperty("statusId").GetInt32());
        Assert.Equal("Cancelled", cancelled.Text("status"));
        Assert.Equal("Khách đổi lịch", cancelled.Text("cancelReason"));
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, cancelled.GetProperty("cancelledAt").ValueKind);
        Assert.False(cancelled.GetProperty("canCancel").GetBoolean());

        var rows = (await factory.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestCancelled).ToList();
        Assert.NotEmpty(rows);
        var row = rows[0];
        Assert.Equal("MSG-SALE-02", row.MessageCode);
        Assert.False(row.IsMandatory);
        var sentOn = await factory.DbAsync(db => db.PurchaseRequests.FindAsync(request.Id()).AsTask());
        var day = OMTS.Application.Common.BusinessClock.ToBusinessDate(sentOn!.SentAt);
        Assert.StartsWith($"Anh Tùng cancelled the purchase request of {day:dd/MM/yyyy} for about 200 kg of Hàu ", row.Body);
        Assert.EndsWith(". Khách đổi lịch", row.Body);

        var audit = (await factory.AuditRowsAsync(request.Id())).Single(e => e.Action == "PurchaseRequest.Cancelled");
        Assert.Equal((trader.Account.Id, "Khách đổi lịch"), (audit.ActorAccountId, audit.Reason));
        Assert.Contains("AWAITING_RESPONSE", audit.Before);
        Assert.Contains("CANCELLED", audit.After);
    }

    [Fact]
    public async Task No_reason_is_needed_and_a_long_one_is_refused()
    {
        var (_, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);

        Assert.Equal(["Reason"], await (await trader.Client.CancelAsync(request, new string('x', 1001))).InvalidFieldsAsync());

        var cancelled = await (await trader.Client.CancelAsync(request)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, cancelled.GetProperty("cancelReason").ValueKind);
        var row = (await factory.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestCancelled).First();
        Assert.EndsWith(".", row.Body);
    }

    [Fact]
    public async Task An_accepted_request_can_be_cancelled_and_keeps_its_response()
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);
        (await owner.AcceptAsync(request, kg: 50m, payLater: true)).EnsureSuccessStatusCode();
        var accepted = await trader.Client.MineAsync(request.Id());
        Assert.True(accepted.GetProperty("canCancel").GetBoolean());

        var cancelled = await (await trader.Client.CancelAsync(accepted)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(StatusIds.PurchaseRequest.Cancelled, cancelled.GetProperty("statusId").GetInt32());
        Assert.Equal(50m, cancelled.GetProperty("response").GetProperty("acceptedKg").GetDecimal());
    }

    // SALE-02.0.E1.
    [Fact]
    public async Task A_declined_or_cancelled_request_cannot_be_cancelled_and_its_status_is_shown()
    {
        var (owner, trader, productId) = await WorldAsync();
        var declined = await trader.Client.PlaceAsync(productId);
        (await owner.DeclineAsync(declined)).EnsureSuccessStatusCode();
        var cancelled = await trader.Client.PlaceAsync(productId);
        (await trader.Client.CancelAsync(cancelled)).EnsureSuccessStatusCode();
        var notifiedOnce = (await factory.NotificationRowsAsync(cancelled.Id())).OfType(NotificationTypes.PurchaseRequestCancelled).Count();

        var first = await (await trader.Client.CancelAsync(declined)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var second = await (await trader.Client.CancelAsync(cancelled)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Equal("Purchase request is Declined and cannot be used for this action.", first.Text("detail"));
        Assert.Equal("Purchase request is Cancelled and cannot be used for this action.", second.Text("detail"));
        Assert.Equal(notifiedOnce, (await factory.NotificationRowsAsync(cancelled.Id())).OfType(NotificationTypes.PurchaseRequestCancelled).Count());
    }

    // BR-SALE-01: Fulfilled is worked out from the invoices (the hook SALE-05 fills); a request whose invoice is cancelled is Accepted again.
    [Fact]
    public async Task A_fulfilled_request_cannot_be_cancelled_and_can_again_once_it_no_longer_is()
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);
        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
        factory.Fulfilment.Fulfilled.Add(request.Id());

        var mine = await trader.Client.MineAsync(request.Id());
        Assert.Equal((StatusIds.PurchaseRequest.Accepted, "Fulfilled", true, false), (mine.GetProperty("statusId").GetInt32(), mine.Text("status"), mine.GetProperty("fulfilled").GetBoolean(), mine.GetProperty("canCancel").GetBoolean()));
        var seen = await owner.ViewedByStaffAsync(request.Id());
        Assert.Equal(("Fulfilled", true), (seen.Text("status"), seen.GetProperty("fulfilled").GetBoolean()));

        var refused = await (await trader.Client.CancelAsync(mine)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal("Purchase request is Fulfilled and cannot be used for this action.", refused.Text("detail"));

        factory.Fulfilment.Fulfilled.Remove(request.Id());
        var again = await trader.Client.MineAsync(request.Id());
        Assert.Equal(("Accepted", true), (again.Text("status"), again.GetProperty("canCancel").GetBoolean()));
        (await trader.Client.CancelAsync(again)).EnsureSuccessStatusCode();
    }

    // NFR-09: the trader sees the response before cancelling; the status comes first when it forbids the cancel.
    [Fact]
    public async Task A_form_read_before_the_owner_answered_gets_msg06_and_a_missing_version_is_msg_gen_01()
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);

        Assert.Equal(["RowVersion"], await (await trader.Client.PostAsJsonRawAsync($"/api/me/purchase-requests/{request.Id()}/cancel", "{}")).InvalidFieldsAsync());
        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
        var stale = Convert.ToBase64String(new byte[8]);
        var response = await trader.Client.PostAsJsonRawAsync($"/api/me/purchase-requests/{request.Id()}/cancel", $$"""{ "rowVersion": "{{stale}}" }""");

        await response.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
    }
}
