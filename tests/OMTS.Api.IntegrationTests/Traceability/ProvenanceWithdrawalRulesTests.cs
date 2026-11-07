using System.Net;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// TRACE-04 PRE-2, TRACE-04.0.E1 and BR-TRACE-02/06: only a Published lot is withdrawn, with a reason; a Withdrawn lot is refused until the Owner publishes it again (TRACE-01.2), after
/// which it can be withdrawn again.
/// </summary>
public class ProvenanceWithdrawalRulesTests
{
    private static string WrongStatus(string code, string state) => $"Harvest lot {code} is {state} and cannot be used for this action.";

    [Fact]
    public async Task Only_a_published_lot_is_withdrawn_and_the_reason_is_required()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var code = (await api.LotRowAsync(sale.LotId)).LotCode;

        // PRE-2: a lot that is Not published has no withdrawal.
        foreach (var refused in new[] { await sale.Owner.Client.GetWithdrawPreviewAsync(sale.LotId), await sale.Owner.Client.PostWithdrawAsync(sale.LotId) })
        {
            var body = await refused.ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(("MSG-GEN-09", WrongStatus(code, "Not published")), (body.Text("code"), body.Text("detail")));
        }

        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);

        // TRACE-04.0.E1: no reason (or one too long) withdraws nothing and tells nobody.
        foreach (var reason in new[] { null, "   ", new string('x', 1001) })
        {
            Assert.Equal(["Reason"], await (await sale.Owner.Client.PostWithdrawAsync(sale.LotId, reason)).InvalidFieldsAsync());
        }

        Assert.Equal(HarvestLotOriginState.Published, (await api.LotRowAsync(sale.LotId)).OriginState);
        Assert.Empty(await api.WithdrawAuditAsync(sale.LotId));
        Assert.Empty(await api.NoticeSendingsAsync(sale.LotId));

        var withdrawn = await sale.Owner.Client.WithdrawnAsync(sale.LotId, "  Data in doubt  ");
        Assert.Equal("Withdrawn", withdrawn.GetProperty("lot").Text("originState"));
        Assert.Equal("Data in doubt", (await api.LotRowAsync(sale.LotId)).WithdrawalReason);

        // Withdrawn once: a second withdrawal and its preview are refused, nothing is sent again.
        foreach (var refused in new[] { await sale.Owner.Client.GetWithdrawPreviewAsync(sale.LotId), await sale.Owner.Client.PostWithdrawAsync(sale.LotId) })
        {
            var body = await refused.ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(("MSG-GEN-09", WrongStatus(code, "Withdrawn")), (body.Text("code"), body.Text("detail")));
        }

        Assert.Single(await api.WithdrawAuditAsync(sale.LotId));
        Assert.Single(await api.NoticeSendingsAsync(sale.LotId));
        Assert.Equal(HttpStatusCode.NotFound, (await sale.Owner.Client.GetWithdrawPreviewAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sale.Owner.Client.PostWithdrawAsync(Guid.NewGuid())).StatusCode);
    }

    // BR-TRACE-02: the Owner publishes a Withdrawn lot again (a new publish time; the Zone Manager cannot), and a lot Published again can be withdrawn again, telling its traders again.
    [Fact]
    public async Task A_lot_published_again_after_a_withdrawal_can_be_withdrawn_again()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        await sale.Owner.Client.WithdrawnAsync(sale.LotId, "First doubt");
        var first = await api.LotRowAsync(sale.LotId);

        Assert.Equal(HttpStatusCode.Forbidden, (await sale.Manager.Client.PostPublishAsync(sale.LotId)).StatusCode);
        var republished = await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Published", true), (republished.GetProperty("lot").Text("originState"), republished.GetProperty("republished").GetBoolean()));
        var again = await api.LotRowAsync(sale.LotId);
        Assert.Equal((first.WithdrawnAt, "First doubt"), (again.WithdrawnAt, again.WithdrawalReason));
        Assert.True(again.PublishedAt > again.WithdrawnAt);
        Assert.True((await sale.Owner.Client.TraceAsync(sale.LotId)).GetProperty("canWithdraw").GetBoolean());

        var second = await sale.Owner.Client.WithdrawnAsync(sale.LotId, "Second doubt");

        Assert.Equal("Withdrawn", second.GetProperty("lot").Text("originState"));
        var row = await api.LotRowAsync(sale.LotId);
        Assert.Equal("Second doubt", row.WithdrawalReason);
        Assert.True(row.WithdrawnAt > again.PublishedAt);
        Assert.Equal(["First doubt", "Second doubt"], (await api.WithdrawAuditAsync(sale.LotId)).Select(e => e.Reason));
        Assert.Equal(2, (await api.NoticeSendingsAsync(sale.LotId)).Count);
    }
}
