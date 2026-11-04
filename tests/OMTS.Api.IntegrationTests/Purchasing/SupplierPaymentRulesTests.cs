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

    // PURCHASE-05.2: money paid back raises the debt and is never an advance.
    [Fact]
    public async Task Money_paid_back_raises_the_debt_without_the_advance_warning_and_is_filtered_by_kind()
    {
        var world = await factory.WorldAsync();

        var paidBack = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Kind = "PaidBack", Amount = 70_000 })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("PaidBack", 70_000m), (paidBack.GetProperty("kind").GetString(), paidBack.Money("debtAfter")));
        Assert.Equal(70_000m, await world.DebtAsync());
        await world.PayAsync(20_000);
        var search = await (await world.Owner.GetAsync($"/api/supplier-payments?supplierId={world.SupplierId}&kind=PaidBack")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, search.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal((0m, 70_000m), (search.Money("totalPaid"), search.Money("totalPaidBack")));
    }

    // PURCHASE-05.3: a reason, once, and it leaves the debt; a row version from an old screen is refused on SQL Server.
    [Fact]
    public async Task Cancelling_needs_a_reason_keeps_the_payment_with_it_and_leaves_the_debt_once_only()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 10, 100_000));
        var payment = await world.PayAsync(400_000);
        var paidBack = await world.PayAsync(30_000, kind: "PaidBack");
        Assert.Equal(630_000m, await world.DebtAsync());

        foreach (var reason in new[] { null, "  ", new string('r', 1001) })
        {
            Assert.Equal("Reason", await (await world.Owner.CancelPaymentAsync(payment.Id(), reason)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        var cancelled = await (await world.Owner.CancelPaymentAsync(payment.Id(), " Wrong amount ")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Cancelled", "Wrong amount", 1_030_000m), (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("cancelReason").GetString(), cancelled.Money("debtAfter")));
        Assert.NotEqual(JsonValueKind.Null, cancelled.GetProperty("cancelledAt").ValueKind);
        Assert.Equal(1_030_000m, await world.DebtAsync());

        // Cancelling money paid back lowers the debt again.
        var back = await (await world.Owner.CancelPaymentAsync(paidBack.Id(), "Not received")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1_000_000m, back.Money("debtAfter"));

        await (await world.Owner.CancelPaymentAsync(payment.Id(), "Again")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await world.Owner.CancelPaymentAsync(Guid.NewGuid(), "x")).ReadAsync(HttpStatusCode.NotFound);
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == payment.Id() && e.Action == "SupplierPayment.Cancelled"));
        Assert.Equal("Wrong amount", audit.Reason);
    }

    [Fact]
    public async Task A_repeated_deduplication_key_returns_the_first_payment_even_with_other_values_and_creates_and_notifies_nothing()
    {
        var world = await factory.WorldAsync();
        var key = $"pay-{Guid.NewGuid():N}";
        var first = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 200_000, ConfirmAdvance = true, DeduplicationKey = key })).ReadAsync(HttpStatusCode.Created);
        // Not even the confirmations are asked again.
        var again = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 900_000, DeduplicationKey = $" {key} " })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal((first.Id(), 200_000m, -200_000m), (again.Id(), again.Money("amount"), again.Money("debtAfter")));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "SupplierPayment.Recorded" && e.RecordId == first.Id())));
        var told = await factory.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == first.Id()).Select(n => n.RecipientAccountId).ToListAsync());
        Assert.NotEmpty(told);
        Assert.Equal(told.Distinct().Count(), told.Count);
    }

    [Fact]
    public async Task Payment_history_filters_by_supplier_kind_method_status_and_dates_with_paging_and_sums_of_what_counts()
    {
        var world = await factory.WorldAsync();
        await world.PayAsync(100_000);
        await world.PayAsync(200_000, Today.AddDays(-3));
        var cancelled = await world.PayAsync(300_000);
        await (await world.Owner.CancelPaymentAsync(cancelled.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);
        await world.PayAsync(40_000, kind: "PaidBack");

        async Task<JsonElement> Search(string query) => await (await world.Owner.GetAsync($"/api/supplier-payments?supplierId={world.SupplierId}&{query}")).ReadAsync(HttpStatusCode.OK);
        static (int Total, decimal Paid, decimal Back) Summary(JsonElement page) => (page.GetProperty("page").GetProperty("total").GetInt32(), page.Money("totalPaid"), page.Money("totalPaidBack"));

        Assert.Equal((4, 300_000m, 40_000m), Summary(await Search("")));
        Assert.Equal((3, 300_000m, 40_000m), Summary(await Search($"statusId={StatusIds.SupplierPayment.Recorded}")));
        Assert.Equal((1, 0m, 0m), Summary(await Search($"statusId={StatusIds.SupplierPayment.Cancelled}")));
        Assert.Equal((3, 300_000m, 0m), Summary(await Search("kind=Payment")));
        Assert.Equal((0, 0m, 0m), Summary(await Search("method=BankTransfer")));
        Assert.Equal((1, 200_000m, 0m), Summary(await Search($"from={Today.AddDays(-4):yyyy-MM-dd}&to={Today.AddDays(-2):yyyy-MM-dd}")));
        var paged = await Search("page=2&pageSize=3");
        Assert.Equal(1, paged.GetProperty("page").GetProperty("items").GetArrayLength());
        // Newest first; the person who recorded it is named.
        var first = (await Search("pageSize=1")).GetProperty("page").GetProperty("items")[0];
        Assert.Equal(Today.ToString("yyyy-MM-dd"), first.GetProperty("paymentDate").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("recordedBy").GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Payments_follow_the_accountant_role_in_any_zone_and_the_zone_manager_has_no_access()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 10, 100_000));
        var body = new PaymentBody { SupplierId = world.SupplierId, Amount = 1_000 };
        var (otherZone, _) = await factory.ZoneWithRaftAsync();
        var payment = await world.PayAsync(1_000);

        // PURCHASE-04/05 are No for the Zone Manager, even at All zones (§I.4.4).
        foreach (var refused in new[] { await factory.ZoneManagerAsync(world.Zone), await factory.PurchasingStaffAsync(null, StaffRole.ZoneManager) })
        {
            await (await refused.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await refused.GetAsync("/api/supplier-payments")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await refused.GetAsync($"/api/supplier-payments/{payment.Id()}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await refused.CancelPaymentAsync(payment.Id(), "x")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        // Payments have no zone: an Accountant pair for any zone opens them (BR-FARM-08, AC-01).
        foreach (var accountant in new[] { await factory.AccountantAsync(otherZone), await factory.AccountantAsync() })
        {
            await (await accountant.GetAsync("/api/supplier-payments")).ReadAsync(HttpStatusCode.OK);
            var recorded = await (await accountant.PostPaymentAsync(body)).ReadAsync(HttpStatusCode.Created);
            await (await accountant.CancelPaymentAsync(recorded.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);
        }

        // Read-only on PURCHASE-01…03 and PURCHASE-06.
        var viewer = await factory.AccountantAsync();
        await (await viewer.GetAsync($"/api/purchase-receipts/{receipt.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/purchase-receipts/export")).StatusCode);
        await (await viewer.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await viewer.CancelAsync(receipt.Id(), "x")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }
}
