using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>DEBT-02.2 and DEBT-02.4: the payments Awaiting confirmation, one list confirmed and cancelled with one notice, and the cancellation of one payment (BR-DEBT-03/04/06).</summary>
public class CustomerPaymentConfirmationTests
{
    [Fact]
    public async Task One_list_confirms_and_cancels_the_port_cash_changes_no_debt_by_the_confirmations_and_tells_the_owner_once()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var accountant = await api.AllZonesAccountantAsync();
        var client = accountant.Client;
        var nothing = await (await client.GetAwaitingAsync()).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-11", 0), (nothing.Text("messageCode"), nothing.GetProperty("page").GetProperty("total").GetInt32()));

        var older = await sale.PayLaterAsync(sale.CustomerId, 20m, cash: 100_000m);
        await api.BackDateAsync(older.Id(), 3);
        var paidInCash = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId))).GetProperty("invoice");
        var walkIn = (await sale.Manager.Client.SellAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m), sale.LotId))).GetProperty("invoice");
        var (first, second, third) = (await api.PortCashOfAsync(older), await api.PortCashOfAsync(paidInCash), await api.PortCashOfAsync(walkIn));

        // Oldest first, each with its customer (none for the walk-in), invoice, amount and the Zone Manager who collected it.
        var rows = await client.AwaitingRowsAsync();
        Assert.Equal([first, second, third], rows.Select(r => r.Id()));
        Assert.Equal((older.Text("code"), sale.Manager.Account.Id, "Cash"), (rows[0].GetProperty("invoice").Text("code"), rows[0].GetProperty("collectedBy").Id(), rows[0].Text("method")));
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("customer").ValueKind);
        Assert.Equal(430_000m, (await (await client.GetAwaitingAsync()).ReadAsync(HttpStatusCode.OK)).Money("totalAmount"));
        Assert.Equal((500_000m, 500_000m, 500_000m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));

        var answer = await (await client.ConfirmAsync([first, third], (second, "The cash was not handed over"))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("MSG-GEN-05", 2, 1), (answer.Text("messageCode"), answer.GetProperty("confirmed").GetArrayLength(), answer.GetProperty("cancelled").GetArrayLength()));
        var confirmed = answer.GetProperty("confirmed")[0];
        Assert.Equal(("Completed", accountant.Account.Id), (confirmed.Text("status"), confirmed.GetProperty("confirmedBy").Id()));
        var cancelled = answer.GetProperty("cancelled")[0];
        Assert.Equal(("Cancelled", "The cash was not handed over"), (cancelled.Text("status"), cancelled.Text("cancelReason")));
        // The confirmations change no debt (they already counted); the cancelled cash no longer counts (BR-DEBT-02).
        Assert.Equal((800_000m, 800_000m, 800_000m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));
        Assert.Equal(0, (await client.DebtListAsync()).GetProperty("awaitingConfirmationCount").GetInt32());
        Assert.Equal("MSG-GEN-11", (await (await client.GetAwaitingAsync()).ReadAsync(HttpStatusCode.OK)).Text("messageCode"));

        var name = await api.QueryAsync(db => db.Customers.Where(c => c.Id == sale.CustomerId).Select(c => c.Name).SingleAsync());
        var notice = Assert.Single(await api.TransactionNoticesAsync(sale.Owner.Account.Id));
        var codes = answer.GetProperty("confirmed").EnumerateArray().Select(p => p.Text("code")).ToList();
        Assert.Equal(
            $"{accountant.Account.DisplayName} confirmed 2 payments ({codes[0]}, {codes[1]}) of 130,000 VND and cancelled 1 payment ({cancelled.Text("code")}) of 300,000 VND. {name} now owes the cooperative 800,000 VND.",
            notice.Body);
        Assert.Null(notice.RecordId);
        Assert.Equal(430_000m, JsonDocument.Parse(notice.Params!).RootElement.Money("amount"));

        // BR-DEBT-04: the cancelled cash stays on its date, counts 0 and shows its cancel date; the confirmed one is no longer Awaiting.
        var entries = (await client.StatementAsync(sale.CustomerId, PurchaseRequestTestKit.Today.AddDays(-5), PurchaseRequestTestKit.Today)).GetProperty("entries").EnumerateArray().ToList();
        var cancelledEntry = entries.Single(e => e.Id() == second);
        Assert.Equal((true, 0m), (cancelledEntry.GetProperty("cancelled").GetBoolean(), cancelledEntry.Money("debtChange")));
        Assert.NotEqual(JsonValueKind.Null, cancelledEntry.GetProperty("cancelledAt").ValueKind);
        Assert.False(entries.Single(e => e.Id() == first).GetProperty("awaitingConfirmation").GetBoolean());
        Assert.Equal(2, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "CustomerPayment.Confirmed")));

        // The same list again: nothing is Awaiting any more, nothing changes and nobody is told again.
        var again = await (await client.ConfirmAsync([first])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", first), (again.Text("code"), again.GetProperty("paymentId").GetGuid()));
        Assert.Single(await api.TransactionNoticesAsync(sale.Owner.Account.Id));
    }

    [Fact]
    public async Task A_list_is_saved_whole_or_not_at_all()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = (await api.AllZonesAccountantAsync()).Client;
        var awaiting = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 50_000m));
        var recorded = (await client.PaidAsync(MoneyBody(sale.CustomerId, 10_000m))).Payment().Id();

        Assert.Equal(("MSG-GEN-01", "Payments"), await FieldAsync(await client.ConfirmAsync([])));
        Assert.Equal(("MSG-GEN-01", "Payments"), await FieldAsync(await client.ConfirmAsync([awaiting], (awaiting, "Twice"))));
        var noReason = await (await client.ConfirmAsync([], (awaiting, " "))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Reason", awaiting), (noReason.Text("field"), noReason.GetProperty("paymentId").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.ConfirmAsync([awaiting, Guid.NewGuid()])).StatusCode);
        // A payment the Accountant recorded is Confirmed already: the whole list is refused, the cash stays Awaiting.
        Assert.Equal("MSG-GEN-09", (await (await client.ConfirmAsync([awaiting, recorded])).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, await api.QueryAsync(db => db.CustomerPayments.Where(p => p.Id == awaiting).Select(p => p.StatusId).SingleAsync()));
        Assert.Single(await api.TransactionNoticesAsync(sale.Owner.Account.Id));
    }

    [Fact]
    public async Task A_payment_is_cancelled_with_a_reason_leaves_the_debt_and_the_owner_opens_it_from_the_notice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 10m);
        var client = (await api.AllZonesAccountantAsync()).Client;
        var payment = (await client.PaidAsync(MoneyBody(sale.CustomerId, 200_000m))).Payment();

        Assert.Equal(("MSG-GEN-01", "Reason"), await FieldAsync(await client.CancelAsync(payment.Id(), "  ")));
        var cancelled = await (await client.CancelAsync(payment.Id(), "Recorded on the wrong customer")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((300_000m, $"Cancellation of payment {payment.Text("code")} saved."), (cancelled.Money("debtAfter"), cancelled.Text("message")));
        Assert.Equal(("Cancelled", "Recorded on the wrong customer"), (cancelled.Payment().Text("status"), cancelled.Payment().Text("cancelReason")));
        Assert.Equal((300_000m, 300_000m, 300_000m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));
        Assert.Equal("MSG-GEN-09", (await (await client.CancelAsync(payment.Id(), "Again")).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.CancelAsync(Guid.NewGuid(), "Unknown")).StatusCode);

        var notices = await api.TransactionNoticesAsync(sale.Owner.Account.Id);
        Assert.Equal(2, notices.Count);
        Assert.Contains($"cancelled payment {payment.Text("code")} of 200,000 VND from", notices[1].Body);
        Assert.EndsWith("now owes the cooperative 300,000 VND.", notices[1].Body);
        var row = await api.QueryAsync(db => db.Notifications.FirstAsync(n => n.Type == NotificationTypes.CustomerPaymentTransaction && n.RecipientAccountId == sale.Owner.Account.Id));
        var opened = await (await sale.Owner.Client.OpenNotificationAsync(row.Id)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((RelatedRecordTypes.CustomerPayment, payment.Id()), (opened.Text("recordType"), opened.GetProperty("recordId").GetGuid()));
        Assert.Equal("Cancelled", (await (await sale.Owner.Client.GetPaymentAsync(payment.Id())).ReadAsync(HttpStatusCode.OK)).Text("status"));
    }

    // QA L1 of 8a, now reachable: a transfer partly paid back, then cancelled. The 8a default is kept (PM question): the debt is the formula, the unpaid invoices explain
    // only the part an invoice holds, and the age is the invoice's.
    [Fact]
    public async Task A_transfer_partly_paid_back_then_cancelled_leaves_a_debt_above_the_unpaid_invoices_on_the_screens()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.PayLaterAsync(sale.CustomerId, 10m);
        var client = (await api.AllZonesAccountantAsync()).Client;
        var transfer = (await client.PaidAsync(MoneyBody(sale.CustomerId, 450_000m).With("confirmAboveDebt", true))).Payment();
        await (await client.PayBackAsync(MoneyBody(sale.CustomerId, 150_000m, "Cash"))).ReadAsync(HttpStatusCode.Created);

        var cancelled = await (await client.CancelAsync(transfer.Id(), "Never arrived")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(450_000m, cancelled.Money("debtAfter"));
        Assert.Equal((450_000m, 450_000m, 450_000m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));
        var statement = await client.StatementAsync(sale.CustomerId);
        Assert.Equal([(invoice.Text("code"), 300_000m)], statement.Unpaid());
        Assert.Equal(0, statement.GetProperty("debtAgeDays").GetInt32());
        Assert.Equal(0, (await client.DebtListAsync()).RowOf(sale.CustomerId).GetProperty("debtAgeDays").GetInt32());
    }

    private static async Task<(string?, string?)> FieldAsync(HttpResponseMessage response)
    {
        var body = await response.ReadAsync(HttpStatusCode.BadRequest);
        return (body.Text("code"), body.Text("field"));
    }
}
