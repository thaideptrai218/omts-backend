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

/// <summary>QA of OMTS-223 on a real SQL Server: real row versions, column sizes, the status catalog and the migration.</summary>
public class PurchaseRequestQaSqlServerTests
{
    private static async Task<(WebApplicationFactory<Program> Hooked, HttpClient Owner)> HookedOwnerAsync(FarmingHost host, Action<IServiceCollection> services)
    {
        var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services));
        var client = hooked.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", (await LoginTokensAsync(client, host.Owner.Account.Username)).GetProperty("accessToken").GetString());
        return (hooked, client);
    }

    // NFR-09 "when two users edit the same record": a trader's request edits nothing in the profile, and the merge it guards against cannot reach a linked profile.
    [SqlServerFact(Skip = "Waiting for the PM: may a trader's request make an open customer form or merge preview stale? Moving the version stops a double tap from sending two messages.")]
    public async Task M1_a_customer_form_stays_valid_when_the_trader_sends_a_request()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var form = await owner.CustomerAsync(trader.Profile.Id);

        await trader.Client.PlaceAsync(productId);

        var saved = await owner.PutAsJsonAsync($"/api/customers/{trader.Profile.Id}", new { name = "Tên đã sửa", phoneNumber = trader.Profile.PhoneNumber, address = "Vân Đồn", rowVersion = form.RowVersion() });
        Assert.True(saved.StatusCode == HttpStatusCode.OK, $"{(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync()}");
    }

    // SALE-04.3 step 3 shows what will be counted; nothing about the two profiles changed. A request of the kept profile's trader is counted with it anyway.
    [SqlServerFact(Skip = "Waiting for the PM: may a trader's request make an open customer form or merge preview stale? Moving the version stops a double tap from sending two messages.")]
    public async Task M1_a_merge_preview_stays_valid_when_the_trader_of_the_kept_profile_sends_a_request()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var offline = await owner.CreateCustomerAsync();
        var preview = await (await owner.GetAsync($"/api/customers/{trader.Profile.Id}/merge-preview?mergedCustomerId={offline.Id()}")).ReadAsync(HttpStatusCode.OK);

        await trader.Client.PlaceAsync(productId);

        var merged = await owner.PostMergeAsync(trader.Profile.Id, offline.Id(), preview.GetProperty("kept").RowVersion(), preview.GetProperty("merged").RowVersion());
        Assert.True(merged.StatusCode == HttpStatusCode.OK, $"{(int)merged.StatusCode}: {await merged.Content.ReadAsStringAsync()}");
    }

    // A second tap after the first has returned makes a second request, so the moving version does not stop a repeat (the client list says so).
    [SqlServerFact]
    public async Task A_second_tap_after_the_first_answered_is_a_second_request_and_the_profile_version_moved_each_time()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var start = (await owner.CustomerAsync(trader.Profile.Id)).RowVersion();

        await trader.Client.PlaceAsync(productId);
        var afterOne = (await owner.CustomerAsync(trader.Profile.Id)).RowVersion();
        await trader.Client.PlaceAsync(productId);
        var afterTwo = (await owner.CustomerAsync(trader.Profile.Id)).RowVersion();

        Assert.NotEqual(start, afterOne);
        Assert.NotEqual(afterOne, afterTwo);
        Assert.Equal(2, await host.Api.DbAsync(db => db.PurchaseRequests.CountAsync(r => r.CustomerId == trader.Profile.Id)));
    }

    // A merge moved a request still Awaiting, so the Owner's form is stale: MSG06 with the current version, and the answer works after reading.
    [SqlServerFact]
    public async Task A_form_read_before_a_merge_moved_the_request_gets_msg06_and_works_after_reading()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var offline = await owner.CreateCustomerAsync();
        var moved = new PurchaseRequest { CustomerId = offline.Id(), ProductId = productId, RequestedKg = 10, PickupDate = PurchaseRequestTestKit.Today, SentAt = DateTimeOffset.UtcNow };
        await host.UseDbAsync(db => db.PurchaseRequests.Add(moved));
        var form = await owner.ViewedByStaffAsync(moved.Id);

        (await owner.MergeAsync(trader.Profile.Id, offline.Id())).EnsureSuccessStatusCode();

        var conflict = await (await owner.AcceptAsync(form, 5m)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var current = await owner.ViewedByStaffAsync(moved.Id);
        Assert.Equal(current.RowVersion(), conflict.GetProperty("currentRowVersion").GetString());
        Assert.Equal(trader.Profile.Id, current.GetProperty("customer").GetProperty("id").GetGuid());
        (await owner.AcceptAsync(current, 5m)).EnsureSuccessStatusCode();
    }

    // TraderMessageService bounds the error code to its column and not the id the service gives: the save of the status fails and Sent is lost.
    [SqlServerFact]
    public async Task L1_a_long_message_id_from_the_service_still_ends_as_sent()
    {
        await using var host = await FarmingHost.StartAsync();
        var (_, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var sender = new StubTraderMessageSender { Send = _ => Task.FromResult(DeliveryResult.Sent(new string('m', 150))) };
        var (hooked, owner) = await HookedOwnerAsync(host, s => s.AddSingleton<ITraderMessageSender>(sender));
        await using var _ = hooked;
        var request = await trader.Client.PlaceAsync(productId);

        var answer = await (await owner.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Sent", answer.GetProperty("sendings")[0].Text("deliveryStatus"));
    }

    // The longest of everything on the real columns: nothing fails a save, and the parameters survive for a client that builds its own text.
    [SqlServerFact]
    public async Task The_longest_names_notes_and_reasons_in_vietnamese_fit_every_column()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var trader = await PurchaseRequestSqlServerTests.TraderOnAsync(host, new string('ữ', 200));
        var product = await owner.CreateProductAsync(string.Concat(Enumerable.Repeat("ắ", 199)) + "x");
        var note = new string('ộ', 1000);

        var answered = await trader.Client.PlaceAsync(product.Id(), note: note);
        (await owner.AcceptAsync(answered, note: note)).EnsureSuccessStatusCode();
        (await trader.Client.CancelAsync(await trader.Client.MineAsync(answered.Id()), note)).EnsureSuccessStatusCode();
        var declined = await trader.Client.PlaceAsync(product.Id(), note: note);
        (await owner.DeclineAsync(declined, note)).EnsureSuccessStatusCode();

        var rows = await host.Api.NotificationRowsAsync(answered.Id());
        Assert.All(rows, n =>
        {
            Assert.NotNull(n.MessageParams);
            Assert.True(n.Body!.Length <= 2000);
        });
        Assert.Contains(note, rows.InList().OfType(NotificationTypes.PurchaseRequestNew).First().Body);
        var audit = await host.Api.AuditRowsAsync(answered.Id());
        Assert.Equal(note, audit.Single(e => e.Action == "PurchaseRequest.Cancelled").Reason);
    }

    [SqlServerFact]
    public async Task Control_characters_an_emoji_and_the_last_date_are_saved_and_read_back()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var note = "Sáng\u0000 sớm\n😀 <b>\"x\"</b>";

        var request = await trader.Client.PlaceAsync(productId, 999_999_999_999_999.999m, DateOnly.MaxValue, note);

        Assert.Equal(note, (await trader.Client.MineAsync(request.Id())).Text("note"));
        var answer = await (await owner.AcceptAsync(request, pickupDate: DateOnly.MaxValue, note: note)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(DateOnly.MaxValue, answer.GetProperty("response").GetProperty("pickupDate").GetFromJson<DateOnly>());
    }
}
