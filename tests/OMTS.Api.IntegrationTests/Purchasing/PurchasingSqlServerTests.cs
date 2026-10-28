using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// INP behaviour only a real SQL Server shows: unique indexes and check constraints, rowversion conflicts, parallel
/// saves (codes, completes, returns, payments, allocations, cancels) and query translation.
/// </summary>
public class PurchasingSqlServerTests
{
    private static async Task AssertLosersAsync(IEnumerable<HttpResponseMessage> losers, params string[] allowed)
    {
        foreach (var loser in losers)
        {
            Assert.Contains(await loser.CodeOrStatusAsync(), allowed);
        }
    }

    [SqlServerFact]
    public async Task Parallel_duplicate_supplier_phones_and_item_codes_create_one_record_and_answer_MSG_GEN_02()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var phone = NewPhone();
        var spellings = new[] { phone, $"+84{phone[1..]}", $"{phone[..4]} {phone[4..7]} {phone[7..]}" };

        var suppliers = await InParallelAsync(6, i => owner.PostAsJsonAsync("/api/suppliers", new { name = $"Supplier {i}", supplierType = "Seed", phoneNumber = spellings[i % 3] }));
        Assert.Single(suppliers, r => r.StatusCode == HttpStatusCode.Created);
        await AssertLosersAsync(suppliers.Where(r => r.StatusCode != HttpStatusCode.Created), "409 MSG-GEN-02");
        Assert.Equal(1, await host.QueryAsync(db => db.Suppliers.CountAsync(s => s.PhoneNumber == phone)));

