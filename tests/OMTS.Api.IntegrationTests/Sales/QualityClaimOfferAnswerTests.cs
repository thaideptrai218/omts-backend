using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// CLAIM-02, PM answer 12/11 ([7.3]): the Owner proposes, the trader accepts or declines. The price cut amount is its own field, required for a price cut offer; the answer of the trader,
/// who may answer once and only their own claim; an open offer keeps the invoice claimed.
/// </summary>
public class QualityClaimOfferAnswerTests
{
    [Fact]
    public async Task A_price_cut_offer_needs_its_amount_above_zero_at_most_the_invoice_total_and_a_return_takes_none()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var total = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().Where(i => i.Id == c.World.InvoiceId).Select(i => i.Total).SingleAsync());

        foreach (var amount in new decimal?[] { null, 0m, -1m, total + 1m, 1.5m })
        {
            var refused = await c.Owner.PostAsJsonAsync($"/api/quality-claims/{c.Id}/accept", new { offer = "PriceCutWholeInvoice", priceCutAmount = amount, rowVersion = c.RowVersion });
            Assert.Equal(["Price cut amount"], await refused.InvalidFieldsAsync());
        }

        Assert.Equal(["Price cut amount"], await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion, "ReturnAllGoods", priceCutAmount: 10_000m)).InvalidFieldsAsync());

        var whole = await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion, "PriceCutWholeInvoice", priceCutAmount: total)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(total, whole.Money("offerAmount"));
        Assert.Equal(StatusIds.QualityClaim.OfferMade, whole.GetProperty("statusId").GetInt32());
    }

    [Fact]
    public async Task A_return_offer_has_no_amount_and_the_trader_sees_the_amount_of_a_price_cut_offer()
    {
        using var api = new QualityClaimApiFactory();
        var returned = await api.QaClaimedAsync();
        var cut = await api.QaClaimedAsync();

        var returnOffer = await (await returned.Owner.AcceptClaimAsync(returned.Id, returned.RowVersion)).ReadAsync(HttpStatusCode.OK);
        await (await cut.Owner.AcceptClaimAsync(cut.Id, cut.RowVersion, "PriceCutWholeInvoice", priceCutAmount: 45_000m)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, returnOffer.GetProperty("offerAmount").ValueKind);
        var seen = await (await cut.World.Trader.Client.GetAsync($"/api/me/quality-claims/{cut.Id}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((45_000m, JsonValueKind.Null), (seen.Money("offerAmount"), seen.GetProperty("answer").ValueKind));
    }

    [Fact]
    public async Task The_trader_accepts_the_offer_once_and_it_is_audited_and_changes_neither_invoice_nor_debt()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var profile = c.World.Trader.Profile;
        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion, "PriceCutWholeInvoice", priceCutAmount: 20_000m)).ReadAsync(HttpStatusCode.OK);
        var debtBefore = await c.Owner.CustomerDebtAsync(profile.Id);
        var totalBefore = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().Where(i => i.Id == c.World.InvoiceId).Select(i => new { i.Total, i.StatusId, i.PriceCutTotal }).SingleAsync());

        var answer = await (await c.World.Trader.Client.AcceptOfferAsync(c.Id, "Ok, thanks")).ReadAsync(HttpStatusCode.OK);

        var claim = answer.GetProperty("claim");
        Assert.Equal(StatusIds.QualityClaim.Accepted, claim.GetProperty("statusId").GetInt32());
        Assert.Equal(("Ok, thanks", 20_000m), (claim.GetProperty("answer").Text("note"), claim.Money("offerAmount")));
        Assert.Equal("MSG-GEN-05", answer.Text("messageCode"));
        var row = await api.QueryAsync(db => db.QualityClaims.AsNoTracking().SingleAsync(x => x.Id == c.Id));
        Assert.NotNull(row.AnsweredAt);
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == c.Id && e.Action == "QualityClaim.OfferAccepted" && e.ZoneId == c.World.Sale.ZoneId)));
        Assert.Equal(debtBefore, await c.Owner.CustomerDebtAsync(profile.Id));
        Assert.Equal(totalBefore, await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().Where(i => i.Id == c.World.InvoiceId).Select(i => new { i.Total, i.StatusId, i.PriceCutTotal }).SingleAsync()));

        // Once: a second answer, either way, sees the status that won.
        await (await c.World.Trader.Client.DeclineOfferAsync(c.Id)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await c.World.Trader.Client.AcceptOfferAsync(c.Id)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var staff = await c.Owner.ClaimAsStaffAsync(c.Id);
        Assert.Equal("Ok, thanks", staff.GetProperty("answer").Text("note"));
    }

    [Fact]
    public async Task The_trader_declines_the_offer_with_or_without_a_note_and_a_new_claim_is_open_after_it()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);

        var answer = await (await c.World.Trader.Client.DeclineOfferAsync(c.Id)).ReadAsync(HttpStatusCode.OK);

        var claim = answer.GetProperty("claim");
        Assert.Equal(StatusIds.QualityClaim.OfferDeclined, claim.GetProperty("statusId").GetInt32());
        Assert.Equal(JsonValueKind.Null, claim.GetProperty("answer").GetProperty("note").ValueKind);
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == c.Id && e.Action == "QualityClaim.OfferDeclined")));
        Assert.Equal(StatusIds.QualityClaim.AwaitingResponse, (await c.World.Trader.Client.SubmitClaimAsync(c.World.InvoiceId)).GetProperty("statusId").GetInt32());
    }

    [Fact]
    public async Task Only_the_trader_of_the_claim_answers_and_only_an_offer_that_is_waiting()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var other = await api.TraderAsync();

        // Awaiting response: nothing to answer yet.
        await (await c.World.Trader.Client.AcceptOfferAsync(c.Id)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await c.World.Trader.Client.DeclineOfferAsync(c.Id)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await c.Owner.RejectClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);
        await (await c.World.Trader.Client.AcceptOfferAsync(c.Id)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        var offered = await api.QaClaimedAsync();
        await (await offered.Owner.AcceptClaimAsync(offered.Id, offered.RowVersion)).ReadAsync(HttpStatusCode.OK);
        await (await other.Client.AcceptOfferAsync(offered.Id)).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await other.Client.DeclineOfferAsync(offered.Id)).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await offered.Owner.PostAsJsonAsync($"/api/me/quality-claims/{offered.Id}/accept-offer", new { })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(StatusIds.QualityClaim.OfferMade, (await api.QueryAsync(db => db.QualityClaims.AsNoTracking().SingleAsync(x => x.Id == offered.Id))).StatusId);
    }

    [Fact]
    public async Task A_note_of_the_trader_is_cut_at_a_thousand_characters_and_an_offer_keeps_the_invoice_claimed()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);

        await (await c.World.Trader.Client.PostClaimAsync(c.World.InvoiceId)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal(["Note"], await (await c.World.Trader.Client.AcceptOfferAsync(c.Id, new string('x', 1001))).InvalidFieldsAsync());
        await (await c.World.Trader.Client.AcceptOfferAsync(c.Id, new string('x', 1000))).ReadAsync(HttpStatusCode.OK);
    }
}
