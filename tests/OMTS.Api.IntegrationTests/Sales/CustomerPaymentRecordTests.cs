using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>DEBT-02 steps 1-7, DEBT-02.1, .3 and .5: payments and money paid back are Confirmed when saved and change the debt at once on every screen (BR-DEBT-02/03).</summary>
public class CustomerPaymentRecordTests
{
    private static async Task<string> NameAsync(AuthApiFactory api, Guid customerId) =>
        await api.QueryAsync(db => db.Customers.Where(c => c.Id == customerId).Select(c => c.Name).SingleAsync());

    [Fact]
    public async Task A_transfer_and_cash_handed_over_are_confirmed_lower_the_debt_on_every_screen_and_the_owner_is_told_each_time()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 40m);
        var accountant = await api.AllZonesAccountantAsync();
        var name = await NameAsync(api, sale.CustomerId);

        var transfer = await accountant.Client.PaidAsync(MoneyBody(sale.CustomerId, 500_000m) .With("note", "Bank statement 07/11"));
        var cash = await accountant.Client.PaidAsync(MoneyBody(sale.CustomerId, 200_000m, "Cash", sale.Manager.Account.Id));

        var paid = transfer.Payment();
        Assert.Equal((700_000m, "MSG-GEN-05", $"Payment {paid.Text("code")} saved."), (transfer.Money("debtAfter"), transfer.Text("messageCode"), transfer.Text("message")));
        Assert.Equal(("Confirmed", "Payment", "OtherTransfer", "Bank statement 07/11"), (paid.Text("status"), paid.Text("kind"), paid.Text("method"), paid.Text("note")));
        Assert.Equal(accountant.Account.Id, paid.GetProperty("recordedBy").Id());
        Assert.Equal(accountant.Account.Id, paid.GetProperty("confirmedBy").Id());
        Assert.Equal(JsonValueKind.Null, paid.GetProperty("collectedBy").ValueKind);
        Assert.Equal((500_000m, sale.Manager.Account.Id), (cash.Money("debtAfter"), cash.Payment().GetProperty("collectedBy").Id()));
        Assert.Equal(JsonValueKind.Null, cash.Payment().GetProperty("bankReference").ValueKind);

        Assert.Equal((500_000m, 500_000m, 500_000m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));
        var statement = await accountant.Client.StatementAsync(sale.CustomerId);
        var entries = statement.GetProperty("entries").EnumerateArray().Where(e => e.Text("kind") == "Payment").ToList();
        Assert.All(entries, e => Assert.False(e.GetProperty("awaitingConfirmation").GetBoolean()));
        Assert.Equal([500_000m], statement.Unpaid().Select(u => u.Unpaid));

        var notices = await api.TransactionNoticesAsync(sale.Owner.Account.Id);
        Assert.Equal(
            [
                $"{accountant.Account.DisplayName} recorded payment {paid.Text("code")} of 500,000 VND from {name}. {name} now owes the cooperative 700,000 VND.",
                $"{accountant.Account.DisplayName} recorded payment {cash.Payment().Text("code")} of 200,000 VND from {name}. {name} now owes the cooperative 500,000 VND.",
            ],
            notices.Select(n => n.Body));
        Assert.Equal(paid.Id(), notices[0].RecordId);
        var parameters = JsonDocument.Parse(notices[0].Params!).RootElement;
        Assert.Equal(("Payment", 500_000m, 700_000m), (parameters.Text("transaction_kind"), parameters.Money("amount"), parameters.Money("debt_amount")));
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == paid.Id() && e.Action == "CustomerPayment.Recorded")));
    }

    [Fact]
    public async Task A_payment_above_the_debt_asks_MSG_DEBT_04_and_a_bank_reference_already_recorded_asks_MSG_DEBT_05_before_anything_is_saved()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 10m);
        var accountant = (await api.AllZonesAccountantAsync()).Client;
        var body = MoneyBody(sale.CustomerId, 400_000m);

        var asked = await (await accountant.RecordAsync(body)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-DEBT-04", "confirmAboveDebt"), (asked.Text("code"), asked.Text("confirmationField")));
        Assert.Equal((300_000m, 100_000m), (asked.Money("debtAmount"), asked.Money("owedBackAmount")));
        Assert.Empty(await api.TransactionNoticesAsync(sale.Owner.Account.Id));

        var first = await accountant.PaidAsync(body.With("confirmAboveDebt", true));
        Assert.Equal(-100_000m, first.Money("debtAfter"));

        // The same bank reference on another payment: asked, then saved on Yes (DEBT-02.5), also when the debt is already below zero (MSG-DEBT-04 first).
        var again = MoneyBody(sale.CustomerId, 1_000m).With("bankReference", body["bankReference"]).With("confirmAboveDebt", true);
        var duplicate = await (await accountant.RecordAsync(again)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-DEBT-05", "confirmDuplicateBankReference"), (duplicate.Text("code"), duplicate.Text("confirmationField")));
        Assert.Equal((first.Payment().Text("code"), first.Payment().Id()), (duplicate.Text("paymentCode"), duplicate.GetProperty("paymentId").GetGuid()));
        await accountant.PaidAsync(again.With("confirmDuplicateBankReference", true));

        var list = await accountant.DebtListAsync();
        Assert.Equal((-101_000m, 101_000m), (list.RowOf(sale.CustomerId).Money("debt"), list.Money("owedBackTotal")));
        Assert.Equal(2, (await api.TransactionNoticesAsync(sale.Owner.Account.Id)).Count);
    }

    [Fact]
    public async Task Money_owed_back_is_paid_back_up_to_what_is_owed_and_raises_the_debt_towards_zero()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.PayLaterAsync(sale.CustomerId, 10m);
        var accountant = (await api.AllZonesAccountantAsync()).Client;
        await accountant.PaidAsync(MoneyBody(sale.CustomerId, 500_000m).With("confirmAboveDebt", true));

        var form = await (await accountant.GetFormAsync(sale.CustomerId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((-200_000m, 200_000m, 0), (form.Money("debt"), form.Money("owedBack"), form.GetProperty("unpaidInvoices").GetArrayLength()));
        var collectors = form.GetProperty("cashCollectors").EnumerateArray().Select(c => c.Id()).ToList();
        Assert.Contains(sale.Manager.Account.Id, collectors);
        Assert.Contains(sale.Owner.Account.Id, collectors);

        var tooMuch = await (await accountant.PayBackAsync(MoneyBody(sale.CustomerId, 200_001m, "Cash"))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-DEBT-06", "Amount", 200_000m), (tooMuch.Text("code"), tooMuch.Text("field"), tooMuch.Money("owedBackAmount")));

        var paidBack = await (await accountant.PayBackAsync(MoneyBody(sale.CustomerId, 150_000m, "Cash"))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal((-50_000m, "PaidBack", "Confirmed"), (paidBack.Money("debtAfter"), paidBack.Payment().Text("kind"), paidBack.Payment().Text("status")));
        Assert.Equal($"Money paid back {paidBack.Payment().Text("code")} saved.", paidBack.Text("message"));
        Assert.Equal((-50_000m, -50_000m, -50_000m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));
        var entry = (await accountant.StatementAsync(sale.CustomerId)).GetProperty("entries").EnumerateArray().Single(e => e.Text("kind") == "PaidBack");
        Assert.Equal((150_000m, -50_000m), (entry.Money("debtChange"), entry.Money("debtAfter")));
        Assert.EndsWith($"to {await NameAsync(api, sale.CustomerId)}. The cooperative now owes {await NameAsync(api, sale.CustomerId)} 50,000 VND.",
            (await api.TransactionNoticesAsync(sale.Owner.Account.Id))[^1].Body);

        // Nothing is owed back to a customer who owes the cooperative.
        var owing = (await sale.Owner.Client.CreateCustomerAsync()).Id();
        await sale.PayLaterAsync(owing, 1m);
        Assert.Equal(0m, (await (await accountant.PayBackAsync(MoneyBody(owing, 1m))).ReadAsync(HttpStatusCode.BadRequest)).Money("owedBackAmount"));
    }

    [Fact]
    public async Task Wrong_data_is_refused_a_merged_profile_too_and_a_repeated_key_records_one_payment()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var accountant = await api.AllZonesAccountantAsync();
        var client = accountant.Client;

        var empty = await (await client.RecordAsync(new { })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(["Customer", "Payment date", "Amount", "Method"], empty.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        foreach (var (body, field) in new (Dictionary<string, object?>, string)[]
        {
            (MoneyBody(sale.CustomerId, 1_000m, "Cash"), "Zone Manager"),
            (MoneyBody(sale.CustomerId, 1_000m, "Cash", accountant.Account.Id), "Zone Manager"),
            (MoneyBody(sale.CustomerId, 1_000m).With("bankReference", null), "Bank reference"),
            (MoneyBody(sale.CustomerId, 1_000m, date: PurchaseRequestTestKit.Today.AddDays(1)), "Payment date"),
            (MoneyBody(sale.CustomerId, 1_000m, "VietQrInvoice"), "Method"),
            (MoneyBody(Guid.NewGuid(), 1_000m), "Customer"),
        })
        {
            var refused = await (await client.RecordAsync(body)).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", field), (refused.Text("code"), refused.Text("field")));
        }

        // NFR-04: a second tap or a resend after a cut connection answers the first payment and records nothing.
        var keyed = MoneyBody(sale.CustomerId, 10_000m).With("confirmAboveDebt", true).With("deduplicationKey", "tap-" + AcsTestKit.NewCode());
        var first = await client.PaidAsync(keyed);
        var second = await client.PaidAsync(keyed);
        Assert.Equal(first.Payment().Id(), second.Payment().Id());
        Assert.Equal(-10_000m, second.Money("debtAfter"));
        Assert.Equal(1, await api.QueryAsync(db => db.CustomerPayments.CountAsync(p => p.CustomerId == sale.CustomerId)));
        Assert.Single(await api.TransactionNoticesAsync(sale.Owner.Account.Id));

        // SALE-04.3: a merged profile takes no payment; its rows now belong to the kept one.
        var trader = await api.TraderAsync();
        (await sale.Owner.Client.MergeAsync(trader.Profile.Id, sale.CustomerId, ownerApproved: true)).EnsureSuccessStatusCode();
        var merged = await (await client.RecordAsync(MoneyBody(sale.CustomerId, 1_000m).With("confirmAboveDebt", true))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", merged.Text("code"));
        Assert.Equal(StatusIds.CustomerPayment.Confirmed, await api.QueryAsync(db => db.CustomerPayments.Where(p => p.Id == first.Payment().Id()).Select(p => p.StatusId).SingleAsync()));
        Assert.Equal(-10_000m, await sale.Owner.Client.CustomerDebtAsync(trader.Profile.Id));
    }
}
