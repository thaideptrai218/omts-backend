using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>CLAIM-02: the Owner's response and BR-CLAIM-02; the lists and details of trader and staff, and the access to them.</summary>
public class QualityClaimRespondTests
{
    private sealed record Claimed(ClaimWorld World, JsonElement Claim, string RowVersion)
    {
        public HttpClient Owner => World.Sale.Owner.Client;

        public Guid Id => Claim.Id();
    }

    private static async Task<Claimed> ClaimedAsync(AuthApiFactory api)
    {
        var world = await api.ClaimWorldAsync();
        var claim = await world.Trader.Client.SubmitClaimAsync(world.InvoiceId);
        var rowVersion = (await world.Sale.Owner.Client.ClaimAsStaffAsync(claim.Id())).Text("rowVersion")!;
        return new Claimed(world, claim, rowVersion);
    }

    private static List<Notification> Rows(IEnumerable<Notification> rows, string type) => rows.OfType(type).ToList();

    [Fact]
    public async Task Accepting_records_the_offer_tells_the_trader_by_push_and_sms_and_changes_neither_invoice_nor_debt()
    {
        using var api = new QualityClaimApiFactory();
        var c = await ClaimedAsync(api);
        var profile = c.World.Trader.Profile;
        var debtBefore = await c.Owner.CustomerDebtAsync(profile.Id);
        var invoiceBefore = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == c.World.InvoiceId));

        var answer = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion, "PriceCutWholeInvoice", "Giam 10%")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((StatusIds.QualityClaim.OfferMade, "Offer made"), (answer.GetProperty("statusId").GetInt32(), answer.Text("status")));
        Assert.Equal(QualityClaimOffer.PriceCutWholeInvoice, answer.ReadOffer());
        Assert.Equal("Giam 10%", answer.GetProperty("response").Text("note"));

        var rows = await api.NotificationRowsAsync(c.Id);
        var inApp = Assert.Single(Rows(rows.InList(), NotificationTypes.QualityClaimResponse));
        Assert.Equal(("MSG-CLAIM-02", c.World.Trader.Account.Id, c.Claim.Text("code")), (inApp.MessageCode, inApp.RecipientAccountId, inApp.RelatedRecordCode));
        Assert.Contains("is accepted: the cooperative offers a price cut of 30,000 VND on the whole invoice", inApp.Body);
        Assert.Contains("Giam 10%", inApp.Body);
        var sending = Assert.Single(rows.Sendings());
        Assert.Equal((StatusIds.MessageDelivery.Sent, profile.PhoneNumber), (sending.DeliveryStatusId, sending.PhoneNumber));
        Assert.Single(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(profile.PhoneNumber!));
        Assert.Contains(api.Services.GetRequiredService<FakePushSender>().Inbox.List(c.World.Trader.Account.Id.ToString()), p => p.NotificationId == inApp.Id);

        var invoiceAfter = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == c.World.InvoiceId));
        Assert.Equal((invoiceBefore.StatusId, invoiceBefore.Total, invoiceBefore.PriceCutTotal), (invoiceAfter.StatusId, invoiceAfter.Total, invoiceAfter.PriceCutTotal));
        Assert.Equal(debtBefore, await c.Owner.CustomerDebtAsync(profile.Id));
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "QualityClaim.OfferMade" && e.RecordId == c.Id && e.ZoneId == c.World.Sale.ZoneId)));
    }

    [Fact]
    public async Task Rejecting_needs_a_note_and_says_not_accepted_and_a_response_cannot_be_changed()
    {
        using var api = new QualityClaimApiFactory();
        var c = await ClaimedAsync(api);

        Assert.Contains("Note", await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion, note: null)).InvalidFieldsAsync());
        Assert.Contains("Offer", await (await c.Owner.PostAsJsonAsync($"/api/quality-claims/{c.Id}/accept", new { note = "x", rowVersion = c.RowVersion })).InvalidFieldsAsync());

        var answer = await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.QualityClaim.Rejected, answer.GetProperty("statusId").GetInt32());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("offer").ValueKind);
        var inApp = Assert.Single(Rows((await api.NotificationRowsAsync(c.Id)).InList(), NotificationTypes.QualityClaimResponse));
        Assert.Contains("is not accepted", inApp.Body);

        var again = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains("is Rejected", again.Text("detail"));
    }

    [Fact]
    public async Task A_claim_cannot_be_accepted_on_a_cancelled_or_returned_invoice_but_can_be_rejected()
    {
        using var api = new QualityClaimApiFactory();
        var c = await ClaimedAsync(api);
        api.Returns.Returned.Add(c.World.InvoiceId);

        var returned = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ProblemAsync(HttpStatusCode.Conflict, "MSG-CLAIM-03");
        Assert.Contains("has a sales return", returned.Text("detail"));

        api.Returns.Returned.Clear();
        await api.UseDbAsync(db => db.SalesInvoices.Where(i => i.Id == c.World.InvoiceId).ToList().ForEach(i => i.StatusId = StatusIds.SalesInvoice.Cancelled));
        var cancelled = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ProblemAsync(HttpStatusCode.Conflict, "MSG-CLAIM-03");
        Assert.Contains("was cancelled", cancelled.Text("detail"));

        await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_message_that_cannot_be_sent_undoes_nothing_and_a_profile_without_phone_gets_a_not_sent_row()
    {
        using var api = new QualityClaimApiFactory();
        var c = await ClaimedAsync(api);
        api.RefuseSmsTo(c.World.Trader.Profile.PhoneNumber!);
        api.RefusePushTo(c.World.Trader.Account.Id);

        var answer = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(StatusIds.QualityClaim.OfferMade, answer.GetProperty("statusId").GetInt32());
        var sending = answer.GetProperty("sendings").EnumerateArray().Single();
        Assert.Equal(StatusIds.MessageDelivery.NotSent, sending.GetProperty("deliveryStatusId").GetInt32());
        Assert.Single(Rows((await api.NotificationRowsAsync(c.Id)).InList(), NotificationTypes.QualityClaimResponse));

        var other = await ClaimedAsync(api);
        await api.UseDbAsync(db => db.Customers.Where(p => p.Id == other.World.Trader.Profile.Id).ToList().ForEach(p => p.PhoneNumber = null));

        await (await other.Owner.RejectClaimAsync(other.Id, other.RowVersion)).ReadAsync(HttpStatusCode.OK);
        var row = Assert.Single((await api.NotificationRowsAsync(other.Id)).Sendings());
        Assert.Equal((StatusIds.MessageDelivery.NotSent, "NO_PHONE_NUMBER", (string?)null), (row.DeliveryStatusId, row.ProviderErrorCode, row.PhoneNumber));
    }

    [Fact]
    public async Task The_trader_reads_only_their_own_claims_without_internal_fields_and_only_the_owner_responds()
    {
        using var api = new QualityClaimApiFactory();
        var c = await ClaimedAsync(api);
        var trader = c.World.Trader.Client;
        var other = await api.TraderAsync();

        var list = await (await trader.GetAsync("/api/me/quality-claims")).ReadAsync(HttpStatusCode.OK);
        var row = list.GetProperty("items").EnumerateArray().Single();
        Assert.Equal((c.Claim.Text("code"), JsonValueKind.Null), (row.Text("code"), row.GetProperty("customer").ValueKind));
        var detail = await (await trader.GetAsync($"/api/me/quality-claims/{c.Id}")).ReadAsync(HttpStatusCode.OK);
        var properties = detail.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Empty(properties.Intersect(["customer", "sendings", "rowVersion"]));
        var line = detail.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, line.GetProperty("unitPrice").ValueKind);
        Assert.Equal(10m, line.GetProperty("kg").GetDecimal());

        await (await other.Client.GetAsync($"/api/me/quality-claims/{c.Id}")).ReadAsync(HttpStatusCode.NotFound);
        var empty = await (await other.Client.GetAsync("/api/me/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("MSG-GEN-11", empty.Text("messageCode"));

        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync($"/api/quality-claims/{c.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.AcceptClaimAsync(c.Id, c.RowVersion)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.World.Sale.Manager.Client.AcceptClaimAsync(c.Id, c.RowVersion)).StatusCode);
    }

    [Fact]
    public async Task Zone_managers_read_claims_of_their_zones_only_the_accountant_none_and_awaiting_comes_first()
    {
        using var api = new QualityClaimApiFactory();
        var c = await ClaimedAsync(api);
        var answered = await ClaimedAsync(api);
        await (await answered.Owner.RejectClaimAsync(answered.Id, answered.RowVersion)).ReadAsync(HttpStatusCode.OK);
        var manager = c.World.Sale.Manager.Client;

        var mine = await (await manager.GetAsync("/api/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(c.Id, mine.GetProperty("items").EnumerateArray().Single().Id());
        Assert.Equal(c.World.Trader.Profile.Name, mine.GetProperty("items")[0].GetProperty("customer").Text("name"));
        var detail = await manager.ClaimAsStaffAsync(c.Id);
        Assert.Equal(c.World.Sale.ZoneId, detail.GetProperty("invoice").GetProperty("zoneId").GetGuid());
        Assert.Equal(c.World.Trader.Profile.PhoneNumber, detail.GetProperty("customer").Text("phoneNumber"));
        Assert.Equal(300_000m, detail.GetProperty("invoice").GetProperty("total").GetDecimal());
        Assert.Equal(30_000m, detail.GetProperty("invoice").GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/quality-claims/{answered.Id}")).StatusCode);
        var accountant = await api.AccountantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/quality-claims")).StatusCode);

        var ownerList = await (await c.Owner.GetAsync("/api/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([StatusIds.QualityClaim.AwaitingResponse, StatusIds.QualityClaim.Rejected], ownerList.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("statusId").GetInt32()));
        var rejected = await (await c.Owner.GetAsync($"/api/quality-claims?statusId={StatusIds.QualityClaim.Rejected}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(answered.Id, rejected.GetProperty("items").EnumerateArray().Single().Id());
        var none = await (await c.Owner.GetAsync($"/api/quality-claims?statusId={StatusIds.QualityClaim.Accepted}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("MSG-GEN-10", none.Text("messageCode"));
    }

    [Fact]
    public async Task Opening_the_notifications_finds_the_claim_for_owner_and_trader()
    {
        using var api = new QualityClaimApiFactory();
        var c = await ClaimedAsync(api);
        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);
        var rows = await api.NotificationRowsAsync(c.Id);

        var ownerRow = Rows(rows.InList(), NotificationTypes.QualityClaimNew).Single();
        var traderRow = Rows(rows.InList(), NotificationTypes.QualityClaimResponse).Single();
        await (await c.Owner.GetAsync($"/api/me/notifications/{ownerRow.Id}/open")).ReadAsync(HttpStatusCode.OK);
        var opened = await (await c.World.Trader.Client.GetAsync($"/api/me/notifications/{traderRow.Id}/open")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(c.Id, opened.GetProperty("recordId").GetGuid());
        await (await (await api.TraderAsync()).Client.GetAsync($"/api/me/notifications/{traderRow.Id}/open")).ReadAsync(HttpStatusCode.NotFound);
    }
}
