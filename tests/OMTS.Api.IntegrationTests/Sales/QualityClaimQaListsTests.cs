using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of the lists and details of claims: paging, filters, order and what a trader never sees (SCR-CLAIM-02, BR-ACCOUNT-04).</summary>
public class QualityClaimQaListsTests
{
    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).Order().ToArray();

    private static async Task<Guid[]> ClaimsOfOneTraderAsync(AuthApiFactory api, ClaimWorld world, int count)
    {
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            var invoice = await world.Sale.SellToAsync(world.Trader.Profile.Id, world.Sale.OtherLotId);
            ids[i] = (await world.Trader.Client.SubmitClaimAsync(invoice)).Id();
        }

        return ids;
    }

    [Fact]
    public async Task The_trader_detail_and_list_carry_only_the_fields_of_the_trader()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion, note: "Offer")).ReadAsync(HttpStatusCode.OK);

        var detail = await (await c.World.Trader.Client.GetAsync($"/api/me/quality-claims/{c.Id}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(
            new[] { "code", "description", "id", "invoice", "invoiceDate", "invoiceTotal", "lines", "offer", "photos", "response", "sentAt", "status", "statusId" }.Order().ToArray(),
            Names(detail));
        Assert.Equal(new[] { "note", "respondedAt" }.Order().ToArray(), Names(detail.GetProperty("response")));
        Assert.Equal(new[] { "kg", "lotCode", "productName", "unitPrice", "amount" }.Order().ToArray(), Names(detail.GetProperty("lines")[0]));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("lines")[0].GetProperty("amount").ValueKind);

        var row = (await (await c.World.Trader.Client.GetAsync("/api/me/quality-claims")).ReadAsync(HttpStatusCode.OK)).Items().Single();
        Assert.Equal(JsonValueKind.Null, row.GetProperty("customer").ValueKind);
        Assert.DoesNotContain("phone", row.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", detail.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", detail.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_staff_detail_shows_the_invoice_customer_and_sendings_of_step_one()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        await (await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).ReadAsync(HttpStatusCode.OK);

        var detail = await c.Owner.ClaimAsStaffAsync(c.Id);

        Assert.Equal(1, detail.GetProperty("sendings").GetArrayLength());
        Assert.Equal(c.World.Trader.Profile.PhoneNumber, detail.GetProperty("sendings")[0].Text("phoneNumber"));
        Assert.Equal(1, detail.GetProperty("photos").GetArrayLength());
        Assert.Equal(300_000m, detail.GetProperty("invoice").GetProperty("total").GetDecimal());
        Assert.Equal(10m, detail.GetProperty("invoice").GetProperty("lines")[0].GetProperty("kg").GetDecimal());
    }

    [Fact]
    public async Task The_owner_list_puts_awaiting_first_newest_first_then_the_answered_and_pages_stay_whole()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var ids = await ClaimsOfOneTraderAsync(api, world, 3);
        var owner = world.Sale.Owner.Client;
        var version = (await owner.ClaimAsStaffAsync(ids[0])).Text("rowVersion");
        await (await owner.RejectClaimAsync(ids[0], version)).ReadAsync(HttpStatusCode.OK);

        var all = await (await owner.GetAsync("/api/quality-claims?pageSize=100")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(new[] { ids[2], ids[1], ids[0] }, all.Items().Select(r => r.Id()).ToArray());
        var first = await (await owner.GetAsync("/api/quality-claims?page=1&pageSize=2")).ReadAsync(HttpStatusCode.OK);
        var second = await (await owner.GetAsync("/api/quality-claims?page=2&pageSize=2")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((2, 3), (first.Items().Count(), first.GetProperty("total").GetInt32()));
        Assert.Equal(ids[0], second.Items().Single().Id());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-5")]
    [InlineData("page=2147483647")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=-1")]
    [InlineData("pageSize=100000")]
    public async Task Paging_values_out_of_range_are_clamped_never_500(string query)
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        await (await c.Owner.GetAsync($"/api/quality-claims?{query}")).ReadAsync(HttpStatusCode.OK);
        await (await c.World.Trader.Client.GetAsync($"/api/me/quality-claims?{query}")).ReadAsync(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("page=abc")]
    [InlineData("pageSize=1.5")]
    [InlineData("statusId=abc")]
    public async Task Paging_or_filter_values_of_the_wrong_kind_answer_400_msg_gen_01(string query)
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        var answer = await (await c.Owner.GetAsync($"/api/quality-claims?{query}")).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("MSG-GEN-01", answer.Text("code"));
    }

    [Fact]
    public async Task A_status_of_no_claim_is_an_empty_page_with_msg_gen_10()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        var none = await (await c.Owner.GetAsync("/api/quality-claims?statusId=99999")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-GEN-10", none.Text("messageCode"));
        Assert.Empty(none.Items());
    }

    [Fact]
    public async Task A_wrong_kind_of_id_answers_404_never_500()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await c.Owner.GetAsync("/api/quality-claims/not-a-guid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.World.Trader.Client.GetAsync("/api/me/quality-claims/123")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Owner.PostAsync("/api/quality-claims/not-a-guid/accept", null)).StatusCode);
    }

    [Fact]
    public async Task The_trader_list_is_newest_first_and_counts_only_the_traders_own_claims()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var mine = await ClaimsOfOneTraderAsync(api, world, 2);
        var other = await api.QaClaimedAsync();

        var list = await (await world.Trader.Client.GetAsync("/api/me/quality-claims")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(new[] { mine[1], mine[0] }, list.Items().Select(r => r.Id()).ToArray());
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        Assert.DoesNotContain(list.Items(), r => r.Id() == other.Id);
    }
}
