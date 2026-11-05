using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>PURCHASE-05 / BR-PURCHASE-08: fields, codes, the two confirmations, money paid back, cancellation, history and access.</summary>
public class SupplierPaymentRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Missing_or_invalid_fields_answer_MSG_GEN_01_with_the_field_and_save_nothing()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 10, 100_000));
        var valid = new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000 };

        foreach (var (body, field) in new (PaymentBody, string)[]
        {
            (valid with { SupplierId = null }, "Supplier"),
            (valid with { SupplierId = Guid.NewGuid() }, "Supplier"),
            (valid with { Kind = null }, "Kind"),
            (valid with { Amount = null }, "Amount"),
            (valid with { Amount = 0 }, "Amount"),
            (valid with { Amount = -1000 }, "Amount"),
            (valid with { Amount = 100.5m }, "Amount"),
            (valid with { Amount = 1_000_000_000_000_000_000m }, "Amount"),
            (valid with { PaymentDate = null }, "Payment date"),
            (valid with { PaymentDate = Today.AddDays(1) }, "Payment date"),
            (valid with { Method = null }, "Method"),
            (valid with { Method = "Other" }, "Method"),
            (valid with { Method = "BankTransfer" }, "Bank reference"),
            (valid with { Method = "BankTransfer", BankReference = "  " }, "Bank reference"),
            (valid with { Method = "BankTransfer", BankReference = new string('r', 101) }, "Bank reference"),
            (valid with { Note = new string('n', 1001) }, "Note"),
            (valid with { DeduplicationKey = new string('k', 101) }, "Deduplication key"),
        })
        {
            Assert.Equal(field, await (await world.Owner.PostPaymentAsync(body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        Assert.Equal(0, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.SupplierId == world.SupplierId)));
    }

    [Fact]
    public async Task A_payment_gets_a_TT_code_keeps_its_dongs_lowers_the_debt_and_is_audited()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 10, 100_000));

        var payment = await (await world.Owner.PostPaymentAsync(new PaymentBody
        {
            SupplierId = world.SupplierId, Amount = 123_457, PaymentDate = Today.AddDays(-1), Method = "BankTransfer", BankReference = $" FT-{Guid.NewGuid():N} ", Note = " Đợt 1 ",
        })).ReadAsync(HttpStatusCode.Created);

        Assert.Matches("^TT\\d{6,}$", payment.GetProperty("code").GetString());
        Assert.Equal(("Payment", 123_457m, "Recorded", "Đợt 1"), (payment.GetProperty("kind").GetString(), payment.Money("amount"), payment.GetProperty("status").GetString(), payment.GetProperty("note").GetString()));
        Assert.DoesNotContain(" ", payment.GetProperty("bankReference").GetString());
        Assert.Equal(1_000_000m - 123_457m, payment.Money("debtAfter"));
        Assert.Equal(1_000_000m - 123_457m, await world.DebtAsync());
        Assert.False(payment.TryGetProperty("receiptId", out _));

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == payment.Id()));
        Assert.Equal(("SupplierPayment.Recorded", (Guid?)null, payment.GetProperty("code").GetString()), (audit.Action, audit.ZoneId, audit.RecordCode));
        var read = await (await world.Owner.GetAsync($"/api/supplier-payments/{payment.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("debtAfter").ValueKind);
        await (await world.Owner.GetAsync($"/api/supplier-payments/{Guid.NewGuid()}")).ReadAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_cash_payment_keeps_no_bank_reference()
    {
        var world = await factory.WorldAsync();
        var payment = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 10, BankReference = "FT-1", ConfirmAdvance = true })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("bankReference").ValueKind);
    }

    // PURCHASE-05.1: only above the debt, or with a debt of zero or below; the 409 carries what the screen shows.
    [Fact]
    public async Task A_payment_above_the_debt_answers_MSG_PURCHASE_05_until_it_is_confirmed_and_exactly_the_debt_needs_no_confirmation()
    {
        var world = await factory.WorldAsync();
        var name = await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.Name).SingleAsync());
        var body = new PaymentBody { SupplierId = world.SupplierId, Amount = 150_000 };

        // Debt zero: the whole amount is paid in advance.
        var noDebt = await (await world.Owner.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-05");
        Assert.Equal(($"The cooperative owes {name} 0 VND. After this payment, it will have paid 150,000 VND in advance. Do you want to save the payment?", 0m, 150_000m, "confirmAdvance"),
            (noDebt.GetProperty("detail").GetString(), noDebt.Money("debtAmount"), noDebt.Money("advanceAmount"), noDebt.GetProperty("confirmFlag").GetString()));

        await world.CompletedAsync(new LineBody(world.Other, 1, 100_000));
        var above = await (await world.Owner.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-05");
        Assert.Equal((100_000m, 50_000m), (above.Money("debtAmount"), above.Money("advanceAmount")));
        Assert.Equal(0, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.SupplierId == world.SupplierId)));

        var exact = await (await world.Owner.PostPaymentAsync(body with { Amount = 100_000 })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(0m, exact.Money("debtAfter"));

        // Below zero after a confirmed advance, a further one shows the exact debt in the details, and 0 owed in the text.
        var advance = await (await world.Owner.PostPaymentAsync(body with { ConfirmAdvance = true })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(-150_000m, advance.Money("debtAfter"));
        var further = await (await world.Owner.PostPaymentAsync(body with { Amount = 50_000 })).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-05");
        Assert.Equal((-150_000m, 200_000m), (further.Money("debtAmount"), further.Money("advanceAmount")));
        Assert.StartsWith($"The cooperative owes {name} 0 VND. After this payment, it will have paid 200,000 VND in advance.", further.GetProperty("detail").GetString());
    }

    // PURCHASE-05.4, also on money paid back and on another supplier; a cancelled payment no longer counts.
    [Fact]
    public async Task A_bank_reference_already_recorded_answers_MSG_PURCHASE_06_until_confirmed_and_a_cancelled_one_no_longer_counts()
    {
        var world = await factory.WorldAsync();
        var reference = $"FT-{Guid.NewGuid():N}";
        var body = new PaymentBody { SupplierId = world.SupplierId, Amount = 50_000, Method = "BankTransfer", BankReference = reference, ConfirmAdvance = true };
        var first = await (await world.Owner.PostPaymentAsync(body with { PaymentDate = Today.AddDays(-2) })).ReadAsync(HttpStatusCode.Created);
        var code = first.GetProperty("code").GetString();

        var problem = await (await world.Owner.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-06");
        Assert.Equal($"Payment {code} of 50,000 VND on {Today.AddDays(-2):dd/MM/yyyy} already has bank reference {reference}. Do you want to save this payment as well?",
            problem.GetProperty("detail").GetString());
        Assert.Equal((first.Id(), code, 50_000m, reference, "confirmDuplicateBankReference"),
            (problem.GetProperty("paymentId").GetGuid(), problem.GetProperty("paymentCode").GetString(), problem.Money("amount"), problem.GetProperty("bankReference").GetString(), problem.GetProperty("confirmFlag").GetString()));

        var otherSupplier = await world.Owner.CreateSupplierAsync();
        await (await world.Owner.PostPaymentAsync(body with { SupplierId = otherSupplier })).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-06");
        await (await world.Owner.PostPaymentAsync(body with { Kind = "PaidBack" })).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-06");

        var second = await (await world.Owner.PostPaymentAsync(body with { ConfirmDuplicateBankReference = true })).ReadAsync(HttpStatusCode.Created);
        foreach (var id in new[] { first.Id(), second.Id() })
        {
            await (await world.Owner.CancelPaymentAsync(id, "Mistake")).ReadAsync(HttpStatusCode.OK);
        }

        await (await world.Owner.PostPaymentAsync(body)).ReadAsync(HttpStatusCode.Created);
    }
}
