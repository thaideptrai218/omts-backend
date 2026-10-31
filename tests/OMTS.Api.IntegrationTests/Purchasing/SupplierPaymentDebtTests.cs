using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-06: payment rules, allocation of detached payments, history, supplier debt in every INP-04/05 scenario, grants.</summary>
public class SupplierPaymentDebtTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Bad_amount_method_date_receipt_or_supplier_return_MSG01_and_non_thousands_MSG41()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 10, 100_000));
        var valid = new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 100_000 };

        foreach (var (body, field) in new (PaymentBody, string)[]
        {
            (valid with { Amount = null }, "Amount"),
            (valid with { Amount = 0 }, "Amount"),
            (valid with { Amount = -1000 }, "Amount"),
            (valid with { Method = null }, "Method"),
            (valid with { PaymentDate = null }, "Payment date"),
            (valid with { PaymentDate = Today.AddDays(1) }, "Payment date"),
            (valid with { ReceiptId = null }, "Receipt"),
            (valid with { ReceiptId = Guid.NewGuid() }, "Receipt"),
            (valid with { SupplierId = null }, "Supplier"),
            (valid with { ReferenceCode = new string('r', 101) }, "Reference code"),
            (valid with { DeduplicationKey = new string('k', 101) }, "Deduplication key"),
        })
        {
            Assert.Equal(field, await (await world.Owner.PostPaymentAsync(body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        Assert.Equal("Amount", await (await world.Owner.PostPaymentAsync(valid with { Amount = 100_500 })).FieldAsync(HttpStatusCode.BadRequest, "MSG41"));
        Assert.Equal(0, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.SupplierId == world.SupplierId)));
    }

    [Fact]
    public async Task A_payment_never_exceeds_the_receipt_outstanding_and_only_completed_receipts_of_the_supplier_are_paid()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 400_000, new LineBody(world.Other, 10, 100_000));
        var exact = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 600_000, ReferenceCode = "FT-123" }))
            .ReadAsync(HttpStatusCode.Created);
        Assert.Equal(("FT-123", "Recorded", receipt.GetProperty("code").GetString()), (exact.GetProperty("referenceCode").GetString(), exact.GetProperty("status").GetString(), exact.GetProperty("receiptCode").GetString()));

        var over = await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 1000 });
        var problem = await over.ProblemAsync(HttpStatusCode.BadRequest, "MSG45");
        Assert.Equal(0, problem.Money("limit"));

        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));
        var cancelled = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Wrong", null)).ReadAsync(HttpStatusCode.OK);
        foreach (var id in new[] { draft.Id(), cancelled.Id() })
        {
            await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = id, Amount = 1000 })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        }

        var otherSupplier = await world.Owner.CreateSupplierAsync();
        var foreign = await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = otherSupplier, ReceiptId = receipt.Id(), Amount = 1000 });
        Assert.Equal("Receipt", await foreign.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == exact.Id()));
        Assert.Equal(("SupplierPayment.Recorded", world.Zone.Id), (audit.Action, audit.ZoneId));
    }

    [Fact]
    public async Task A_repeated_deduplication_key_returns_the_first_payment_even_with_other_values_and_creates_nothing()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 10, 100_000));
        var key = $"pay-{Guid.NewGuid():N}";
        var first = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 200_000, DeduplicationKey = key }))
            .ReadAsync(HttpStatusCode.Created);
        var again = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 900_000, DeduplicationKey = $" {key} " }))
            .ReadAsync(HttpStatusCode.Created);

        Assert.Equal((first.Id(), 200_000m), (again.Id(), again.Money("amount")));
        Assert.Equal(1, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.ReceiptId == receipt.Id())));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "SupplierPayment.Recorded" && e.RecordId == first.Id())));

        // Payments without a key are never merged.
        var noKey = new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 100_000 };
        await (await world.Owner.PostPaymentAsync(noKey)).ReadAsync(HttpStatusCode.Created);
        await (await world.Owner.PostPaymentAsync(noKey)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(3, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.ReceiptId == receipt.Id())));
    }

    [Fact]
    public async Task A_detached_payment_is_allocated_once_to_a_completed_receipt_of_the_same_supplier_within_its_outstanding()
    {
        var world = await factory.WorldAsync();
        var cancelled = await world.CompletedAsync(0, 300_000, new LineBody(world.Other, 10, 100_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Entered twice", false)).ReadAsync(HttpStatusCode.OK);
        var paymentId = await factory.QueryAsync(db => db.SupplierPayments.Where(p => p.SupplierId == world.SupplierId).Select(p => p.Id).SingleAsync());

        var small = await world.CompletedAsync(0, 0, new LineBody(world.Other, 2, 100_000));
        var big = await world.CompletedAsync(0, 0, new LineBody(world.Other, 5, 100_000));
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 5, 100_000)));
        var otherSupplier = await world.Owner.CreateSupplierAsync();
        var foreign = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 5, 100_000)) with { SupplierId = otherSupplier, Complete = true });

        async Task<HttpResponseMessage> Allocate(Guid? receiptId) =>
            await world.Owner.PostAsJsonAsync($"/api/supplier-payments/{paymentId}/allocate", new { receiptId });

        Assert.Equal(200_000, (await (await Allocate(small.Id())).ProblemAsync(HttpStatusCode.BadRequest, "MSG45")).Money("limit"));
        await (await Allocate(draft.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await Allocate(cancelled.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal("Receipt", await (await Allocate(foreign.Id())).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Receipt", await (await Allocate(null)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        // Completed receipts owe 200,000 + 500,000; the detached 300,000 is held by the supplier.
        Assert.Equal(700_000 - 300_000, await DebtAsync(world));

        var allocated = await (await Allocate(big.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((big.Id(), big.GetProperty("code").GetString()), (allocated.GetProperty("receiptId").GetGuid(), allocated.GetProperty("receiptCode").GetString()));
        Assert.Equal(200_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{big.Id()}")).Money("outstanding"));
        Assert.Equal(400_000, await DebtAsync(world));
        await (await Allocate(small.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == paymentId && e.Action == "SupplierPayment.Allocated"));
        Assert.Equal(world.Zone.Id, audit.ZoneId);
        Assert.Contains(big.Id().ToString(), audit.After);
        await (await world.Owner.PostAsJsonAsync($"/api/supplier-payments/{Guid.NewGuid()}/allocate", new { receiptId = big.Id() })).ReadAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_cancelled_payment_cannot_be_allocated()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 100_000, new LineBody(world.Other, 1, 100_000));
        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong", true)).ReadAsync(HttpStatusCode.OK);
        var paymentId = await factory.QueryAsync(db => db.SupplierPayments.Where(p => p.SupplierId == world.SupplierId).Select(p => p.Id).SingleAsync());
        var target = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));
        await (await world.Owner.PostAsJsonAsync($"/api/supplier-payments/{paymentId}/allocate", new { receiptId = target.Id() })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    // Done when: supplier debt equals the formula in every INP-04/05 scenario.
    [Fact]
    public async Task Supplier_debt_follows_payments_returns_cancellations_and_allocations()
    {
        var world = await factory.WorldAsync();
        var a = await world.CompletedAsync(0, 200_000, new LineBody(world.Other, 10, 100_000));      // payable 1,000,000, outstanding 800,000
        var b = await world.CompletedAsync(100_000, 0, new LineBody(world.Seed, 100, 10_000));      // payable 900,000, outstanding 900,000
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 5, 100_000)));    // a Draft never counts
        Assert.Equal(1_700_000, await DebtAsync(world));

        // Return 10 kg of B (90,000) as a debt reduction.
        await (await world.Owner.PostReturnAsync(b.Id(), new ReturnBody { DebtReductionAmount = 90_000, Lines = [new ReturnLineBody(b.LineOf(world.Seed), 10)] }))
            .ReadAsync(HttpStatusCode.Created);
        Assert.Equal(1_610_000, await DebtAsync(world));

        // Pay 300,000 on A, then return 2 bags of A (200,000) as a refund: the outstanding of A stays 500,000.
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = a.Id(), Amount = 300_000 })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(1_310_000, await DebtAsync(world));
        await (await world.Owner.PostReturnAsync(a.Id(), new ReturnBody { RefundAmount = 200_000, RefundMethod = "BankTransfer", Lines = [new ReturnLineBody(a.LineOf(world.Other), 2)] }))
            .ReadAsync(HttpStatusCode.Created);
        Assert.Equal(1_310_000, await DebtAsync(world));

        // D fully paid now, cancelled keeping the payment: the 400,000 becomes unallocated and lowers the debt.
        var d = await world.CompletedAsync(0, 400_000, new LineBody(world.Other, 1, 400_000));
        Assert.Equal(1_310_000, await DebtAsync(world));
        await (await world.Owner.CancelAsync(d.Id(), "Duplicate", false)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(910_000, await DebtAsync(world));

        // Allocating it to B moves it from unallocated to paid: the debt does not change.
        var detached = await factory.QueryAsync(db => db.SupplierPayments.Where(p => p.SupplierId == world.SupplierId && p.ReceiptId == null).Select(p => p.Id).SingleAsync());
        await (await world.Owner.PostAsJsonAsync($"/api/supplier-payments/{detached}/allocate", new { receiptId = b.Id() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(910_000, await DebtAsync(world));

        // E cancelled with its payment cancelled leaves no trace in the debt.
        var e = await world.CompletedAsync(0, 100_000, new LineBody(world.Other, 3, 100_000));
        Assert.Equal(1_110_000, await DebtAsync(world));
        await (await world.Owner.CancelAsync(e.Id(), "Wrong supplier", true)).ReadAsync(HttpStatusCode.OK);

        var detail = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt");
        var totals = detail.GetProperty("totals");
        Assert.Equal(
            (1_900_000m, 290_000m, 900_000m, 200_000m, 0m, 910_000m),
            (totals.Money("payable"), totals.Money("returned"), totals.Money("paid"), totals.Money("refunded"), totals.Money("unallocated"), totals.Money("outstanding")));
        var rows = detail.GetProperty("receipts").EnumerateArray().ToDictionary(r => r.GetProperty("receiptId").GetGuid(), r => r.Money("outstanding"));
        Assert.Equal(new Dictionary<Guid, decimal> { [a.Id()] = 500_000, [b.Id()] = 410_000 }, rows);

        var list = await world.Owner.GetFromJsonAsync<JsonElement>("/api/suppliers/debts");
        var row = list.EnumerateArray().Single(r => r.GetProperty("supplierId").GetGuid() == world.SupplierId);
        Assert.Equal(910_000, row.Money("outstanding"));
        Assert.Equal(910_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}")).Money("debt"));
    }

    [Fact]
    public async Task A_supplier_with_only_an_unallocated_payment_is_in_the_debt_list_with_a_negative_debt()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 250_000, new LineBody(world.Other, 1, 250_000));
        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong", false)).ReadAsync(HttpStatusCode.OK);

        var list = await world.Owner.GetFromJsonAsync<JsonElement>("/api/suppliers/debts");
        var row = list.EnumerateArray().Single(r => r.GetProperty("supplierId").GetGuid() == world.SupplierId);
        Assert.Equal((250_000m, -250_000m), (row.Money("unallocated"), row.Money("outstanding")));
        Assert.StartsWith("Supplier ", row.GetProperty("supplierName").GetString());
    }

    [Fact]
    public async Task Payment_history_filters_by_supplier_receipt_zone_method_status_and_dates_with_paging_and_total()
    {
        var world = await factory.WorldAsync();
        var a = await world.CompletedAsync(0, 100_000, new LineBody(world.Other, 10, 100_000));
        var (otherZone, _) = await factory.ZoneWithRaftAsync();
        var b = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 10, 100_000)) with { ZoneId = otherZone.Id, Complete = true });
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = a.Id(), Amount = 200_000, PaymentDate = Today.AddDays(-3) })).ReadAsync(HttpStatusCode.Created);
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = b.Id(), Amount = 300_000, Method = "Other" })).ReadAsync(HttpStatusCode.Created);

        async Task<JsonElement> Search(string query) => await (await world.Owner.GetAsync($"/api/supplier-payments?supplierId={world.SupplierId}&{query}")).ReadAsync(HttpStatusCode.OK);
        static (int Total, decimal Amount) Summary(JsonElement page) => (page.GetProperty("page").GetProperty("total").GetInt32(), page.Money("totalAmount"));

        Assert.Equal((3, 600_000m), Summary(await Search("")));
        Assert.Equal((2, 300_000m), Summary(await Search($"receiptId={a.Id()}")));
        Assert.Equal((1, 300_000m), Summary(await Search($"zoneId={otherZone.Id}")));
        Assert.Equal((1, 300_000m), Summary(await Search("method=Other")));
        Assert.Equal((1, 200_000m), Summary(await Search($"from={Today.AddDays(-4):yyyy-MM-dd}&to={Today.AddDays(-2):yyyy-MM-dd}")));
        Assert.Equal((3, 600_000m), Summary(await Search($"statusId={StatusIds.SupplierPayment.Recorded}")));
        var paged = await Search("page=2&pageSize=2");
        Assert.Equal((3, 1), (paged.GetProperty("page").GetProperty("total").GetInt32(), paged.GetProperty("page").GetProperty("items").GetArrayLength()));
        // Newest first.
        var first = (await Search("pageSize=1")).GetProperty("page").GetProperty("items")[0];
        Assert.Equal(Today.ToString("yyyy-MM-dd"), first.GetProperty("paymentDate").GetString());
    }

    [Fact]
    public async Task Supplier_payments_need_an_all_zones_grant_and_the_accountant_only_reads_receipts()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Seed, 10, 100_000));
        var body = new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 100_000 };

        var zoneOnly = await factory.PurchasingStaffAsync(world.Zone, new Perm(PermissionFunctions.SupplierPayments, true, true));
        var manager = await factory.ZoneManagerAsync(world.Zone);
        foreach (var refused in new[] { zoneOnly, manager })
        {
            await (await refused.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await refused.GetAsync("/api/supplier-payments")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await refused.GetAsync($"/api/suppliers/{world.SupplierId}/debt")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        var viewOnly = await factory.PurchasingStaffAsync(null, new Perm(PermissionFunctions.SupplierPayments));
        await (await viewOnly.GetAsync("/api/supplier-payments")).ReadAsync(HttpStatusCode.OK);
        await (await viewOnly.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        var accountant = await factory.AccountantAsync();
        await (await accountant.PostPaymentAsync(body)).ReadAsync(HttpStatusCode.Created);
        await (await accountant.GetAsync("/api/suppliers/debts")).ReadAsync(HttpStatusCode.OK);
        await (await accountant.GetAsync($"/api/purchase-receipts/{receipt.Id()}")).ReadAsync(HttpStatusCode.OK);
        await (await accountant.GetAsync("/api/purchase-receipts")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/api/purchase-receipts/export")).StatusCode);
        await (await accountant.GetAsync("/api/farming-input-items")).ReadAsync(HttpStatusCode.OK);

        // Read-only on INP-03…INP-05.
        await (await accountant.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.CancelAsync(receipt.Id(), "x", true)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.PostReturnAsync(receipt.Id(), new ReturnBody { DebtReductionAmount = 100_000, Lines = [new ReturnLineBody(receipt.LineOf(world.Seed), 1)] }))
            .ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.PostAsync($"/api/purchase-receipts/{receipt.Id()}/copy", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    private async Task<decimal> DebtAsync(PurchasingWorld world) =>
        (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals").Money("outstanding");
}
