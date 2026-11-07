using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: every move of a request and every illegal one (BR-SALE-01, BR-SALE-02, SALE-02.0.E1, SALE-03.0.E1), and which message wins.</summary>
public class PurchaseRequestQaStateMachineTests(PurchaseRequestStubsFactory factory) : IClassFixture<PurchaseRequestStubsFactory>
{
    public enum Start
    {
        Awaiting,
        Accepted,
        Declined,
        Cancelled,
        Fulfilled,
    }

    public enum Move
    {
        Accept,
        Decline,
        Cancel,
    }

    private async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId)> WorldAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        return (owner, await factory.TraderAsync(), (await owner.CreateProductAsync()).Id());
    }

    private async Task<JsonElement> InStateAsync(HttpClient owner, TraderActor trader, Guid productId, Start start)
    {
        var request = await trader.Client.PlaceAsync(productId);
        switch (start)
        {
            case Start.Accepted:
            case Start.Fulfilled:
                (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
                break;
            case Start.Declined:
                (await owner.DeclineAsync(request)).EnsureSuccessStatusCode();
                break;
            case Start.Cancelled:
                (await trader.Client.CancelAsync(request)).EnsureSuccessStatusCode();
                break;
        }

        if (start == Start.Fulfilled)
        {
            factory.Fulfilment.Fulfilled.Add(request.Id());
        }

        return await trader.Client.MineAsync(request.Id());
    }

    private static Task<HttpResponseMessage> RunAsync(Move move, HttpClient owner, TraderActor trader, JsonElement request) => move switch
    {
        Move.Accept => owner.AcceptAsync(request),
        Move.Decline => owner.DeclineAsync(request),
        _ => trader.Client.CancelAsync(request),
    };

    [Theory]
    [InlineData(Start.Awaiting, Move.Accept, StatusIds.PurchaseRequest.Accepted)]
    [InlineData(Start.Awaiting, Move.Decline, StatusIds.PurchaseRequest.Declined)]
    [InlineData(Start.Awaiting, Move.Cancel, StatusIds.PurchaseRequest.Cancelled)]
    [InlineData(Start.Accepted, Move.Cancel, StatusIds.PurchaseRequest.Cancelled)]
    public async Task A_legal_move_is_saved(Start start, Move move, int expected)
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await InStateAsync(owner, trader, productId, start);

        (await RunAsync(move, owner, trader, request)).EnsureSuccessStatusCode();

        Assert.Equal(expected, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
    }

    // SALE-02.0.E1 and SALE-03.0.E1: nothing changes, nothing is sent, and the status is shown. A Fulfilled request reads as Fulfilled.
    [Theory]
    [InlineData(Start.Accepted, Move.Accept, "Accepted")]
    [InlineData(Start.Accepted, Move.Decline, "Accepted")]
    [InlineData(Start.Declined, Move.Accept, "Declined")]
    [InlineData(Start.Declined, Move.Decline, "Declined")]
    [InlineData(Start.Declined, Move.Cancel, "Declined")]
    [InlineData(Start.Cancelled, Move.Accept, "Cancelled")]
    [InlineData(Start.Cancelled, Move.Decline, "Cancelled")]
    [InlineData(Start.Cancelled, Move.Cancel, "Cancelled")]
    [InlineData(Start.Fulfilled, Move.Cancel, "Fulfilled")]
    public async Task An_illegal_move_changes_nothing_sends_nothing_and_shows_the_status(Start start, Move move, string status)
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await InStateAsync(owner, trader, productId, start);
        var rows = (await factory.NotificationRowsAsync(request.Id())).Count;
        var audit = (await factory.AuditRowsAsync(request.Id())).Count;

        var refused = await (await RunAsync(move, owner, trader, request)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Equal($"Purchase request is {status} and cannot be used for this action.", refused.GetProperty("detail").GetString());
        var after = await trader.Client.MineAsync(request.Id());
        Assert.Equal(request.GetProperty("statusId").GetInt32(), after.GetProperty("statusId").GetInt32());
        Assert.Equal(rows, (await factory.NotificationRowsAsync(request.Id())).Count);
        Assert.Equal(audit, (await factory.AuditRowsAsync(request.Id())).Count);
    }

    // SALE-03.0.E1 "shows its status", and SCR-SALE-02 reads the same request as Fulfilled. Reached once SALE-05 fills the hook.
    [Theory]
    [InlineData(Move.Accept)]
    [InlineData(Move.Decline)]
    public async Task L3_answering_a_fulfilled_request_shows_fulfilled_as_the_screen_does(Move move)
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await InStateAsync(owner, trader, productId, Start.Fulfilled);
        Assert.Equal("Fulfilled", (await owner.ViewedByStaffAsync(request.Id())).Text("status"));

        var refused = await (await RunAsync(move, owner, trader, request)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Equal("Purchase request is Fulfilled and cannot be used for this action.", refused.GetProperty("detail").GetString());
    }

    // What wins when two things are wrong. The status first (the client list, §1 and §2), then the version, then the fields.
    [Fact]
    public async Task The_status_wins_over_a_stale_or_missing_version_and_the_version_over_the_fields()
    {
        var (owner, trader, productId) = await WorldAsync();
        var declined = await InStateAsync(owner, trader, productId, Start.Declined);
        foreach (var version in new[] { "null", "\"%%%\"", "\"AAAAAAAAAAA=\"" })
        {
            await (await owner.PostAsJsonRawAsync($"/api/purchase-requests/{declined.Id()}/accept", $$"""{ "rowVersion": {{version}} }""")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
            await (await trader.Client.PostAsJsonRawAsync($"/api/me/purchase-requests/{declined.Id()}/cancel", $$"""{ "rowVersion": {{version}} }""")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        }

        var awaiting = await trader.Client.PlaceAsync(productId);
        Assert.Equal(["RowVersion"], await (await owner.PostAsJsonRawAsync($"/api/purchase-requests/{awaiting.Id()}/decline", """{ "note": "" }""")).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await trader.Client.PostAsJsonRawAsync($"/api/me/purchase-requests/{awaiting.Id()}/cancel", "{}")).InvalidFieldsAsync());
    }

    // BR-SALE-01, §1.3 of the design: the hook decides Fulfilled; it is read once per list, and only for Accepted rows.
    [Fact]
    public async Task The_fulfilment_hook_is_enough_to_test_a_fulfilled_request_end_to_end()
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await InStateAsync(owner, trader, productId, Start.Fulfilled);

        var mine = await trader.Client.MineAsync(request.Id());
        Assert.Equal((StatusIds.PurchaseRequest.Accepted, "Fulfilled", true, false), (mine.GetProperty("statusId").GetInt32(), mine.Text("status"), mine.GetProperty("fulfilled").GetBoolean(), mine.GetProperty("canCancel").GetBoolean()));
        var staff = await owner.ViewedByStaffAsync(request.Id());
        Assert.Equal((StatusIds.PurchaseRequest.Accepted, "Fulfilled", true), (staff.GetProperty("statusId").GetInt32(), staff.Text("status"), staff.GetProperty("fulfilled").GetBoolean()));

        // SALE-08: the invoice is cancelled, so it is Accepted again and can be cancelled.
        factory.Fulfilment.Fulfilled.Remove(request.Id());
        mine = await trader.Client.MineAsync(request.Id());
        Assert.Equal(("Accepted", false, true), (mine.Text("status"), mine.GetProperty("fulfilled").GetBoolean(), mine.GetProperty("canCancel").GetBoolean()));
        (await trader.Client.CancelAsync(mine)).EnsureSuccessStatusCode();
    }

    // SALE-02: cancelling after acceptance tells the Owner and sends the trader nothing.
    [Fact]
    public async Task A_cancel_after_acceptance_tells_the_owner_and_sends_no_new_message_to_the_trader()
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);
        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
        var sendingsBefore = (await factory.NotificationRowsAsync(request.Id())).Sendings().Count;

        (await trader.Client.CancelAsync(await trader.Client.MineAsync(request.Id()))).EnsureSuccessStatusCode();

        var rows = await factory.NotificationRowsAsync(request.Id());
        Assert.Equal(sendingsBefore, rows.Sendings().Count);
        Assert.Equal(rows.InList().OfType(NotificationTypes.PurchaseRequestNew).Count(), rows.InList().OfType(NotificationTypes.PurchaseRequestCancelled).Count());
        Assert.DoesNotContain(rows.InList().OfType(NotificationTypes.PurchaseRequestCancelled), n => n.RecipientAccountId == trader.Account.Id);
        Assert.Single(rows.InList().OfType(NotificationTypes.PurchaseRequestResponse));
    }
}
