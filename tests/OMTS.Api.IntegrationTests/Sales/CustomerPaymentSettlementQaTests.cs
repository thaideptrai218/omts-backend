using System.Net;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentSettlementTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA probes of DEBT-02.6 (7b v2). They were written without being run (Bash broken, develop's cycle start answers 404): each asserts what the code does today by reading it, so
/// a failure means the reading was wrong. They record behaviours the PM should confirm, they are not defect tests.
/// </summary>
public class CustomerPaymentSettlementQaTests
{
    // QA-M2: attach asks no MSG-DEBT-04. A profile that owes nothing gets 120,000 and is then owed money back; no flag, no question, 200 at once.
    [Fact]
    public async Task Probe_attaching_to_a_profile_that_owes_nothing_is_saved_without_a_question_and_leaves_money_owed_back()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var orphan = await api.OrphanTransferAsync(120_000m);

        var answer = await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(-120_000m, answer.Settled().Money("debtAfter"));
        Assert.Empty(await api.AllocationsOfAsync(orphan.Id));
        Assert.Equal(-120_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
    }

    // QA-L1: the reading a later payment of the list is checked against only takes off the parts of the first one, not what its rest covers through the pool of the debt reading.
    // P1 700,000 names the newer invoice (600,000); its other 100,000 covers the older invoice (300,000 -> 200,000 unpaid). P2 300,000 naming the older invoice for 300,000 is accepted
    // (a payment recorded after P1 on its own would be refused: that invoice has 200,000 left). The debt is right; only the part stored for P2 is more than its invoice needed.
    [Fact]
    public async Task Probe_a_second_payment_of_one_list_may_name_an_invoice_that_the_rest_of_the_first_one_already_covers()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var (older, newer) = await w.TwoInvoicesAsync();
        var first = await api.OrphanTransferAsync(700_000m);
        var second = await api.OrphanTransferAsync(300_000m);

        var answer = await (await accountant.SettleAsync(
            ToCustomer(first.Id, sale.CustomerId, (newer.Id(), 600_000m)),
            ToCustomer(second.Id, sale.CustomerId, (older.Id(), 300_000m)))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal([-100_000m, -100_000m], answer.GetProperty("settled").EnumerateArray().Select(s => s.Money("debtAfter")));
        Assert.Equal(-100_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal([(older.Text("code")!, 300_000m)], await api.AllocationsOfAsync(second.Id));
        Assert.Empty((await accountant.StatementAsync(sale.CustomerId)).Unpaid());
    }

    // QA-M1: an attached payment is Completed and one-way. Cancelled afterwards (wrong profile), the transfer cannot be settled again nor paid back: it is Cancelled for good while the
    // money is still in the bank; its receipt stays in the cash book as a Cancelled line.
    [Fact]
    public async Task Probe_a_transfer_attached_to_the_wrong_profile_can_only_be_cancelled_and_then_cannot_be_settled_again()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var orphan = await api.OrphanTransferAsync(120_000m);
        await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).ReadAsync(HttpStatusCode.OK);

        await (await accountant.CancelAsync(orphan.Id, "Wrong profile")).ReadAsync(HttpStatusCode.OK);
        var again = await (await accountant.SettleAsync(PayBack(orphan.Id))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-GEN-09", again.Text("code"));
        Assert.Equal(StatusIds.CustomerPayment.Cancelled, (await api.PaymentByIdAsync(orphan.Id)).StatusId);
        Assert.Empty(await api.PaidBackForAsync(orphan.Id));
        Assert.Equal("Cancelled", Assert.Single(await accountant.CashBookLinesAsync("Bank", orphan.Code)).Text("status"));
        Assert.Equal(0m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
    }
}
