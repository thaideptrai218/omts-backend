using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using PurchaseRequestStubsFactory = OMTS.Api.IntegrationTests.Sales.PurchaseRequestStubsFactory;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using Catalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA of TRACE-04 (OMTS-222): the traders of merged profiles (BR-TRACE-07, BR-SALE-04), who gets the in-app notice (TRACE-04.1, BR-ACCOUNT-11/12) and who does not, and a slow SMS
/// provider (step 7: the result of each notice is only in the answer).
/// </summary>
public class ProvenanceWithdrawalQaTests
{
    private static async Task SetCooperativeAsync(AuthApiFactory api)
    {
        var cooperative = await api.EnsureCooperativeAsync();
        await api.UpdateAsync<Cooperative>(cooperative.Id, c => (c.ShortName, c.PhoneNumber) = ("DEMO HTX", AcsTestKit.NewPhone()));
    }

    private static Task<int> NoticesOfAsync(AuthApiFactory api, params Guid[] accountIds) => api.QueryAsync(db => db.Notifications.AsNoTracking()
        .CountAsync(n => n.Type == NotificationTypes.ProvenanceWithdrawn && n.RecipientAccountId != null && accountIds.Contains(n.RecipientAccountId.Value)));

    // BR-TRACE-07 with a merge (SALE-04): the invoices of the merged profile now belong to the kept one, so the trader is told once, listing both invoices, at the kept phone.
    [Fact]
    public async Task A_merged_profile_is_told_once_listing_the_invoices_of_both_profiles()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await SetCooperativeAsync(api);
        var kept = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: "DEMO QA kept");
        var merged = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: "DEMO QA merged");
        var first = await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: kept.Id());
        var second = await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: merged.Id());
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        await (await Catalogue.MergeAsync(sale.Owner.Client, kept.Id(), merged.Id())).ReadAsync(HttpStatusCode.OK);

        var preview = await sale.Owner.Client.WithdrawPreviewAsync(sale.LotId);
        var answer = await sale.Owner.Client.WithdrawnAsync(sale.LotId);

        var expected = new List<string> { $"DEMO QA kept: {Catalogue.Text(first, "code")}, {Catalogue.Text(second, "code")}" };
        Assert.Equal(expected, preview.Traders("traders"));
        Assert.Equal(expected, answer.Traders("notices"));
        Assert.Single(api.SmsTo(Catalogue.Text(kept, "phoneNumber")!));
        Assert.Empty(api.SmsTo(Catalogue.Text(merged, "phoneNumber")!));
    }

    // TRACE-04.1 only for an active trader account (a Locked one still gets the SMS, BR-TRACE-07); nothing reaches the Owner, the Zone Manager or a trader who did not buy the lot.
    [Fact]
    public async Task A_locked_trader_gets_the_sms_only_and_no_staff_or_other_trader_gets_the_notice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await SetCooperativeAsync(api);
        var (locked, profile) = await Catalogue.LinkedProfileAsync(api, "DEMO QA locked trader");
        var bystander = await api.TraderAsync("DEMO QA bystander");
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: profile.Id);
        await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId, customerId: bystander.Profile.Id);
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        await api.UpdateAsync<Account>(locked.Id, a => a.StatusId = StatusIds.Account.Locked);

        var answer = await sale.Owner.Client.WithdrawnAsync(sale.LotId);

        var notice = answer.NoticeOf(profile.Id);
        Assert.Equal(("Sent", false, false), (Catalogue.Text(notice, "deliveryStatus"), notice.GetProperty("notifiedInApp").GetBoolean(), notice.GetProperty("toCall").GetBoolean()));
        Assert.Single(api.SmsTo(profile.PhoneNumber!));
        Assert.Empty(await api.NoticesInListAsync(locked.Id));
        Assert.Empty(api.Services.GetRequiredService<FakePushSender>().Inbox.List(locked.Id.ToString()));
        Assert.Equal(0, await NoticesOfAsync(api, sale.Owner.Account.Id, sale.Manager.Account.Id, bystander.Account.Id));
        Assert.Empty(api.SmsTo(bystander.Profile.PhoneNumber!));
    }

    // BR-ACCOUNT-11: PROVENANCE_WITHDRAWN is not a mandatory type, so the trader may turn its push off; the notice is still kept, unread, in the list (BR-ACCOUNT-12, GFR-06).
    [Fact]
    public async Task A_trader_who_turned_the_push_off_still_finds_the_notice_unread_in_the_list()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await SetCooperativeAsync(api);
        var trader = await api.TraderAsync("DEMO QA quiet trader");
        var off = await trader.Client.PutAsJsonAsync("/api/me/notification-preferences", new { items = new[] { new { type = NotificationTypes.ProvenanceWithdrawn, push = false } } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: trader.Profile.Id);
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);

        var answer = await sale.Owner.Client.WithdrawnAsync(sale.LotId);

        Assert.True(answer.NoticeOf(trader.Profile.Id).GetProperty("notifiedInApp").GetBoolean());
        var list = await trader.Client.GetFromJsonAsync<JsonElement>("/api/me/notifications");
        Assert.Equal(1, list.GetProperty("unreadCount").GetInt32());
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal((NotificationTypes.ProvenanceWithdrawn, "MSG-TRACE-05"), (Catalogue.Text(row, "type"), Catalogue.Text(row, "messageCode")));
        Assert.Empty(api.Services.GetRequiredService<FakePushSender>().Inbox.List(trader.Account.Id.ToString()));
    }

    // Steps 5-7 with a slow SMS Service (each send may take up to 10 s, TraderMessageService.SendTimeout): the withdrawal is saved before any send (GFR-06), but the notices are sent
    // one after the other inside the request, so the answer, the only place that lists the traders to call (deviation 3), comes after the sum of the sends. Expected: bounded by
    // one send, not one per trader (NFR-10: an action waiting for an external service shows a processing status, it does not hold the answer per recipient).
    [Fact]
    public async Task The_answer_of_a_withdrawal_waits_for_one_slow_send_not_one_per_trader()
    {
        const int Traders = 4;
        var perSend = TimeSpan.FromSeconds(4);
        using var api = new PurchaseRequestStubsFactory();
        var sale = await api.SaleSetupAsync();
        await SetCooperativeAsync(api);
        for (var i = 0; i < Traders; i++)
        {
            var customer = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: $"DEMO QA slow {i}");
            await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: customer.Id());
        }

        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        var savedBeforeSend = new ConcurrentBag<bool>();
        api.Sender.Send = async cancellationToken =>
        {
            savedBeforeSend.Add((await api.LotRowAsync(sale.LotId)).OriginState == HarvestLotOriginState.Withdrawn);
            await Task.Delay(perSend, cancellationToken);
            return DeliveryResult.Sent("slow");
        };

        var watch = Stopwatch.StartNew();
        var answer = await sale.Owner.Client.WithdrawnAsync(sale.LotId);
        watch.Stop();

        Assert.Equal(Traders, answer.GetProperty("notices").GetArrayLength());
        Assert.Equal(Enumerable.Repeat(true, Traders), savedBeforeSend);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"The answer took {watch.Elapsed.TotalSeconds:F1} s for {Traders} traders at {perSend.TotalSeconds} s per send: the sends are made one after the other.");
    }
}
