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

/// <summary>SALE-03, BR-SALE-02: the Owner accepts, at most the requested kg, or declines with a note, once.</summary>
public class PurchaseRequestResponseTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId, string ProductName)> WorldAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(name: $"Hàu {NewCode()}");
        return (owner, await factory.TraderAsync(), product.Id(), product.Text("name")!);
    }

    [Fact]
    public async Task Accept_saves_fewer_kg_another_date_pay_later_and_the_note_with_an_audit_row()
    {
        var (owner, trader, productId, _) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId, 300);
        var pickup = Today.AddDays(5);

        var view = await (await owner.AcceptAsync(request, 250, pickup, payLater: true, note: "  Đủ 250 kg ")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(StatusIds.PurchaseRequest.Accepted, view.GetProperty("statusId").GetInt32());
        var response = view.GetProperty("response");
        Assert.Equal(250, response.GetProperty("acceptedCages").GetDecimal());
        Assert.Equal(pickup, response.GetProperty("pickupDate").GetFromJson<DateOnly>());
        Assert.True(response.GetProperty("payLaterAgreed").GetBoolean());
        Assert.Equal("Đủ 250 kg", response.Text("note"));
        Assert.Equal(300, view.GetProperty("requestedCages").GetDecimal());
        var audit = (await factory.AuditRowsAsync(request.Id())).Single(e => e.Action == "PurchaseRequest.Accepted");
        Assert.Contains("250", audit.After);
        Assert.Contains("ACCEPTED", audit.After);
        Assert.Contains("AWAITING_RESPONSE", audit.Before);
        Assert.NotNull(audit.ActorAccountId);
        Assert.NotEqual(trader.Account.Id, audit.ActorAccountId);
    }

    [Fact]
    public async Task The_requested_cages_itself_may_be_accepted_and_a_kg_above_it_never()
    {
        var (owner, trader, productId, _) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId, 100);

        foreach (var cages in new[] { 101, 0, -1 })
        {
            Assert.Equal(["Accepted cages"], await (await owner.AcceptAsync(request, cages)).InvalidFieldsAsync());
        }

        Assert.Equal(StatusIds.PurchaseRequest.AwaitingResponse, (await owner.ViewedByStaffAsync(request.Id())).GetProperty("statusId").GetInt32());
        (await owner.AcceptAsync(request, 100)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_date_is_not_before_today_and_every_field_to_fix_is_pointed_out()
    {
        var (owner, trader, productId, _) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId, 100, Today);

        Assert.Equal(["Pickup date"], await (await owner.AcceptAsync(request, 10, Today.AddDays(-1))).InvalidFieldsAsync());
        var nothing = await owner.PostAsJsonRawAsync($"/api/purchase-requests/{request.Id()}/accept", $$"""{ "note": "{{new string('x', 1001)}}", "rowVersion": "{{request.RowVersion()}}" }""");
        Assert.Equal(["Accepted cages", "Pickup date", "Pay later", "Note"], await nothing.InvalidFieldsAsync());

        (await owner.AcceptAsync(request, 10, Today)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Decline_needs_a_note_that_shows_something_and_checks_nothing_else()
    {
        var (owner, trader, productId, _) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId, 100, Today);

        foreach (var note in new[] { null, "", "   ", "​", new string('x', 1001) })
        {
            Assert.Equal(["Note"], await (await owner.DeclineAsync(request, note)).InvalidFieldsAsync());
        }

        var view = await (await owner.DeclineAsync(request, "Chưa đủ hàu")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(StatusIds.PurchaseRequest.Declined, view.GetProperty("statusId").GetInt32());
        var response = view.GetProperty("response");
        Assert.Equal("Chưa đủ hàu", response.Text("note"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, response.GetProperty("acceptedCages").ValueKind);
        Assert.Single(await factory.AuditRowsAsync(request.Id()), e => e.Action == "PurchaseRequest.Declined");
    }

    // BR-SALE-02, SALE-03.0.E1: a response cannot be changed once sent; a cancelled request shows its status, also on a stale form.
    [Fact]
    public async Task Only_an_awaiting_request_is_answered_and_a_refused_answer_sends_nothing()
    {
        var (owner, trader, productId, _) = await WorldAsync();
        var accepted = await trader.Client.PlaceAsync(productId);
        (await owner.AcceptAsync(accepted)).EnsureSuccessStatusCode();
        var declined = await trader.Client.PlaceAsync(productId);
        (await owner.DeclineAsync(declined)).EnsureSuccessStatusCode();
        var cancelled = await trader.Client.PlaceAsync(productId);
        (await trader.Client.CancelAsync(cancelled)).EnsureSuccessStatusCode();

        foreach (var (request, status) in new[] { (accepted, "Accepted"), (declined, "Declined"), (cancelled, "Cancelled") })
        {
            var rowsBefore = (await factory.NotificationRowsAsync(request.Id())).Count;
            foreach (var answer in new[] { await owner.AcceptAsync(request), await owner.DeclineAsync(request) })
            {
                var problem = await answer.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
                Assert.Equal($"Purchase request is {status} and cannot be used for this action.", problem.Text("detail"));
            }

            Assert.Equal(rowsBefore, (await factory.NotificationRowsAsync(request.Id())).Count);
        }
    }

    [Fact]
    public async Task A_missing_version_is_msg_gen_01_a_stale_one_msg06_and_an_unknown_request_404()
    {
        var (owner, trader, productId, _) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);
        var path = $"/api/purchase-requests/{request.Id()}";

        Assert.Equal(["RowVersion"], await (await owner.PostAsJsonRawAsync($"{path}/decline", """{ "note": "No" }""")).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.PostAsJsonRawAsync($"{path}/accept", """{ "rowVersion": "not base64!" }""")).InvalidFieldsAsync());
        await (await owner.PostAsJsonRawAsync($"{path}/decline", $$"""{ "note": "No", "rowVersion": "{{Convert.ToBase64String(new byte[8])}}" }""")).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonRawAsync($"/api/purchase-requests/{Guid.NewGuid()}/decline", """{ "note": "No", "rowVersion": "" }""")).StatusCode);
    }
}