        var code = $"Seed-{NewCode()}";
        var items = await InParallelAsync(6, i => owner.PostAsJsonAsync("/api/farming-input-items", new
        {
            code = i % 2 == 0 ? code : code.ToUpperInvariant(), name = "Hàu giống", group = "Seed", unit = "kg",
        }));
        Assert.Single(items, r => r.StatusCode == HttpStatusCode.Created);
        await AssertLosersAsync(items.Where(r => r.StatusCode != HttpStatusCode.Created), "409 MSG-GEN-02");
        Assert.Equal(1, await host.QueryAsync(db => db.FarmingInputItems.CountAsync(i => i.Code == code)));
    }

    // INP-03: a double Complete never creates the payment twice.
    [SqlServerFact]
    public async Task Parallel_completes_of_one_draft_complete_it_once_with_one_paid_now_payment()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 10, 100_000)) with { AmountPaidNow = 300_000, PaidNowMethod = "Cash" });

        var responses = await InParallelAsync(6, _ => world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.ReceiptId == draft.Id())));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == draft.Id() && e.Action == "PurchaseReceipt.StatusChanged")));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "SupplierPayment.Recorded" && e.ZoneId == world.Zone.Id)));
    }

    // INP-05: concurrent returns never exceed the returnable quantity (receipt row version; loser MSG06).
    [SqlServerFact]
    public async Task Parallel_returns_never_exceed_the_returnable_quantity()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Seed, 100, 10_000));
        var line = receipt.LineOf(world.Seed);

        var responses = await InParallelAsync(6, _ => world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody
        {
            DebtReductionAmount = 300_000, Lines = [new ReturnLineBody(line, 30)],
        }));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.InRange(created, 1, 3);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.Created), "409 MSG06", "400 MSG-PURCHASE-03");
        var returned = await host.QueryAsync(db => db.PurchaseReturnLines.Where(l => l.ReceiptLineId == line).SumAsync(l => l.Quantity));
        Assert.Equal(created * 30m, returned);
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal(1_000_000 - created * 300_000m, view.Money("outstanding"));
    }

    // INP-06: payments can never exceed the outstanding; a repeated key records one payment.
    [SqlServerFact]
    public async Task Parallel_payments_never_exceed_the_outstanding_and_a_repeated_key_records_one_payment()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var accountant = await host.StaffAsync(null, new Perm(PermissionFunctions.SupplierPayments, true, true));
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 10, 100_000));

        var responses = await InParallelAsync(6, _ => accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 400_000 }));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.InRange(created, 1, 2);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.Created), "409 MSG06", "400 MSG45");
        var paid = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.ReceiptId == receipt.Id() && p.StatusId == StatusIds.SupplierPayment.Recorded).SumAsync(p => p.Amount));
        Assert.Equal(created * 400_000m, paid);

        var other = await world.CompletedAsync(0, 0, new LineBody(world.Other, 10, 100_000));
        var key = $"retry-{Guid.NewGuid():N}";
        var keyed = await InParallelAsync(5, _ => accountant.PostPaymentAsync(new PaymentBody
        {
            SupplierId = world.SupplierId, ReceiptId = other.Id(), Amount = 100_000, DeduplicationKey = key,
        }));
        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.DeduplicationKey == key)));
        var ids = new HashSet<Guid>();
        foreach (var ok in keyed.Where(r => r.StatusCode == HttpStatusCode.Created))
        {
            ids.Add((await ok.ReadAsync(HttpStatusCode.Created)).Id());
        }

        Assert.Single(ids);
        await AssertLosersAsync(keyed.Where(r => r.StatusCode != HttpStatusCode.Created), "409 MSG06", "409 MSG-GEN-02");
        Assert.Equal(900_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{other.Id()}")).Money("outstanding"));
    }

    [SqlServerFact]
    public async Task Parallel_saves_on_one_day_get_distinct_receipt_seed_lot_and_return_codes()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();

        var creates = await InParallelAsync(5, _ => world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Seed, 10, 10_000), new LineBody(world.Other, 1, 1000)) with
        {
            AmountPaidNow = 1000, PaidNowMethod = "Cash", Complete = true,
        }));
        var receipts = new List<JsonElement>();
        foreach (var response in creates)
        {
            receipts.Add(await response.ReadAsync(HttpStatusCode.Created));
        }

        var codes = receipts.Select(r => r.GetProperty("code").GetString()!).ToList();
        Assert.Equal(5, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches($"^PR-{Today:yyyyMMdd}-\\d{{3}}$", c));
        var lots = await host.QueryAsync(db => db.SeedReceiptLots.Where(l => l.SupplierId == world.SupplierId).Select(l => l.InternalCode).ToListAsync());
        Assert.Equal(codes.Select(c => $"{c}-S1").Order(), lots.Order());
        Assert.Equal(5, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.SupplierId == world.SupplierId)));
        var audited = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "PurchaseReceipt.Created" && e.ZoneId == world.Zone.Id).Select(e => e.RecordCode).ToListAsync());
        Assert.Equal(codes.Order(), audited.Order());

        var returns = await InParallelAsync(5, i => world.Owner.PostReturnAsync(receipts[i].Id(), new ReturnBody
        {
            DebtReductionAmount = 10_000, Lines = [new ReturnLineBody(receipts[i].LineOf(world.Seed), 1)],
        }));
        foreach (var response in returns)
        {
            await response.ReadAsync(HttpStatusCode.Created);
        }

        var returnCodes = await host.QueryAsync(db => db.PurchaseReturns.Where(r => r.Receipt.SupplierId == world.SupplierId).Select(r => r.Code).ToListAsync());
        Assert.Equal(5, returnCodes.Distinct().Count());
        Assert.All(returnCodes, c => Assert.Matches($"^PRT-{Today:yyyyMMdd}-\\d{{3}}$", c));
    }

    [SqlServerFact]
    public async Task Parallel_allocations_of_one_detached_payment_allocate_it_once()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var cancelled = await world.CompletedAsync(0, 200_000, new LineBody(world.Other, 2, 100_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Entered twice", false)).ReadAsync(HttpStatusCode.OK);
        var paymentId = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.SupplierId == world.SupplierId).Select(p => p.Id).SingleAsync());
        var targets = new[]
        {
            await world.CompletedAsync(0, 0, new LineBody(world.Other, 5, 100_000)),
            await world.CompletedAsync(0, 0, new LineBody(world.Other, 5, 100_000)),
        };

        var responses = await InParallelAsync(6, i => world.Owner.PostAsJsonAsync($"/api/supplier-payments/{paymentId}/allocate", new { receiptId = targets[i % 2].Id() }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
        var allocatedTo = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.Id == paymentId).Select(p => p.ReceiptId).SingleAsync());
        Assert.Contains(allocatedTo!.Value, targets.Select(t => t.Id()));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == paymentId && e.Action == "SupplierPayment.Allocated")));
        Assert.Equal(800_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals").Money("outstanding"));
    }

    // INP-04: cancel is blocked by a return; a payment cannot land on a cancelled receipt – also when both run at once.
    [SqlServerFact]
    public async Task Parallel_cancel_and_return_or_payment_never_leave_a_return_or_recorded_payment_on_a_cancelled_receipt()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        for (var round = 0; round < 3; round++)
        {
            var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Seed, 10, 10_000));
            var returnTask = world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { DebtReductionAmount = 10_000, Lines = [new ReturnLineBody(receipt.LineOf(world.Seed), 1)] });
            var cancelTask = world.Owner.CancelAsync(receipt.Id(), "Wrong", true);
            var paymentReceipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));
            var paymentTask = world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = paymentReceipt.Id(), Amount = 100_000 });
            var paymentCancelTask = world.Owner.CancelAsync(paymentReceipt.Id(), "Wrong", true);
            await Task.WhenAll(returnTask, cancelTask, paymentTask, paymentCancelTask);

            var state = await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == receipt.Id()).Select(r => new { r.StatusId, Returns = r.Returns.Count }).SingleAsync());
            Assert.False(state.StatusId == StatusIds.PurchaseReceipt.Cancelled && state.Returns > 0, $"round {round}: cancelled with a return");
            Assert.NotEqual(HttpStatusCode.InternalServerError, (await returnTask).StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, (await cancelTask).StatusCode);

            var paid = await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == paymentReceipt.Id())
                .Select(r => new { r.StatusId, Recorded = r.Payments.Count(p => p.StatusId == StatusIds.SupplierPayment.Recorded) }).SingleAsync());
            Assert.False(paid.StatusId == StatusIds.PurchaseReceipt.Cancelled && paid.Recorded > 0, $"round {round}: cancelled with a recorded payment");
            Assert.NotEqual(HttpStatusCode.InternalServerError, (await paymentTask).StatusCode);
        }
    }

    [SqlServerFact]
    public async Task Stale_row_versions_on_supplier_item_draft_and_cancel_answer_MSG06_and_keep_the_first_save()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;

        var supplier = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}");
        var supplierVersion = supplier.GetProperty("rowVersion").GetString();
        Assert.False(string.IsNullOrEmpty(supplierVersion));
        object SupplierBody(string name) => new { name, supplierType = "Seed", phoneNumber = supplier.GetProperty("phoneNumber").GetString(), rowVersion = supplierVersion };
        await (await owner.PutAsJsonAsync($"/api/suppliers/{world.SupplierId}", SupplierBody("First save"))).ReadAsync(HttpStatusCode.OK);
        var staleSupplier = await (await owner.PutAsJsonAsync($"/api/suppliers/{world.SupplierId}", SupplierBody("Late save"))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.True(staleSupplier.TryGetProperty("currentRowVersion", out _));
        Assert.Equal("First save", await host.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.Name).SingleAsync()));

        var item = await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{world.Other}");
        object ItemBody(string name) => new { code = item.GetProperty("code").GetString(), name, group = "Other", unit = "bag", rowVersion = item.GetProperty("rowVersion").GetString() };
        await (await owner.PutAsJsonAsync($"/api/farming-input-items/{world.Other}", ItemBody("First save"))).ReadAsync(HttpStatusCode.OK);
        await (await owner.PutAsJsonAsync($"/api/farming-input-items/{world.Other}", ItemBody("Late save"))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal("First save", await host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == world.Other).Select(i => i.Name).SingleAsync()));

        // A draft without lines can be edited on SQL Server (lines cannot: see PurchasingSqlServerBugTests.pending.cs.txt).
        var draft = await owner.CreateReceiptAsync(world.Body());
        var draftVersion = draft.GetProperty("rowVersion").GetString();
        await (await owner.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body() with { Notes = "First save", RowVersion = draftVersion })).ReadAsync(HttpStatusCode.OK);
        await (await owner.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body() with { Notes = "Late save", RowVersion = draftVersion })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var staleCancel = await owner.PostAsJsonAsync($"/api/purchase-receipts/{draft.Id()}/cancel", new { reason = "Late", rowVersion = draftVersion });
        await staleCancel.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var kept = await host.QueryAsync(db => db.PurchaseReceipts.SingleAsync(r => r.Id == draft.Id()));
        Assert.Equal(("First save", StatusIds.PurchaseReceipt.Draft), (kept.Notes, kept.StatusId));
    }

    [SqlServerFact]
    public async Task Check_constraints_and_unique_indexes_hold_in_the_database()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(10_000, 100_000, new LineBody(world.Seed, 10, 100_000));
        var returned = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody
        {
            RefundAmount = 99_000, RefundMethod = "Cash", Lines = [new ReturnLineBody(receipt.LineOf(world.Seed), 1)],
        })).ReadAsync(HttpStatusCode.Created);
        var returnId = returned.GetProperty("returns")[0].Id();
        var paymentId = receipt.GetProperty("payments")[0].Id();
        var lineId = receipt.LineOf(world.Seed);

        foreach (var sql in new[]
        {
            $"UPDATE SupplierPayments SET Amount = 100500 WHERE Id = '{paymentId}'",
            $"UPDATE PurchaseReceipts SET DiscountAmount = 10500 WHERE Id = '{receipt.Id()}'",
            $"UPDATE PurchaseReceipts SET AmountPaidNow = 100001 WHERE Id = '{receipt.Id()}'",
            $"UPDATE PurchaseReceiptLines SET LineAmount = 999999 WHERE Id = '{lineId}'",
            $"UPDATE PurchaseReturns SET RefundAmount = 98500, DebtReductionAmount = 500 WHERE Id = '{returnId}'",
            $"UPDATE PurchaseReturnLines SET ReturnValue = 99500 WHERE ReturnId = '{returnId}'",
        })
        {
            await using var db = host.Database.CreateContext();
            var failure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(547, failure.Number);
        }

        // Deduplication keys are unique when set; payments without a key are not limited.
        var second = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 1000 })).ReadAsync(HttpStatusCode.Created);
        var third = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 1000, DeduplicationKey = "k-1" })).ReadAsync(HttpStatusCode.Created);
        await AssertDuplicateAsync(host, $"UPDATE SupplierPayments SET DeduplicationKey = 'k-1' WHERE Id = '{second.Id()}'");
        Assert.NotEqual(second.Id(), third.Id());

        // One seed lot per seed line; supplier phones and item codes are unique.
        var lot = await host.QueryAsync(db => db.SeedReceiptLots.AsNoTracking().SingleAsync(l => l.PurchaseReceiptLineId == lineId));
        await AssertDuplicateAsync(host, db => db.SeedReceiptLots.Add(new SeedReceiptLot
        {
            InternalCode = $"{lot.InternalCode}-X", SeedTypeId = lot.SeedTypeId, SupplierId = lot.SupplierId, PurchaseReceiptLineId = lineId, Quantity = 1, Unit = "kg",
            ReceivedAt = lot.ReceivedAt,
        }));
        var otherSupplier = await world.Owner.CreateSupplierAsync();
        var phone = await host.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.PhoneNumber).SingleAsync());
        await AssertDuplicateAsync(host, $"UPDATE Suppliers SET PhoneNumber = '{phone}' WHERE Id = '{otherSupplier}'");
        var seedCode = await host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == world.Seed).Select(i => i.Code).SingleAsync());
        await AssertDuplicateAsync(host, $"UPDATE FarmingInputItems SET Code = '{seedCode.ToLowerInvariant()}' WHERE Id = '{world.Other}'");
    }

    [SqlServerFact]
    public async Task Longest_allowed_values_fit_the_columns()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;

        // A name of 101–200 characters fails (audit RecordCode is 100): see PurchasingSqlServerBugTests.pending.cs.txt.
        var supplier = await (await owner.PostAsJsonAsync("/api/suppliers", new
        {
            name = new string('Ư', 100), supplierType = "Material", phoneNumber = NewPhone(), otherContacts = new string('ộ', 500),
        })).ReadAsync(HttpStatusCode.Created);
        var item = await (await owner.PostAsJsonAsync("/api/farming-input-items", new
        {
            code = NewCode() + new string('Đ', 40), name = new string('ố', 200), group = "Seed", unit = new string('ụ', 30), description = new string('ề', 2000),
        })).ReadAsync(HttpStatusCode.Created);
        var receipt = await owner.CreateReceiptAsync(new ReceiptBody
        {
            SupplierId = supplier.Id(), ZoneId = world.Zone.Id, Notes = new string('ả', 1000), AmountPaidNow = 1000, PaidNowMethod = "Other", Complete = true,
            Lines = [new LineBody(item.Id(), 999_999.999m, 1000, new string('ừ', 100), new string('ỡ', 500))],
        });
        Assert.Equal(1_000_000_000, receipt.Money("subtotal"));
        var lot = await host.QueryAsync(db => db.SeedReceiptLots.SingleAsync(l => l.SupplierId == supplier.Id()));
        Assert.Equal((999_999.999m, new string('ụ', 30), 100, 500), (lot.Quantity, lot.Unit, lot.SupplierLotCode!.Length, lot.ActualSource!.Length));

        await (await owner.PostReturnAsync(receipt.Id(), new ReturnBody
        {
            Reason = new string('ắ', 1000), DebtReductionAmount = 1000, Lines = [new ReturnLineBody(receipt.LineOf(item.Id()), 1)],
        })).ReadAsync(HttpStatusCode.Created);
        await (await owner.PostPaymentAsync(new PaymentBody
        {
            SupplierId = supplier.Id(), ReceiptId = receipt.Id(), Amount = 1000, ReferenceCode = new string('R', 100), DeduplicationKey = new string('K', 100),
        })).ReadAsync(HttpStatusCode.Created);
        var copy = await (await owner.PostAsync($"/api/purchase-receipts/{receipt.Id()}/copy", null)).ReadAsync(HttpStatusCode.Created);
        await (await owner.CancelAsync(copy.Id(), new string('ỷ', 1000), null)).ReadAsync(HttpStatusCode.OK);

        var saved = await host.QueryAsync(db => db.Suppliers.SingleAsync(s => s.Id == supplier.Id()));
        Assert.Equal((new string('Ư', 100), new string('ộ', 500)), (saved.Name, saved.OtherContacts));
        Assert.Equal(new string('ỷ', 1000), await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == copy.Id()).Select(r => r.CancelReason).SingleAsync()));
    }

    [SqlServerFact]
    public async Task Searches_ignore_case_and_every_list_filter_and_debt_query_runs_on_sql_server()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;
        var tag = NewCode();
        var named = await owner.CreateSupplierAsync(name: $"Hau Giong Cat Ba {tag}");
        var foundSuppliers = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers?search=HAU GIONG CAT BA {tag.ToUpperInvariant()}");
        Assert.Equal([named], foundSuppliers.GetProperty("items").EnumerateArray().Select(s => s.Id()));
        var seedCode = await host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == world.Seed).Select(i => i.Code).SingleAsync());
        var foundItems = await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items?search={seedCode.ToUpperInvariant()}");
        Assert.Equal([world.Seed], foundItems.EnumerateArray().Select(i => i.Id()));

        var seed = await world.CompletedAsync(100_000, 200_000, new LineBody(world.Seed, 100, 10_000), new LineBody(world.Material, 10, 10_000));
        await (await owner.PostReturnAsync(seed.Id(), new ReturnBody { DebtReductionAmount = 9000, Lines = [new ReturnLineBody(seed.LineOf(world.Material), 1)] })).ReadAsync(HttpStatusCode.Created);
        var draft = await owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)));
        var code = seed.GetProperty("code").GetString()!;

        var list = await (await owner.GetAsync(
            $"/api/purchase-receipts?itemGroup=RaftMaterial&itemId={world.Material}&zoneId={world.Zone.Id}&raftId={world.Raft.Id}&supplierId={world.SupplierId}" +
            $"&statusId={StatusIds.PurchaseReceipt.Completed}&from={Today.AddDays(-1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}&code={code[3..].ToLowerInvariant()}&page=1&pageSize=5"))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal([seed.Id()], list.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Id()));
        // 1,100,000 − 100,000 = 1,000,000 payable; floats 100,000 less 1/11 of the discount → 91,000 less one float returned (9,000).
        Assert.Equal((1_000_000m, 9_000m, 791_000m), (list.GetProperty("totals").Money("payable"), list.GetProperty("totals").Money("returned"), list.GetProperty("totals").Money("outstanding")));
        var costs = await (await owner.GetAsync($"/api/purchase-receipts/raft-material-costs?zoneId={world.Zone.Id}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(82_000, costs.EnumerateArray().Single().Money("materialCost"));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/purchase-receipts/export?supplierId={world.SupplierId}")).StatusCode);

        var debts = await (await owner.GetAsync("/api/suppliers/debts")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(791_000, debts.EnumerateArray().Single(r => r.GetProperty("supplierId").GetGuid() == world.SupplierId).Money("outstanding"));
        var payments = await (await owner.GetAsync($"/api/supplier-payments?supplierId={world.SupplierId}&zoneId={world.Zone.Id}&method=Cash&statusId={StatusIds.SupplierPayment.Recorded}&from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(200_000, payments.Money("totalAmount"));
        Assert.Equal(StatusIds.PurchaseReceipt.Draft, (await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{draft.Id()}")).GetProperty("statusId").GetInt32());
    }

    private static async Task AssertDuplicateAsync(FarmingHost host, string sql)
    {
        await using var db = host.Database.CreateContext();
        var failure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Contains(failure.Number, new[] { 2601, 2627 });
    }

    private static async Task AssertDuplicateAsync(FarmingHost host, Action<OMTS.Infrastructure.Persistence.OmtsDbContext> change)
    {
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(change));
        Assert.Contains(((SqlException)failure.InnerException!).Number, new[] { 2601, 2627 });
    }
}
