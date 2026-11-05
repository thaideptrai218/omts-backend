using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Messaging.Fakes;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of OMTS-223: races on a real SQL Server. 2xx or 4xx, never 500; one winner; one notification, push and SMS per act. Run these alone
/// (`~PurchaseRequestQaRaceSqlServerTests`) as well as in a group: a parallel test passing inside a large run has hidden a real bug here before.
/// </summary>
public class PurchaseRequestQaRaceSqlServerTests
{
    private const int Rounds = 15;

    private static async Task AssertOneResponseAsync(FarmingHost host, TraderActor trader, Guid requestId, int status)
    {
        Assert.Equal(status, (await host.Api.DbAsync(db => db.PurchaseRequests.AsNoTracking().SingleAsync(r => r.Id == requestId))).StatusId);
        var rows = await host.Api.NotificationRowsAsync(requestId);
        var responded = status is StatusIds.PurchaseRequest.Accepted or StatusIds.PurchaseRequest.Declined;
        Assert.Equal(responded ? 1 : 0, rows.InList().OfType(NotificationTypes.PurchaseRequestResponse).Count());
        Assert.Equal(responded ? 1 : 0, rows.Sendings().Count);
        Assert.All(rows.Sendings(), s => Assert.Equal(StatusIds.MessageDelivery.Sent, s.DeliveryStatusId));
        var pushes = host.Api.Services.GetRequiredService<FakePushSender>().Inbox.List(trader.Account.Id.ToString());
        var listRows = rows.InList().OfType(NotificationTypes.PurchaseRequestResponse).Select(n => (Guid?)n.Id).ToList();
        Assert.Equal(responded ? 1 : 0, pushes.Count(p => listRows.Contains(p.NotificationId)));
    }

    private static int SmsTo(FarmingHost host, TraderActor trader) =>
        host.Api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(trader.Profile.PhoneNumber!).Count;

    [SqlServerFact]
    public async Task Accept_against_cancel()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var sms = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var request = await trader.Client.PlaceAsync(productId);

            var answers = await InParallelAsync(2, i => i == 0 ? owner.AcceptAsync(request) : trader.Client.CancelAsync(request));

            await AssertAnswersAsync(answers, "200", "409 MSG06", "409 MSG-GEN-09");
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            var acceptWon = answers[0].StatusCode == HttpStatusCode.OK;
            sms += acceptWon ? 1 : 0;
            await AssertOneResponseAsync(host, trader, request.Id(), acceptWon ? StatusIds.PurchaseRequest.Accepted : StatusIds.PurchaseRequest.Cancelled);
            Assert.Equal(acceptWon ? 0 : 1, (await host.Api.NotificationRowsAsync(request.Id())).InList().OfType(NotificationTypes.PurchaseRequestCancelled).Count());
            Assert.Equal(sms, SmsTo(host, trader));
        }
    }

    [SqlServerFact]
    public async Task Accept_against_decline()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var request = await trader.Client.PlaceAsync(productId);

            var answers = await InParallelAsync(2, i => i == 0 ? owner.AcceptAsync(request) : owner.DeclineAsync(request));

            await AssertAnswersAsync(answers, "200", "409 MSG-GEN-09", "409 MSG06");
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            await AssertOneResponseAsync(host, trader, request.Id(), answers[0].StatusCode == HttpStatusCode.OK ? StatusIds.PurchaseRequest.Accepted : StatusIds.PurchaseRequest.Declined);
            Assert.Equal(round + 1, SmsTo(host, trader));
        }
    }

    [SqlServerFact]
    public async Task Accept_against_accept()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var request = await trader.Client.PlaceAsync(productId);

            var answers = await InParallelAsync(3, i => owner.AcceptAsync(request, 10m + i));

            await AssertAnswersAsync(answers, "200", "409 MSG-GEN-09", "409 MSG06");
            var winner = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            var stored = await host.Api.DbAsync(db => db.PurchaseRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id()));
            Assert.Equal(10m + Array.IndexOf(answers, winner), stored.AcceptedKg);
            await AssertOneResponseAsync(host, trader, request.Id(), StatusIds.PurchaseRequest.Accepted);
        }
    }

    [SqlServerFact]
    public async Task Cancel_against_cancel()
    {
        await using var host = await FarmingHost.StartAsync();
        var (_, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var request = await trader.Client.PlaceAsync(productId);

            var answers = await InParallelAsync(3, _ => trader.Client.CancelAsync(request, "Đổi lịch"));

            await AssertAnswersAsync(answers, "200", "409 MSG-GEN-09", "409 MSG06");
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            var rows = await host.Api.NotificationRowsAsync(request.Id());
            Assert.Single(rows.InList().OfType(NotificationTypes.PurchaseRequestCancelled));
            Assert.Single(await host.Api.AuditRowsAsync(request.Id()), e => e.Action == "PurchaseRequest.Cancelled");
        }
    }

    // The trader's request and the merge of a duplicate into the trader's profile: nothing stays on the merged profile.
    [SqlServerFact]
    public async Task Place_against_merge_into_the_traders_profile()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var offline = await owner.CreateCustomerAsync();
            var kept = await owner.CustomerAsync(trader.Profile.Id);
            var merged = await owner.CustomerAsync(offline.Id());

            var answers = await InParallelAsync(3, i => i == 0
                ? owner.PostMergeAsync(trader.Profile.Id, offline.Id(), kept.RowVersion(), merged.RowVersion())
                : trader.Client.PostRequestAsync(productId));

            await AssertAnswersAsync([answers[0]], "200", "409 MSG06");
            await AssertAnswersAsync(answers[1..], "201", "409 MSG06");
            Assert.Equal(0, await host.Api.DbAsync(db => db.PurchaseRequests.CountAsync(r => r.CustomerId == offline.Id())));
        }
    }

    // A trader who taps Send during a discontinue: the code says the request may name the product; the state stays whole.
    [SqlServerFact]
    public async Task Place_against_discontinue()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var trader = await PurchaseRequestSqlServerTests.TraderOnAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var product = await owner.CreateProductAsync();

            var answers = await InParallelAsync(2, i => i == 0
                ? trader.Client.PostRequestAsync(product.Id())
                : owner.PostAsync($"/api/products/{product.Id()}/discontinue", null));

            await AssertAnswersAsync([answers[0]], "201", "400 MSG-GEN-09", "409 MSG06");
            await AssertAnswersAsync([answers[1]], "200", "409 MSG06");
            var saved = await host.Api.DbAsync(db => db.PurchaseRequests.CountAsync(r => r.ProductId == product.Id()));
            Assert.Equal(answers[0].StatusCode == HttpStatusCode.Created ? 1 : 0, saved);
        }
    }

    // Two owners and a deadlock-prone mix on one profile: parallel requests of a trader while the Owner answers others.
    [SqlServerFact]
    public async Task Places_answers_and_cancels_of_one_profile_at_one_moment_never_answer_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var requests = new List<JsonElement>();
            for (var i = 0; i < 3; i++)
            {
                requests.Add(await trader.Client.PlaceAsync(productId));
            }

            var answers = await InParallelAsync(6, i => i switch
            {
                0 => owner.AcceptAsync(requests[0]),
                1 => owner.DeclineAsync(requests[1]),
                2 => trader.Client.CancelAsync(requests[2]),
                _ => trader.Client.PostRequestAsync(productId),
            });

            await AssertAnswersAsync(answers[..3], "200");
            await AssertAnswersAsync(answers[3..], "201", "409 MSG06");
        }
    }
}
