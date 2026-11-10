using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of CLAIM-02 and the messages MSG-CLAIM-01, 02 and 03 (BR-CLAIM-01, BR-CLAIM-02, GFR-06).</summary>
public class QualityClaimQaRespondTests
{
    private const string CooperativePhone = "999900000001";

    private static Task PhoneOfCooperativeAsync(AuthApiFactory api, string? phone) =>
        api.UseDbAsync(db => db.Cooperatives.ToList().ForEach(c => c.PhoneNumber = phone));

    private static Task<string> OfferSmsAsync(AuthApiFactory api, QaClaim c) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.RelatedRecordId == c.Id && n.Channel != NotificationChannel.InApp).Select(n => n.Body!).SingleAsync());

    private static Task<Notification> ListRowAsync(AuthApiFactory api, QaClaim c, string type) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.RelatedRecordId == c.Id && n.Type == type && n.Channel == NotificationChannel.InApp));

    private static string CodeOf(QaClaim c) => c.Claim.GetProperty("invoice").Text("code")!;

    [Theory]
    [InlineData("ReturnAllGoods", "accepted: the cooperative offers a return of all the goods")]
    [InlineData("PriceCutWholeInvoice", "accepted: the cooperative offers a price cut of the whole invoice")]
    public async Task Each_offer_reads_as_msg_claim_02_with_the_parameters_on_push_and_sms(string offer, string result)
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await PhoneOfCooperativeAsync(api, CooperativePhone);

        var answer = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion, offer, "Sorry")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(StatusIds.QualityClaim.Accepted, answer.GetProperty("statusId").GetInt32());
        var name = await api.QueryAsync(db => db.Cooperatives.Select(x => x.ShortName ?? x.FullName).SingleAsync());
        var expected = $"{name}: your quality claim on invoice {CodeOf(c)} is {result}. Sorry Please call {CooperativePhone}.";
        var row = await ListRowAsync(api, c, NotificationTypes.QualityClaimResponse);
        Assert.Equal(("MSG-CLAIM-02", expected), (row.MessageCode, row.Body));
        Assert.Equal(expected, await OfferSmsAsync(api, c));
        var push = Assert.Single(api.Services.GetRequiredService<FakePushSender>().Inbox.List(c.World.Trader.Account.Id.ToString()), m => m.NotificationId == row.Id);
        Assert.Contains(expected, push.Text);
        using var parameters = JsonDocument.Parse(row.MessageParams!);
        Assert.Equal(("Accepted", offer, "Sorry", CooperativePhone), (parameters.RootElement.Text("result"), parameters.RootElement.Text("offer"), parameters.RootElement.Text("note"), parameters.RootElement.Text("cooperative_phone")));
    }

    [Fact]
    public async Task A_rejection_says_not_accepted_with_the_reason_and_no_cooperative_phone_reads_the_cooperative()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await PhoneOfCooperativeAsync(api, null);

        await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion, "Too late")).ReadAsync(HttpStatusCode.OK);

        var name = await api.QueryAsync(db => db.Cooperatives.Select(x => x.ShortName ?? x.FullName).SingleAsync());
        Assert.Equal($"{name}: your quality claim on invoice {CodeOf(c)} is not accepted. Too late Please call the cooperative.", await OfferSmsAsync(api, c));
    }

    [Fact]
    public async Task An_accept_without_a_note_reads_cleanly_and_a_blank_note_counts_as_none()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await PhoneOfCooperativeAsync(api, CooperativePhone);

        var answer = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion, note: "   ")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, answer.GetProperty("response").GetProperty("note").ValueKind);
        Assert.EndsWith($"of all the goods. Please call {CooperativePhone}.", await OfferSmsAsync(api, c));
    }

    [Fact]
    public async Task The_sms_cuts_a_long_note_while_the_list_keeps_it_whole_and_msg_claim_01_starts_the_description()
    {
        using var api = new QualityClaimApiFactory();
        var longDescription = new string('d', 120);
        var world = await api.ClaimWorldAsync();
        var claim = await (await world.Trader.Client.PostClaimAsync(world.InvoiceId, longDescription)).ReadAsync(HttpStatusCode.Created);
        var rowVersion = (await world.Sale.Owner.Client.ClaimAsStaffAsync(claim.GetProperty("claim").Id())).Text("rowVersion")!;
        var c = new QaClaim(world, claim.GetProperty("claim"), rowVersion);
        var note = new string('n', 90);

        var owner = await ListRowAsync(api, c, NotificationTypes.QualityClaimNew);
        Assert.Equal($"{world.Trader.Profile.Name} reported a quality problem with invoice {claim.GetProperty("claim").GetProperty("invoice").Text("code")}: {new string('d', 100)}.... Please ask the Zone Manager to check it, then respond.", owner.Body);
        Assert.Equal("MSG-CLAIM-01", owner.MessageCode);
        Assert.False(owner.IsMandatory);
        using (var parameters = JsonDocument.Parse(owner.MessageParams!))
        {
            Assert.Equal((world.Trader.Profile.Name, new string('d', 100) + "..."), (parameters.RootElement.Text("customer_name"), parameters.RootElement.Text("description_start")));
        }

        Assert.Contains(api.Services.GetRequiredService<FakePushSender>().Inbox.List(world.Sale.Owner.Account.Id.ToString()), m => m.NotificationId == owner.Id);
        Assert.Empty(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(world.Sale.Owner.Account.PhoneNumber!));

        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion, note: note)).ReadAsync(HttpStatusCode.OK);

        var sms = await OfferSmsAsync(api, c);
        Assert.Contains(new string('n', 80) + "...", sms);
        Assert.DoesNotContain(new string('n', 81), sms);
        Assert.Contains(note, (await ListRowAsync(api, c, NotificationTypes.QualityClaimResponse)).Body);
        Assert.Equal(note, (await c.Owner.ClaimAsStaffAsync(c.Id)).GetProperty("response").Text("note"));
    }

    [Fact]
    public async Task A_refused_push_and_a_refused_sms_undo_nothing_and_the_owner_is_still_told_a_new_claim_when_push_fails()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        api.RefusePushTo(world.Sale.Owner.Account.Id);

        var claim = await world.Trader.Client.SubmitClaimAsync(world.InvoiceId);

        Assert.Equal(1, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.QualityClaimNew && n.RelatedRecordId == claim.Id() && n.Channel == NotificationChannel.InApp)));
    }

    [Fact]
    public async Task A_trader_who_turned_the_push_off_keeps_the_list_row_and_still_gets_the_sms()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var trader = c.World.Trader;

        Assert.Equal(HttpStatusCode.OK, (await trader.Client.SavePushAsync((NotificationTypes.QualityClaimResponse, false))).StatusCode);
        var types = await trader.Client.OfferedTypesAsync();
        Assert.Equal((false, false), types[NotificationTypes.QualityClaimResponse]);

        await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);

        var row = await ListRowAsync(api, c, NotificationTypes.QualityClaimResponse);
        Assert.DoesNotContain(api.Services.GetRequiredService<FakePushSender>().Inbox.List(trader.Account.Id.ToString()), m => m.NotificationId == row.Id);
        Assert.Single(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(trader.Profile.PhoneNumber!));
    }

    [Fact]
    public async Task The_new_types_are_not_mandatory_and_the_owner_type_can_be_turned_off()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var traderTypes = await c.World.Trader.Client.OfferedTypesAsync();
        Assert.Equal((false, true), traderTypes[NotificationTypes.QualityClaimResponse]);
        Assert.DoesNotContain(NotificationTypes.QualityClaimNew, traderTypes.Keys);

        var ownerTypes = await c.Owner.OfferedTypesAsync();
        Assert.Equal((false, true), ownerTypes[NotificationTypes.QualityClaimNew]);
        Assert.DoesNotContain(NotificationTypes.QualityClaimResponse, ownerTypes.Keys);
    }

    [Fact]
    public async Task A_profile_with_no_account_gets_the_sms_only_and_the_save_stands()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await api.UseDbAsync(db => db.Customers.Where(p => p.Id == c.World.Trader.Profile.Id).ToList().ForEach(p => p.AccountId = null));

        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(0, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == c.Id && n.Type == NotificationTypes.QualityClaimResponse && n.Channel == NotificationChannel.InApp)));
        var sending = await api.QueryAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.RelatedRecordId == c.Id && n.Channel != NotificationChannel.InApp));
        Assert.Equal((StatusIds.MessageDelivery.Sent, (Guid?)null), (sending.DeliveryStatusId, sending.RecipientAccountId));
    }

    [Fact]
    public async Task Msg_claim_03_names_the_invoice_and_the_cause_exactly()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await api.UseDbAsync(db => db.SalesInvoices.Where(i => i.Id == c.World.InvoiceId).ToList().ForEach(i => i.StatusId = StatusIds.SalesInvoice.Cancelled));

        var answer = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ProblemAsync(HttpStatusCode.Conflict, "MSG-CLAIM-03");

        Assert.Equal($"Invoice {CodeOf(c)} was cancelled, so the claim cannot be accepted. You can still reject it with a note.", answer.Text("detail"));
    }

    [Theory]
    [InlineData("""{"offer":"Refund","rowVersion":"AAAA"}""")]
    [InlineData("""{"offer":99,"rowVersion":"ROWVERSION"}""")]
    [InlineData("""{"offer":true,"rowVersion":"AAAA"}""")]
    [InlineData("""{"offer":[1],"rowVersion":"AAAA"}""")]
    [InlineData("""{"offer":"ReturnAllGoods","note":123,"rowVersion":"ROWVERSION"}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task A_broken_accept_body_answers_400_never_500(string body)
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        var answer = await c.Owner.PostAsJsonRawAsync($"/api/quality-claims/{c.Id}/accept", body.Replace("ROWVERSION", c.RowVersion, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(StatusIds.QualityClaim.AwaitingResponse, await api.QueryAsync(db => db.QualityClaims.Where(q => q.Id == c.Id).Select(q => q.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task The_offer_named_in_another_case_or_as_its_number_is_the_same_offer_and_a_null_offer_is_missing()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        var none = await c.Owner.PostAsJsonRawAsync($"/api/quality-claims/{c.Id}/accept", $$$"""{"offer":null,"rowVersion":"{{{c.RowVersion}}}"}""");
        Assert.Equal(["Offer"], await none.InvalidFieldsAsync());

        var lowercase = await (await c.Owner.PostAsJsonRawAsync($"/api/quality-claims/{c.Id}/accept", $$$"""{"offer":"pricecutwholeinvoice","rowVersion":"{{{c.RowVersion}}}"}""")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(QualityClaimOffer.PriceCutWholeInvoice, lowercase.ReadOffer());
    }

    [Theory]
    [InlineData(null, "RowVersion")]
    [InlineData("not base 64!", "RowVersion")]
    public async Task A_missing_or_broken_row_version_is_refused_as_invalid_on_both_responses(string? rowVersion, string field)
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        Assert.Equal([field], await (await c.Owner.AcceptClaimAsync(c.Id, rowVersion)).InvalidFieldsAsync());
        Assert.Equal([field], await (await c.Owner.RejectClaimAsync(c.Id, rowVersion)).InvalidFieldsAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("")]
    public async Task A_rejection_needs_a_reason(string? note)
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        Assert.Equal(["Note"], await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion, note)).InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_note_of_1000_characters_fits_and_1001_is_refused_on_both_responses()
    {
        using var api = new QualityClaimApiFactory();
        var a = await api.QaClaimedAsync();
        var b = await api.QaClaimedAsync();

        Assert.Equal(["Note"], await (await a.Owner.AcceptClaimAsync(a.Id, a.RowVersion, note: new string('n', 1001))).InvalidFieldsAsync());
        Assert.Equal(["Note"], await (await a.Owner.RejectClaimAsync(a.Id, a.RowVersion, new string('n', 1001))).InvalidFieldsAsync());
        await (await a.Owner.AcceptClaimAsync(a.Id, a.RowVersion, note: new string('n', 1000))).ReadAsync(HttpStatusCode.OK);
        await (await b.Owner.RejectClaimAsync(b.Id, b.RowVersion, new string('n', 1000))).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Answering_twice_is_refused_sends_nothing_more_and_a_new_claim_is_open_after_acceptance_too()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var sms = api.Services.GetRequiredService<FakeSmsSender>().Inbox;

        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);
        var count = sms.List(c.World.Trader.Profile.PhoneNumber!).Count;
        await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Equal(count, sms.List(c.World.Trader.Profile.PhoneNumber!).Count);
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == c.Id && e.Action == "QualityClaim.Accepted")));

        var second = await c.World.Trader.Client.SubmitClaimAsync(c.World.InvoiceId);
        Assert.NotEqual(c.Claim.Text("code"), second.Text("code"));
        Assert.Equal(StatusIds.QualityClaim.AwaitingResponse, second.GetProperty("statusId").GetInt32());
    }

    [Fact]
    public async Task A_rejection_changes_neither_invoice_nor_debt_and_the_audit_carries_no_phone_number()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var profile = c.World.Trader.Profile;
        var debtBefore = await c.Owner.CustomerDebtAsync(profile.Id);
        var totalBefore = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().Where(i => i.Id == c.World.InvoiceId).Select(i => new { i.Total, i.StatusId }).SingleAsync());

        await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(debtBefore, await c.Owner.CustomerDebtAsync(profile.Id));
        Assert.Equal(totalBefore, await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().Where(i => i.Id == c.World.InvoiceId).Select(i => new { i.Total, i.StatusId }).SingleAsync()));
        var audit = await api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == c.Id).Select(e => e.After).ToListAsync());
        Assert.All(audit, after => Assert.DoesNotContain(profile.PhoneNumber!, after ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logs_hold_no_phone_number_no_description_and_no_note_even_when_the_sending_fails()
    {
        using var api = new QualityClaimApiFactory();
        const string secretDescription = "DEMO-SECRET-DESCRIPTION";
        const string secretNote = "DEMO-SECRET-NOTE";
        var world = await api.ClaimWorldAsync();
        api.RefuseSmsTo(world.Trader.Profile.PhoneNumber!);
        api.RefusePushTo(world.Trader.Account.Id);

        var claim = (await (await world.Trader.Client.PostClaimAsync(world.InvoiceId, secretDescription)).ReadAsync(HttpStatusCode.Created)).GetProperty("claim");
        var rowVersion = (await world.Sale.Owner.Client.ClaimAsStaffAsync(claim.Id())).Text("rowVersion");
        await (await world.Sale.Owner.Client.RejectClaimAsync(claim.Id(), rowVersion, secretNote)).ReadAsync(HttpStatusCode.OK);

        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(secretDescription, StringComparison.Ordinal) || l.Contains(secretNote, StringComparison.Ordinal) || l.Contains(world.Trader.Profile.PhoneNumber!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_response_row_opens_only_for_its_recipient()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);
        var row = await ListRowAsync(api, c, NotificationTypes.QualityClaimResponse);

        Assert.Equal(HttpStatusCode.NotFound, (await c.Owner.GetAsync($"/api/me/notifications/{row.Id}/open")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.World.Sale.Manager.Client.GetAsync($"/api/me/notifications/{row.Id}/open")).StatusCode);
    }
}
