using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of PR 8b (DEBT-02, OMTS-224): amounts on both endpoints, MSG-DEBT-05 with money paid back and cancelled payments, the Owner's own actions (BR-DEBT-06),
/// cancelling money paid back, and the staff statement (BR-DEBT-04).</summary>
public class CustomerPaymentQaTests
{
    private static async Task<string> NameAsync(AuthApiFactory api, Guid customerId) =>
        await api.QueryAsync(db => db.Customers.Where(c => c.Id == customerId).Select(c => c.Name).SingleAsync());

    // BR-DEBT-03 "the amount is greater than 0", whole VND, on a payment and on money paid back; refused before any question and nothing is saved.
    [Fact]
    public async Task Zero_negative_and_decimal_amounts_are_refused_on_a_payment_and_on_money_paid_back()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 10m);
        var client = (await api.AllZonesAccountantAsync()).Client;
        await client.PaidAsync(MoneyBody(sale.CustomerId, 500_000m).With("confirmAboveDebt", true));

        foreach (var amount in new[] { 0m, -1_000m, 0.4m, 1_000.5m, 1_000_000_000_000_000_000m })
        {
            var payment = await (await client.RecordAsync(MoneyBody(sale.CustomerId, amount).With("confirmAboveDebt", true))).ReadAsync(HttpStatusCode.BadRequest);
            var paidBack = await (await client.PayBackAsync(MoneyBody(sale.CustomerId, amount, "Cash"))).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", "Amount"), (payment.Text("code"), payment.Text("field")));
            Assert.Equal(("MSG-GEN-01", "Amount"), (paidBack.Text("code"), paidBack.Text("field")));
        }

        // The VietQR code of an invoice is the system's method only (GFR-08), for money paid back too.
        Assert.Equal("Method", (await (await client.PayBackAsync(MoneyBody(sale.CustomerId, 1_000m, "VietQrInvoice"))).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));

        // Exactly the money owed back can be paid back: the debt is then 0 on every screen.
        var all = await (await client.PayBackAsync(MoneyBody(sale.CustomerId, 200_000m))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(0m, all.Money("debtAfter"));
        Assert.Equal((0m, 0m, 0m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));
        Assert.Equal(2, await api.QueryAsync(db => db.CustomerPayments.CountAsync(p => p.CustomerId == sale.CustomerId && p.RecordedByAccountId != null)));
    }

    // DEBT-02.3 step 4 sends DEBT-02.5 back to step 3 of DEBT-02.3, so money paid back by transfer is checked too; BR-DEBT-03 checks only payments that are not cancelled.
    [Fact]
    public async Task A_bank_reference_is_asked_for_money_paid_back_too_and_is_free_again_once_its_payments_are_cancelled()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 10m);
        var client = (await api.AllZonesAccountantAsync()).Client;
        var reference = NewReference();
        var transfer = (await client.PaidAsync(MoneyBody(sale.CustomerId, 500_000m).With("bankReference", reference).With("confirmAboveDebt", true))).Payment();

        var payBack = MoneyBody(sale.CustomerId, 100_000m).With("bankReference", reference);
        var asked = await (await client.PayBackAsync(payBack)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-DEBT-05", transfer.Text("code")), (asked.Text("code"), asked.Text("paymentCode")));
        var paidBack = (await (await client.PayBackAsync(payBack.With("confirmDuplicateBankReference", true))).ReadAsync(HttpStatusCode.Created)).Payment();

        (await client.CancelAsync(transfer.Id(), "Never arrived")).EnsureSuccessStatusCode();
        var stillOnPaidBack = await (await client.RecordAsync(MoneyBody(sale.CustomerId, 1_000m).With("bankReference", reference))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-DEBT-05", paidBack.Text("code")), (stillOnPaidBack.Text("code"), stillOnPaidBack.Text("paymentCode")));

        (await client.CancelAsync(paidBack.Id(), "Recorded by mistake")).EnsureSuccessStatusCode();
        var free = await (await client.RecordAsync(MoneyBody(sale.CustomerId, 1_000m).With("bankReference", reference))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(299_000m, free.Money("debtAfter"));
    }

    // BR-DEBT-06: "The Cooperative Owner also receives it when the Cooperative Owner did the action"; DEBT-02 is Full for the Owner (§I.4.4).
    [Fact]
    public async Task The_owner_who_records_pays_back_and_cancels_is_told_of_each_once()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 10m);
        var owner = sale.Owner.Client;

        var paid = (await owner.PaidAsync(MoneyBody(sale.CustomerId, 400_000m).With("confirmAboveDebt", true))).Payment();
        var paidBack = (await (await owner.PayBackAsync(MoneyBody(sale.CustomerId, 100_000m, "Cash"))).ReadAsync(HttpStatusCode.Created)).Payment();
        (await owner.CancelAsync(paidBack.Id(), "Recorded twice")).EnsureSuccessStatusCode();

        var notices = await api.TransactionNoticesAsync(sale.Owner.Account.Id);
        Assert.Equal(3, notices.Count);
        Assert.Contains($"recorded payment {paid.Text("code")} of 400,000 VND", notices[0].Body);
        Assert.Contains($"recorded money paid back {paidBack.Text("code")} of 100,000 VND", notices[1].Body);
        Assert.Contains($"cancelled money paid back {paidBack.Text("code")} of 100,000 VND", notices[2].Body);
        Assert.Equal([paid.Id(), paidBack.Id(), paidBack.Id()], notices.Select(n => n.RecordId!.Value));
        Assert.Equal(sale.Owner.Account.Id, paid.GetProperty("confirmedBy").Id());
    }

    // DEBT-02.4 cancels money paid back too: the debt goes back below zero; a cancelled payment cannot be cancelled or confirmed again, alone or from the list (MSG-GEN-09).
    [Fact]
    public async Task Cancelling_money_paid_back_owes_it_again_and_a_cancelled_entry_is_final_everywhere()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 10m);
        var client = (await api.AllZonesAccountantAsync()).Client;
        await client.PaidAsync(MoneyBody(sale.CustomerId, 500_000m).With("confirmAboveDebt", true));
        var paidBack = (await (await client.PayBackAsync(MoneyBody(sale.CustomerId, 200_000m, "Cash"))).ReadAsync(HttpStatusCode.Created)).Payment();
        var name = await NameAsync(api, sale.CustomerId);

        var cancelled = await (await client.CancelAsync(paidBack.Id(), "Not handed to the customer")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((-200_000m, $"Cancellation of money paid back {paidBack.Text("code")} saved."), (cancelled.Money("debtAfter"), cancelled.Text("message")));
        Assert.Equal((-200_000m, -200_000m, -200_000m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));
        Assert.EndsWith($"cancelled money paid back {paidBack.Text("code")} of 200,000 VND to {name}. The cooperative now owes {name} 200,000 VND.",
            (await api.TransactionNoticesAsync(sale.Owner.Account.Id))[^1].Body);
        var entry = (await client.StatementAsync(sale.CustomerId)).GetProperty("entries").EnumerateArray().Single(e => e.Id() == paidBack.Id());
        Assert.Equal((true, 0m), (entry.GetProperty("cancelled").GetBoolean(), entry.Money("debtChange")));

        Assert.Equal("MSG-GEN-09", (await (await client.ConfirmAsync([], (paidBack.Id(), "Again"))).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal("MSG-GEN-09", (await (await client.ConfirmAsync([paidBack.Id()])).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal("MSG-GEN-09", (await (await client.CancelAsync(paidBack.Id(), "Again")).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal(3, (await api.TransactionNoticesAsync(sale.Owner.Account.Id)).Count);
    }

    // BR-DEBT-04: "The statement that the trader sees (DEBT-04) shows the same entries without the internal data of the payments: who recorded or confirmed them, the
    // Zone Manager who collected the cash, their notes and their cancel reasons" - so the statement of DEBT-03 (staff) shows them, and DEBT-03.2 exports what is shown.
    [Fact]
    public async Task The_staff_statement_shows_who_recorded_and_collected_each_payment_its_note_and_its_cancel_reason()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 10m);
        var accountant = await api.AllZonesAccountantAsync();
        var cash = (await accountant.Client.PaidAsync(MoneyBody(sale.CustomerId, 100_000m, "Cash", sale.Manager.Account.Id).With("note", "Handed over at the office"))).Payment();
        (await accountant.Client.CancelAsync(cash.Id(), "Counted twice")).EnsureSuccessStatusCode();

        var entry = (await accountant.Client.StatementAsync(sale.CustomerId)).GetProperty("entries").EnumerateArray().Single(e => e.Id() == cash.Id());

        Assert.True(entry.TryGetProperty("cancelReason", out var reason), $"statement entry without the cancel reason: {entry}");
        Assert.Equal("Counted twice", reason.GetString());
        Assert.True(entry.TryGetProperty("note", out var note) && note.GetString() == "Handed over at the office", $"statement entry without the note: {entry}");
        Assert.True(entry.TryGetProperty("collectedBy", out var collector) && collector.ValueKind == JsonValueKind.Object && collector.Id() == sale.Manager.Account.Id,
            $"statement entry without the Zone Manager who collected the cash: {entry}");
        Assert.True(entry.TryGetProperty("recordedBy", out var recorder) && recorder.ValueKind == JsonValueKind.Object && recorder.Id() == accountant.Account.Id,
            $"statement entry without who recorded it: {entry}");
    }
}
