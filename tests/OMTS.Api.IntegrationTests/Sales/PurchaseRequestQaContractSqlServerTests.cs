using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223 on a real SQL Server: the hook for SALE-05, the lists, the audit rows, and what a stale form shows.</summary>
public class PurchaseRequestQaContractSqlServerTests
{
    private static async Task<(WebApplicationFactory<Program> Hooked, HttpClient Owner)> HookedOwnerAsync(FarmingHost host, Action<IServiceCollection> services)
    {
        var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services));
        var client = hooked.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", (await LoginTokensAsync(client, host.Owner.Account.Username)).GetProperty("accessToken").GetString());
        return (hooked, client);
    }

    // SALE-01 step 4 checks the product before the save; the code says that a discontinue in the very moment after is not caught. Pinned: the state stays whole.
    [SqlServerFact]
    public async Task A_discontinue_between_the_check_and_the_save_leaves_a_request_naming_the_product_and_the_owner_can_decline_it()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var query = new DiscontinuesFirst(() => owner.PostAsync($"/api/products/{productId}/discontinue", null));
        var (hooked, _) = await HookedOwnerAsync(host, s => s.AddScoped<ICooperativeOwnerQuery>(_ => query));
        await using var _1 = hooked;
        var client = hooked.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (await LoginTokensAsync(client, trader.Account.Username)).GetProperty("accessToken").GetString());

        var request = await client.PlaceAsync(productId);

        Assert.Equal(StatusIds.Usage.Discontinued, await host.Api.DbAsync(db => db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.StatusId).SingleAsync()));
        (await owner.DeclineAsync(request)).EnsureSuccessStatusCode();
    }

    // The contract of IPurchaseRequestFulfilment for SALE-05: an invoice saved at the moment of a cancel marks the request changed. The cancel then loses and the trader reads Fulfilled.
    [SqlServerFact]
    public async Task The_fulfilment_contract_can_be_tested_a_cancel_that_meets_an_invoice_saved_at_that_moment_loses()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var hook = new StubFulfilment();
        var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<OMTS.Application.Features.Sales.PurchaseRequests.IPurchaseRequestFulfilment>(hook)));
        await using var _ = hooked;
        var client = hooked.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (await LoginTokensAsync(client, trader.Account.Username)).GetProperty("accessToken").GetString());
        var request = await client.PlaceAsync(productId);
        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
        var form = await client.MineAsync(request.Id());

        hook.DuringNextCheck = async () =>
        {
            // As SALE-05 is told to: the invoice's save calls MarkChanged on the request.
            await host.UseDbAsync(db =>
            {
                var row = db.PurchaseRequests.Single(r => r.Id == request.Id());
                db.Entry(row).State = EntityState.Modified;
            });
        };

        await (await client.CancelAsync(form)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        hook.Fulfilled.Add(request.Id());
        var read = await client.MineAsync(request.Id());
        Assert.Equal(("Fulfilled", false), (read.Text("status"), read.GetProperty("canCancel").GetBoolean()));
        await (await client.CancelAsync(read)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Empty((await host.Api.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestCancelled));
    }

    // The translated queries on the real server: SCR-SALE-02 order and paging, the filter of SALE-04.1, the sendings of a whole page, and SCR-SALE-01.
    [SqlServerFact]
    public async Task Both_lists_page_and_sort_on_the_real_server()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var other = await PurchaseRequestSqlServerTests.TraderOnAsync(host, "Anh Khác");
        var answered = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var request = await trader.Client.PlaceAsync(productId, 10 + i);
            if (i % 2 == 0)
            {
                (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
                answered.Add(request.Id());
            }
        }

        await other.Client.PlaceAsync(productId);

        var first = await (await owner.GetAsync("/api/purchase-requests?page=1&pageSize=4")).ReadAsync(HttpStatusCode.OK);
        var second = await (await owner.GetAsync("/api/purchase-requests?page=2&pageSize=4")).ReadAsync(HttpStatusCode.OK);
        var all = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal((7, 7), (first.GetProperty("total").GetInt32(), all.Select(r => r.Id()).Distinct().Count()));
        Assert.Equal(Enumerable.Repeat(3201, 4).Concat(Enumerable.Repeat(3202, 3)), all.Select(r => r.GetProperty("statusId").GetInt32()));
        Assert.All(all.Where(r => answered.Contains(r.Id())), r => Assert.Equal("Sent", r.GetProperty("sendings")[0].Text("deliveryStatus")));

        var filtered = await (await owner.GetAsync($"/api/purchase-requests?customerId={other.Profile.Id}&statusId=3201")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());

        var mine = await (await trader.Client.GetAsync("/api/me/purchases?page=1&pageSize=4")).ReadAsync(HttpStatusCode.OK);
        var times = mine.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("sentAt").GetDateTimeOffset()).ToList();
        Assert.Equal((6, 4), (mine.GetProperty("total").GetInt32(), times.Count));
        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    // The client list, §7: what each act writes to the audit log, on the real columns.
    [SqlServerFact]
    public async Task Each_act_writes_its_audit_row_with_its_actor_and_values()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var accepted = await trader.Client.PlaceAsync(productId, note: "Sáng");
        (await owner.AcceptAsync(accepted, 50, payLater: true, note: "Đủ 50")).EnsureSuccessStatusCode();
        (await trader.Client.CancelAsync(await trader.Client.MineAsync(accepted.Id()), "Đổi lịch")).EnsureSuccessStatusCode();
        var declined = await trader.Client.PlaceAsync(productId);
        (await owner.DeclineAsync(declined, "Chưa đủ")).EnsureSuccessStatusCode();

        var rows = await host.Api.AuditRowsAsync(accepted.Id());
        var sent = rows.Single(e => e.Action == "PurchaseRequest.Sent");
        Assert.Equal(trader.Account.Id, sent.ActorAccountId);
        Assert.Contains("AWAITING_RESPONSE", sent.After);
        var accept = rows.Single(e => e.Action == "PurchaseRequest.Accepted");
        Assert.Equal(host.Owner.Account.Id, accept.ActorAccountId);
        Assert.Contains("AWAITING_RESPONSE", accept.Before);
        var values = JsonDocument.Parse(accept.After!).RootElement;
        Assert.Equal(("ACCEPTED", 50, true), (values.GetProperty("Status").GetString(), values.GetProperty("AcceptedCages").GetDecimal(), values.GetProperty("PayLaterAgreed").GetBoolean()));
        var cancel = rows.Single(e => e.Action == "PurchaseRequest.Cancelled");
        Assert.Equal((trader.Account.Id, "Đổi lịch"), (cancel.ActorAccountId, cancel.Reason));
        Assert.Contains("ACCEPTED", cancel.Before);
        var decline = (await host.Api.AuditRowsAsync(declined.Id())).Single(e => e.Action == "PurchaseRequest.Declined");
        Assert.Equal(host.Owner.Account.Id, decline.ActorAccountId);
        Assert.Contains("Chưa đủ", JsonDocument.Parse(decline.After!).RootElement.GetProperty("Note").GetString());
    }

    // MSG06 names who saved last. To a trader on a stale form, that is the Owner's display name: nothing of another trader.
    [SqlServerFact]
    public async Task A_stale_cancel_names_the_owner_and_no_phone()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var request = await trader.Client.PlaceAsync(productId);
        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        var conflict = await (await trader.Client.CancelAsync(request)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        Assert.Contains(host.Owner.Account.DisplayName, conflict.GetProperty("detail").GetString());
        Assert.DoesNotContain(host.Owner.Account.PhoneNumber!, conflict.ToString());
    }

    private sealed class DiscontinuesFirst(Func<Task<HttpResponseMessage>> discontinue) : ICooperativeOwnerQuery
    {
        public async Task<IReadOnlyList<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken)
        {
            (await discontinue()).EnsureSuccessStatusCode();
            return [];
        }
    }
}
