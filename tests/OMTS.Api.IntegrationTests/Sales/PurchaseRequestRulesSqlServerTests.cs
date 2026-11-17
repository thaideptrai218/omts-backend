using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-01 to SALE-03 on a real SQL Server: row versions, column sizes and constraints.</summary>
public class PurchaseRequestRulesSqlServerTests
{
    private static async Task<(WebApplicationFactory<Program> Hooked, HttpClient Trader, StubFulfilment Hook)> HookedTraderAsync(FarmingHost host, TraderActor trader)
    {
        var hook = new StubFulfilment();
        var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPurchaseRequestFulfilment>(hook)));
        var client = hooked.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginTokensAsync(client, trader.Account.Username)).GetProperty("accessToken").GetString());
        return (hooked, client, hook);
    }

    // The one outcome of the race, every time: the Owner's answer commits after the cancel read the row. The cancel's own form is current
    // for what it read, so only the database stops it, and SALE-02.0.E1 shows the status that won.
    [SqlServerFact]
    public async Task A_cancel_that_loses_to_a_decline_in_the_database_answers_msg_gen_09_and_leaves_nothing_of_itself()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var (hooked, client, hook) = await HookedTraderAsync(host, trader);
        await using var _ = hooked;
        var request = await client.PlaceAsync(productId);
        hook.DuringNextCheck = async () => (await owner.DeclineAsync(request)).EnsureSuccessStatusCode();

        var refused = await (await client.CancelAsync(request)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Equal("Purchase request is Declined and cannot be used for this action.", refused.GetProperty("detail").GetString());
        var stored = await host.Api.DbAsync(db => db.PurchaseRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id()));
        Assert.Equal((StatusIds.PurchaseRequest.Declined, (DateTimeOffset?)null), (stored.StatusId, stored.CancelledAt));
        Assert.DoesNotContain(await host.Api.AuditRowsAsync(request.Id()), e => e.Action == "PurchaseRequest.Cancelled");
        Assert.Empty((await host.Api.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestCancelled));
    }

    // SALE-03.0.E1 for the loser of a race too: another answer and an invoice taking it in full commit after this answer read the row.
    [SqlServerFact]
    public async Task An_answer_that_loses_to_a_request_now_fulfilled_shows_fulfilled_as_the_screen_does()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var request = await trader.Client.PlaceAsync(productId);
        var fulfilment = new StubFulfilment();
        var beforeTheSave = new RunsBeforeTheSave(async () =>
        {
            (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
            fulfilment.Fulfilled.Add(request.Id());
        });
        await using var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IPurchaseRequestFulfilment>(fulfilment);
            s.AddSingleton<INotificationDispatcher>(beforeTheSave);
        }));
        var client = hooked.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginTokensAsync(client, host.Owner.Account.Username)).GetProperty("accessToken").GetString());

        var refused = await (await client.DeclineAsync(request)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Equal("Purchase request is Fulfilled and cannot be used for this action.", refused.GetProperty("detail").GetString());
    }

    private sealed class RunsBeforeTheSave(Func<Task> action) : INotificationDispatcher
    {
        public Task NotifyAsync(IReadOnlyCollection<Guid> recipientAccountIds, NotificationContent content, CancellationToken cancellationToken) => action();
    }

    // An Accepted request may still be cancelled, so the loser is told to read again, not refused, and the trader sees the response first.
    [SqlServerFact]
    public async Task A_cancel_that_loses_to_an_accept_in_the_database_answers_msg06_and_can_be_repeated_after_reading()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var (hooked, client, hook) = await HookedTraderAsync(host, trader);
        await using var _ = hooked;
        var request = await client.PlaceAsync(productId);
        hook.DuringNextCheck = async () => (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        await (await client.CancelAsync(request)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        var read = await client.MineAsync(request.Id());
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, read.GetProperty("statusId").GetInt32());
        Assert.Equal(StatusIds.PurchaseRequest.Cancelled, (await (await client.CancelAsync(read)).ReadAsync(HttpStatusCode.OK)).GetProperty("statusId").GetInt32());
    }

    [SqlServerFact]
    public async Task A_stale_form_gets_msg06_with_the_current_version_and_the_first_save_is_kept()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var request = await trader.Client.PlaceAsync(productId);

        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        // The trader's form was read while the request was Awaiting response: an Accepted request can be cancelled, after reading it.
        var conflict = await (await trader.Client.CancelAsync(request)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.NotEqual(request.RowVersion(), conflict.GetProperty("currentRowVersion").GetString());
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
        (await trader.Client.CancelAsync(await trader.Client.MineAsync(request.Id()))).EnsureSuccessStatusCode();

        // SALE-03.0.E1: on a stale form the status comes first.
        await (await owner.DeclineAsync(request)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [SqlServerFact]
    public async Task The_columns_hold_whole_cages_and_the_answer_comes_back_with_a_new_version()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var request = await trader.Client.PlaceAsync(productId, int.MaxValue);
        Assert.Equal(int.MaxValue, request.GetProperty("requestedCages").GetDecimal());

        var small = await trader.Client.PlaceAsync(productId, 2);
        var answer = await (await owner.AcceptAsync(small, 1)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(1, answer.GetProperty("response").GetProperty("acceptedCages").GetDecimal());
        Assert.NotEqual(small.RowVersion(), answer.RowVersion());
        Assert.Equal(StatusIds.MessageDelivery.Sent, answer.GetProperty("sendings")[0].GetProperty("deliveryStatusId").GetInt32());
    }

    [SqlServerFact]
    public async Task The_database_refuses_a_request_outside_the_rules_too()
    {
        await using var host = await FarmingHost.StartAsync();
        var (_, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);

        PurchaseRequest Row(Action<PurchaseRequest> change)
        {
            var row = new PurchaseRequest { CustomerId = trader.Profile.Id, ProductId = productId, RequestedCages = 10, PickupDate = PurchaseRequestTestKit.Today, SentAt = DateTimeOffset.UtcNow };
            change(row);
            return row;
        }

        await host.UseDbAsync(db => db.PurchaseRequests.Add(Row(_ => { })));
        foreach (var bad in new Action<PurchaseRequest>[]
        {
            r => r.RequestedCages = 0,
            r => r.AcceptedCages = 11,
            r => r.AcceptedCages = 0,
            r => r.StatusId = StatusIds.PurchaseRequest.Accepted,
            r => r.StatusId = StatusIds.PurchaseRequest.Declined,
        })
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.PurchaseRequests.Add(Row(bad))));
        }
    }
}
