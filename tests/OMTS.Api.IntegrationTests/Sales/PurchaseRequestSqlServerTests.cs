using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-01 to SALE-03 on a real SQL Server: races answering 2xx or 409, never 500.</summary>
public class PurchaseRequestSqlServerTests
{
    private const int Rounds = 8;

    internal static async Task<TraderActor> TraderOnAsync(FarmingHost host, string name = "Thương lái")
    {
        using var scope = host.Api.Services.CreateScope();
        var account = new Account
        {
            Username = NewCode(), DisplayName = name, SubjectType = SubjectType.Trader, StatusId = StatusIds.Account.Active,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password),
            PhoneNumber = NewPhone(), PhoneVerifiedAt = DateTimeOffset.UtcNow,
        };
        await host.UseDbAsync(db => db.Accounts.Add(account));
        var profile = new Customer { Code = OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit.NewCustomerCode(), CooperativeId = host.CooperativeId, AccountId = account.Id, Name = name, PhoneNumber = account.PhoneNumber };
        await host.UseDbAsync(db => db.Customers.Add(profile));
        return new TraderActor(await host.LoginAsync(account.Username), account, profile);
    }

    internal static async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId)> WorldAsync(FarmingHost host) =>
        (host.Owner.Client, await TraderOnAsync(host), (await host.Owner.Client.CreateProductAsync()).Id());

    private static Task<HttpResponseMessage> Cancel(HttpClient trader, JsonElement request) => trader.CancelAsync(request);

    [SqlServerFact]
    public async Task The_owners_accept_and_the_traders_cancel_at_one_moment_leave_one_consistent_result()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var request = await trader.Client.PlaceAsync(productId);

            var answers = await InParallelAsync(2, i => i == 0 ? owner.AcceptAsync(request) : Cancel(trader.Client, request));

            await AssertAnswersAsync(answers, "200", "409 MSG06", "409 MSG-GEN-09");
            Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
            await AssertConsistentAsync(host, request.Id(), acceptWon: answers[0].StatusCode == HttpStatusCode.OK, cancelWon: answers[1].StatusCode == HttpStatusCode.OK);
        }
    }

    // A cancel may follow an accept that it raced with only after reading the response again, so both can never succeed on one version.
    private static async Task AssertConsistentAsync(FarmingHost host, Guid requestId, bool acceptWon, bool cancelWon)
    {
        Assert.NotEqual(acceptWon, cancelWon);
        var stored = await host.Api.DbAsync(db => db.PurchaseRequests.AsNoTracking().SingleAsync(r => r.Id == requestId));
        Assert.Equal(acceptWon ? StatusIds.PurchaseRequest.Accepted : StatusIds.PurchaseRequest.Cancelled, stored.StatusId);
        var rows = await host.Api.NotificationRowsAsync(requestId);
        Assert.Equal(acceptWon ? 1 : 0, rows.Sendings().Count);
        Assert.Equal(acceptWon ? 1 : 0, rows.InList().OfType(NotificationTypes.PurchaseRequestResponse).Count());
        Assert.Equal(cancelWon ? 1 : 0, rows.InList().OfType(NotificationTypes.PurchaseRequestCancelled).Count());
        var audit = await host.Api.AuditRowsAsync(requestId);
        Assert.Equal(acceptWon ? 1 : 0, audit.Count(e => e.Action == "PurchaseRequest.Accepted"));
        Assert.Equal(cancelWon ? 1 : 0, audit.Count(e => e.Action == "PurchaseRequest.Cancelled"));
    }

    [SqlServerFact]
    public async Task Parallel_answers_save_one_response_and_send_one_message()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var request = await trader.Client.PlaceAsync(productId);

            var answers = await InParallelAsync(6, i => i % 2 == 0 ? owner.AcceptAsync(request) : owner.DeclineAsync(request));

            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            await AssertAnswersAsync(answers.Where(a => a.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
            var rows = await host.Api.NotificationRowsAsync(request.Id());
            Assert.Single(rows.Sendings());
            Assert.Single(rows.InList().OfType(NotificationTypes.PurchaseRequestResponse));
        }
    }

    [SqlServerFact]
    public async Task Parallel_cancels_save_one_and_notify_once()
    {
        await using var host = await FarmingHost.StartAsync();
        var (_, trader, productId) = await WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var request = await trader.Client.PlaceAsync(productId);

            var answers = await InParallelAsync(4, _ => Cancel(trader.Client, request));

            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            await AssertAnswersAsync(answers.Where(a => a.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
            Assert.Single((await host.Api.NotificationRowsAsync(request.Id())).InList().OfType(NotificationTypes.PurchaseRequestCancelled));
        }
    }

    // MarkChanged(customer): a second tap on Send at the same moment conflicts, and whatever is saved is whole.
    [SqlServerFact]
    public async Task Parallel_requests_of_one_trader_never_answer_500_and_each_saved_one_has_its_notification()
    {
        await using var host = await FarmingHost.StartAsync();
        var (_, trader, productId) = await WorldAsync(host);

        var answers = await InParallelAsync(6, _ => trader.Client.PostRequestAsync(productId));

        await AssertAnswersAsync(answers, "201", "409 MSG06");
        var saved = answers.Count(a => a.StatusCode == HttpStatusCode.Created);
        Assert.True(saved >= 1);
        var requests = await host.Api.DbAsync(db => db.PurchaseRequests.AsNoTracking().Where(r => r.CustomerId == trader.Profile.Id).Select(r => r.Id).ToListAsync());
        Assert.Equal(saved, requests.Count);
        foreach (var id in requests)
        {
            Assert.Single((await host.Api.NotificationRowsAsync(id)).InList().OfType(NotificationTypes.PurchaseRequestNew));
        }
    }

    [SqlServerFact]
    public async Task Requests_of_different_traders_for_one_product_do_not_conflict()
    {
        await using var host = await FarmingHost.StartAsync();
        var (_, _, productId) = await WorldAsync(host);
        var traders = new[] { await TraderOnAsync(host), await TraderOnAsync(host), await TraderOnAsync(host), await TraderOnAsync(host) };

        var answers = await InParallelAsync(traders.Length, i => traders[i].Client.PostRequestAsync(productId));

        await AssertAnswersAsync(answers, "201");
    }

    [SqlServerFact]
    public async Task A_request_placed_while_the_owner_discontinues_the_product_never_answers_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var trader = await TraderOnAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var product = await owner.CreateProductAsync();

            var answers = await InParallelAsync(2, i => i == 0 ? trader.Client.PostRequestAsync(product.Id()) : owner.PostAsync($"/api/products/{product.Id()}/discontinue", null));

            await AssertAnswersAsync([answers[0]], "201", "400 MSG-GEN-09", "409 MSG06");
            await AssertAnswersAsync([answers[1]], "200", "409 MSG06");
        }
    }

    // The merge writes the kept profile and the moved rows: a request, an answer or a cancel at that moment conflicts, or goes through whole.
    [SqlServerFact]
    public async Task A_merge_into_the_traders_profile_while_the_trader_sends_and_the_owner_answers_never_answers_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await WorldAsync(host);
        for (var round = 0; round < Rounds; round++)
        {
            var offline = await owner.CreateCustomerAsync();
            var moved = new PurchaseRequest { CustomerId = offline.Id(), ProductId = productId, RequestedKg = 10, PickupDate = PurchaseRequestTestKit.Today, SentAt = DateTimeOffset.UtcNow };
            await host.UseDbAsync(db => db.PurchaseRequests.Add(moved));
            var kept = await owner.CustomerAsync(trader.Profile.Id);
            var merged = await owner.CustomerAsync(offline.Id());
            var form = await owner.ViewedByStaffAsync(moved.Id);

            var answers = await InParallelAsync(3, i => i switch
            {
                0 => owner.PostMergeAsync(trader.Profile.Id, offline.Id(), kept.RowVersion(), merged.RowVersion()),
                1 => trader.Client.PostRequestAsync(productId),
                _ => owner.AcceptAsync(form),
            });

            await AssertAnswersAsync([answers[0]], "200", "409 MSG06");
            await AssertAnswersAsync([answers[1]], "201", "409 MSG06");
            await AssertAnswersAsync([answers[2]], "200", "409 MSG06");
            var mergeWon = answers[0].StatusCode == HttpStatusCode.OK;
            var stored = await host.Api.DbAsync(db => db.PurchaseRequests.AsNoTracking().SingleAsync(r => r.Id == moved.Id));
            Assert.Equal(mergeWon ? trader.Profile.Id : offline.Id(), stored.CustomerId);
        }
    }
}
