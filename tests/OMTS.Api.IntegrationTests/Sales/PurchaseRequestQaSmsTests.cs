using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: the SMS row of the response: Sending, Sent, Not sent; after the commit; nothing undone (BR-SALE-09, GFR-06, SALE-03 step 8).</summary>
public class PurchaseRequestQaSmsTests(PurchaseRequestQaSendFactory factory) : IClassFixture<PurchaseRequestQaSendFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, JsonElement Request)> RequestAsync()
    {
        factory.Sender.Send = _ => Task.FromResult(DeliveryResult.Sent("stub"));
        factory.Gate.FailSaveNumber = 0;
        var owner = await factory.PurchasingOwnerAsync();
        var trader = await factory.TraderAsync();
        return (owner, trader, await trader.Client.PlaceAsync((await owner.CreateProductAsync()).Id()));
    }

    private Task<Notification> SendingAsync(Guid requestId) =>
        factory.DbAsync(async db => (await factory.NotificationRowsAsync(requestId)).Sendings().Single());

    // After the commit only: while the service holds the message the response is saved and the row says Sending, for every reader.
    [Fact]
    public async Task While_the_service_holds_the_message_the_response_is_committed_and_the_row_says_sending()
    {
        var (owner, _, request) = await RequestAsync();
        (int Status, int? Delivery, string? StaffSees)? during = null;
        factory.Sender.Send = async _ =>
        {
            var stored = await factory.DbAsync(db => db.PurchaseRequests.FindAsync(request.Id()).AsTask());
            var view = await owner.ViewedByStaffAsync(request.Id());
            during = (stored!.StatusId, (await SendingAsync(request.Id())).DeliveryStatusId, view.GetProperty("sendings")[0].Text("deliveryStatus"));
            return DeliveryResult.Sent("id-1");
        };

        var answer = await (await owner.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((StatusIds.PurchaseRequest.Accepted, StatusIds.MessageDelivery.Sending, "Sending"), during);
        Assert.Equal("Sent", answer.GetProperty("sendings")[0].Text("deliveryStatus"));
        var row = await SendingAsync(request.Id());
        Assert.Equal((StatusIds.MessageDelivery.Sent, "id-1", (string?)null), (row.DeliveryStatusId, row.ProviderMessageId, row.ProviderErrorCode));
        Assert.NotNull(row.DeliveryStatusAt);
    }

    // A failed first save: nothing is sent, nothing is kept, and the status stays.
    [Fact]
    public async Task A_save_that_fails_sends_nothing()
    {
        var (owner, trader, request) = await RequestAsync();
        var calls = 0;
        factory.Sender.Send = _ =>
        {
            calls++;
            return Task.FromResult(DeliveryResult.Sent("x"));
        };
        factory.Gate.FailSaveNumber = 1;

        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.AcceptAsync(request)).StatusCode);

        factory.Gate.FailSaveNumber = 0;
        Assert.Equal(0, calls);
        Assert.Empty((await factory.NotificationRowsAsync(request.Id())).Sendings());
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingResponse, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
    }

    // The second save fails after the service took the message. GFR-06 holds: 200, saved. The row stays Sending, and nothing ends that (the code says so).
    [Fact]
    public async Task A_failed_save_of_the_delivery_status_undoes_nothing_and_leaves_the_row_at_sending()
    {
        var (owner, trader, request) = await RequestAsync();
        factory.Gate.FailSaveNumber = 2;

        var answer = await (await owner.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);

        factory.Gate.FailSaveNumber = 0;
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
        Assert.Equal("Sending", answer.GetProperty("sendings")[0].Text("deliveryStatus"));
        Assert.Equal(StatusIds.MessageDelivery.Sending, (await SendingAsync(request.Id())).DeliveryStatusId);
        Assert.Contains(factory.Logs.Lines, l => l.Contains("could not be saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_slow_service_within_the_limit_is_sent()
    {
        var (owner, _, request) = await RequestAsync();
        factory.Sender.Send = async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            return DeliveryResult.Sent("slow");
        };

        var answer = await (await owner.DeclineAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Sent", answer.GetProperty("sendings")[0].Text("deliveryStatus"));
    }

    // A sender that blocks its thread and ignores its token: the answer still comes at the limit.
    [Fact]
    public async Task A_service_that_blocks_its_thread_holds_the_answer_no_longer_than_the_limit()
    {
        var (owner, trader, request) = await RequestAsync();
        factory.Sender.Send = _ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(16));
            return Task.FromResult(DeliveryResult.Sent("late"));
        };
        var clock = Stopwatch.StartNew();

        var answer = await (await owner.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Took {clock.Elapsed}.");
        Assert.Equal("Not sent", answer.GetProperty("sendings")[0].Text("deliveryStatus"));
        Assert.Equal("SEND_FAILED", (await SendingAsync(request.Id())).ProviderErrorCode);
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
    }

    // The client gives up while the service works: the data is committed, so the row still ends Sent.
    [Fact]
    public async Task A_client_that_gives_up_during_the_send_does_not_leave_the_row_at_sending()
    {
        var (owner, _, request) = await RequestAsync();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        factory.Sender.Send = async _ =>
        {
            started.SetResult();
            await release.Task;
            return DeliveryResult.Sent("after-abort");
        };
        using var abort = new CancellationTokenSource();
        var call = owner.PostAsJsonAsync($"/api/purchase-requests/{request.Id()}/accept", new { acceptedCages = 100m, pickupDate = PurchaseRequestTestKit.Today, payLaterAgreed = false, rowVersion = request.RowVersion() }, abort.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        abort.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        var deadline = Stopwatch.StartNew();
        Notification row;
        do
        {
            await Task.Delay(50);
            row = await SendingAsync(request.Id());
        }
        while (row.DeliveryStatusId == StatusIds.MessageDelivery.Sending && deadline.Elapsed < TimeSpan.FromSeconds(10));

        Assert.Equal(StatusIds.MessageDelivery.Sent, row.DeliveryStatusId);
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(81, true)]
    [InlineData(1000, true)]
    public async Task A_note_is_cut_only_above_80_characters_in_the_sms_and_stays_whole_elsewhere(int length, bool cut)
    {
        var (owner, _, request) = await RequestAsync();
        var note = string.Concat(Enumerable.Repeat("ữ", length));

        var answer = await (await owner.DeclineAsync(request, note)).ReadAsync(HttpStatusCode.OK);

        var rows = await factory.NotificationRowsAsync(request.Id());
        var sms = rows.Sendings().Single();
        var expected = cut ? note[..80] + "..." : note;
        Assert.EndsWith($"is declined. {expected}", sms.Body);
        Assert.Equal(expected, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sms.MessageParams!)!["note"].GetString());
        Assert.EndsWith($"is declined. {note}", rows.InList().OfType(NotificationTypes.PurchaseRequestResponse).Single().Body);
        Assert.Equal(note, answer.GetProperty("response").Text("note"));
    }

    // MSG-SALE-03 goes to a phone: the cut must not break a character. Vietnamese letters are one char, an emoji is two.
    [Fact]
    public async Task L2_the_cut_at_80_never_leaves_half_a_character_in_the_sms()
    {
        var (owner, _, request) = await RequestAsync();
        var note = new string('a', 79) + "😀 cảm ơn anh";

        (await owner.DeclineAsync(request, note)).EnsureSuccessStatusCode();

        var body = (await SendingAsync(request.Id())).Body!;
        for (var i = 0; i < body.Length; i++)
        {
            if (char.IsHighSurrogate(body[i]))
            {
                Assert.True(i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]), $"Half an emoji at {i} of the SMS text: {body}");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(body[i]), $"Half an emoji at {i} of the SMS text: {body}");
            }
        }
    }

    // A locked trader has no list row (only active accounts are told), and the profile's number still gets the SMS (BR-SALE-03 names the profile).
    [Fact]
    public async Task A_locked_trader_gets_no_list_row_and_the_number_of_the_profile_still_gets_the_sms()
    {
        var (owner, trader, request) = await RequestAsync();
        await factory.UpdateAsync<Account>(trader.Account.Id, a => a.StatusId = StatusIds.Account.Locked);

        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        var rows = (await factory.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestResponse).ToList();
        Assert.Empty(rows.InList());
        Assert.Equal((trader.Profile.PhoneNumber, StatusIds.MessageDelivery.Sent), (rows.Sendings().Single().PhoneNumber, rows.Sendings().Single().DeliveryStatusId));
    }
}
