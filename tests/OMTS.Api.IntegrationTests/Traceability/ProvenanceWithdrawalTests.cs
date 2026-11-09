using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using Catalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// TRACE-04 Withdraw traceability record: the traders found by tracing the lot (BR-TRACE-07), one notice per trader listing their invoices, by SMS and in the app of a trader account
/// (TRACE-04.1), the withdrawal time and reason saved and audited (BR-TRACE-06), and the result of each notice, a failed one listed to call while the lot stays Withdrawn (GFR-06).
/// </summary>
public class ProvenanceWithdrawalTests
{
    private const string Reason = "Seed lot data in doubt";

    /// <summary>The cooperative as MSG-TRACE-05 names it: a short name and a phone number (made up).</summary>
    private static async Task<(string Name, string Phone)> CooperativeAsync(AuthApiFactory api)
    {
        var cooperative = await api.EnsureCooperativeAsync();
        var phone = AcsTestKit.NewPhone();
        await api.UpdateAsync<Cooperative>(cooperative.Id, c => (c.ShortName, c.PhoneNumber) = ("DEMO HTX", phone));
        return ("DEMO HTX", phone);
    }

    // Step 1 then steps 5-7: a trader on two invoices is told once, listing both; a cancelled invoice is no sale and a walk-in has nobody to tell; the sales do not change (POST-3).
    [Fact]
    public async Task Each_trader_of_the_current_invoices_gets_one_notice_listing_their_invoices()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (coopName, coopPhone) = await CooperativeAsync(api);
        var (account, trader) = await Catalogue.LinkedProfileAsync(api, "DEMO trader one");
        var second = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: "DEMO trader two");
        var third = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: "DEMO trader three");
        var first = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 3m, customerId: trader.Id);
        var other = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 2m, customerId: second.Id());
        var again = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 1m, customerId: trader.Id);
        var walkIn = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 1m, walkIn: true);
        var cancelled = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 1m, customerId: third.Id());
        await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId, kg: 1m);
        await api.CancelInvoiceAsync(cancelled.Id());
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        var code = (await api.LotRowAsync(sale.LotId)).LotCode;
        var figures = (await sale.Owner.Client.GetHarvestLotAsync(sale.LotId)).GetProperty("figures").GetRawText();
        Assert.True((await sale.Owner.Client.TraceAsync(sale.LotId)).GetProperty("canWithdraw").GetBoolean());

        var preview = await sale.Owner.Client.WithdrawPreviewAsync(sale.LotId);

        var expectedTraders = new List<string> { $"DEMO trader one: {first.Text("code")}, {again.Text("code")}", $"DEMO trader two: {other.Text("code")}" };
        Assert.Equal(expectedTraders, preview.Traders("traders"));
        Assert.Equal([walkIn.Text("code")], preview.Codes("walkInInvoiceCodes"));
        var listed = preview.GetProperty("traders")[0];
        Assert.Equal((trader.PhoneNumber, account.Id), (listed.Text("phoneNumber"), listed.GetProperty("traderAccountId").GetGuid()));
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("traders")[1].GetProperty("traderAccountId").ValueKind);
        Assert.Equal(("MSG-GEN-04", $"Are you sure you want to withdraw the origin information of harvest lot {code}? This action is recorded in the audit log.", JsonValueKind.Null),
            (preview.Text("messageCode"), preview.Text("message"), preview.GetProperty("notice").ValueKind));
        Assert.Equal(HarvestLotOriginState.Published, (await api.LotRowAsync(sale.LotId)).OriginState);

        var answer = await sale.Owner.Client.WithdrawnAsync(sale.LotId, Reason);

        Assert.Equal(("Withdrawn", "MSG-GEN-05", JsonValueKind.Null), (answer.GetProperty("lot").Text("originState"), answer.Text("messageCode"), answer.GetProperty("notice").ValueKind));
        Assert.Equal(expectedTraders, answer.Traders("notices"));
        Assert.Equal([walkIn.Text("code")], answer.Codes("walkInInvoiceCodes"));
        Assert.All(answer.GetProperty("notices").EnumerateArray(), n => Assert.Equal(("Sms", "Sent", false), (n.Text("channel"), n.Text("deliveryStatus"), n.GetProperty("toCall").GetBoolean())));
        Assert.Equal((true, false), (answer.NoticeOf(trader.Id).GetProperty("notifiedInApp").GetBoolean(), answer.NoticeOf(second.Id()).GetProperty("notifiedInApp").GetBoolean()));

        // Step 5: the time and the reason on the lot, one audit row with the traders told (BR-TRACE-06, GBR-02).
        var row = await api.LotRowAsync(sale.LotId);
        Assert.Equal((HarvestLotOriginState.Withdrawn, Reason), (row.OriginState, row.WithdrawalReason));
        Assert.True(row.WithdrawnAt > row.PublishedAt);
        var audit = Assert.Single(await api.WithdrawAuditAsync(sale.LotId));
        Assert.Equal((code, (Guid?)sale.ZoneId, (Guid?)sale.Owner.Account.Id, Reason), (audit.RecordCode, audit.ZoneId, audit.ActorAccountId, audit.Reason));
        Assert.Contains("\"OriginState\":\"Published\"", audit.Before);
        Assert.Contains("\"OriginState\":\"Withdrawn\"", audit.After);
        Assert.Contains("DEMO trader two", audit.After);
        Assert.DoesNotContain("DEMO trader three", audit.After);

        // Step 6 (BR-TRACE-07): one SMS per trader, never the reason; the trader account also has it in its list, pushed, opening nothing.
        var text = $"{coopName}: the origin information of oyster lot {code} (invoice {first.Text("code")}, {again.Text("code")}) has been withdrawn. You do not need to return the oysters. Please call {coopPhone}.";
        Assert.Equal([text], api.SmsTo(trader.PhoneNumber!).Select(m => m.Text));
        Assert.Single(api.SmsTo(second.Text("phoneNumber")));
        Assert.DoesNotContain(Reason, text);
        var sendings = await api.NoticeSendingsAsync(sale.LotId);
        Assert.Equal(2, sendings.Count);
        Assert.All(sendings, s => Assert.Equal((StatusIds.MessageDelivery.Sent, code, "MSG-TRACE-05"), (s.DeliveryStatusId, s.RelatedRecordCode, s.MessageCode)));
        Assert.Equal(new Guid?[] { account.Id, null }, sendings.OrderBy(s => s.RecipientAccountId is null).Select(s => s.RecipientAccountId));
        var inList = Assert.Single(await api.NoticesInListAsync(account.Id));
        Assert.Equal(((Guid?)null, text), (inList.RelatedRecordId, inList.Body));
        Assert.Contains(api.Services.GetRequiredService<FakePushSender>().Inbox.List(account.Id.ToString()), p => p.NotificationId == inList.Id);

        // POST-3: the oysters are not taken back; the trace offers Withdraw no more.
        Assert.Equal(figures, (await sale.Owner.Client.GetHarvestLotAsync(sale.LotId)).GetProperty("figures").GetRawText());
        Assert.False((await sale.Owner.Client.TraceAsync(sale.LotId)).GetProperty("canWithdraw").GetBoolean());
    }

    // GFR-06 and step 7: a profile without a phone and a refused SMS are Not sent and listed to call; the withdrawal is saved all the same. A trader account still gets it in its list.
    [Fact]
    public async Task A_notice_that_is_not_sent_is_listed_to_call_and_the_lot_stays_withdrawn()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await CooperativeAsync(api);
        var (account, noPhone) = await Catalogue.LinkedProfileAsync(api, "DEMO trader without phone", c => c.PhoneNumber = null);
        var refused = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: "DEMO trader refused");
        api.RefuseSmsTo(refused.Text("phoneNumber"));
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: noPhone.Id);
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: refused.Id());
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);

        var answer = await sale.Owner.Client.WithdrawnAsync(sale.LotId, Reason);

        var unreachable = answer.NoticeOf(noPhone.Id);
        Assert.Equal((JsonValueKind.Null, "Not sent", true, true), (unreachable.GetProperty("phoneNumber").ValueKind, unreachable.Text("deliveryStatus"),
            unreachable.GetProperty("toCall").GetBoolean(), unreachable.GetProperty("notifiedInApp").GetBoolean()));
        var notAccepted = answer.NoticeOf(refused.Id());
        Assert.Equal(("Not sent", StatusIds.MessageDelivery.NotSent, true), (notAccepted.Text("deliveryStatus"), notAccepted.GetProperty("deliveryStatusId").GetInt32(),
            notAccepted.GetProperty("toCall").GetBoolean()));
        Assert.Equal(HarvestLotOriginState.Withdrawn, (await api.LotRowAsync(sale.LotId)).OriginState);
        Assert.Single(await api.WithdrawAuditAsync(sale.LotId));
        var sendings = await api.NoticeSendingsAsync(sale.LotId);
        Assert.Equal(new (string?, string?)[] { (null, "NO_PHONE_NUMBER"), (refused.Text("phoneNumber"), "FAKE_CONFIGURED_FAILURE") },
            sendings.OrderBy(s => s.PhoneNumber is not null).Select(s => (s.PhoneNumber, s.ProviderErrorCode)));
        Assert.All(sendings, s => Assert.Equal(StatusIds.MessageDelivery.NotSent, s.DeliveryStatusId));
        Assert.Single(await api.NoticesInListAsync(account.Id));
        Assert.Empty(api.SmsTo(refused.Text("phoneNumber")));
    }

    // TRACE-04.2: a Published lot whose only invoice was cancelled is sold by no current invoice: MSG-TRACE-06, and the withdrawal tells nobody.
    [Fact]
    public async Task A_lot_no_current_invoice_sells_is_withdrawn_without_notices_and_msg_trace_06()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        await api.CancelInvoiceAsync(invoice.Id());
        var code = (await api.LotRowAsync(sale.LotId)).LotCode;
        var msg06 = $"No current sales invoice sells harvest lot {code}; no trader will be notified.";

        var preview = await sale.Owner.Client.WithdrawPreviewAsync(sale.LotId);
        var answer = await sale.Owner.Client.WithdrawnAsync(sale.LotId);

        Assert.Equal((0, 0, "MSG-TRACE-06", msg06), (preview.GetProperty("traders").GetArrayLength(), preview.GetProperty("walkInInvoiceCodes").GetArrayLength(),
            preview.Text("noticeCode"), preview.Text("notice")));
        Assert.Equal((0, "MSG-TRACE-06", msg06, "Withdrawn"), (answer.GetProperty("notices").GetArrayLength(), answer.Text("noticeCode"), answer.Text("notice"),
            answer.GetProperty("lot").Text("originState")));
        Assert.Empty(await api.NoticeSendingsAsync(sale.LotId));
        Assert.Empty(api.SmsTo((await api.QueryAsync(db => db.Set<Customer>().SingleAsync(c => c.Id == sale.CustomerId))).PhoneNumber!));
    }
}
