using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// Purchasing behaviour only a real SQL Server shows: unique indexes and check constraints, rowversion conflicts, parallel
/// saves (PN codes, completes, returns, payments, allocations, cancels) and query translation.
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
    public async Task Parallel_duplicate_supplier_phones_and_item_codes_create_one_record_and_answer_MSG_GEN_02_with_the_existing_id()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var phone = NewPhone();
        var spellings = new[] { phone, $"+84{phone[1..]}", $"{phone[..4]} {phone[4..7]} {phone[7..]}" };

        var suppliers = await InParallelAsync(6, i => owner.PostAsJsonAsync("/api/suppliers", new { name = $"Supplier {i}", supplierType = "Seed", phoneNumber = spellings[i % 3] }));
        Assert.Single(suppliers, r => r.StatusCode == HttpStatusCode.Created);
        var id = await host.QueryAsync(db => db.Suppliers.Where(s => s.PhoneNumber == phone).Select(s => s.Id).SingleAsync());
        foreach (var refused in suppliers.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(id, (await refused.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
        }

        var code = $"Seed-{NewCode()}";
        var items = await InParallelAsync(6, i => owner.PostAsJsonAsync("/api/farming-input-items", new
        {
            code = i % 2 == 0 ? code : code.ToUpperInvariant(), name = "Hàu giống", group = "Seed", unit = "kg",
        }));
        Assert.Single(items, r => r.StatusCode == HttpStatusCode.Created);
        await AssertLosersAsync(items.Where(r => r.StatusCode != HttpStatusCode.Created), "409 MSG-GEN-02");
        Assert.Equal(1, await host.QueryAsync(db => db.FarmingInputItems.CountAsync(i => i.Code == code)));
    }

    // GBR-04 code allocation race: parallel first saves take the next PN numbers, one each, and the seed lots follow.
    [SqlServerFact]
    public async Task Parallel_creates_take_distinct_consecutive_PN_codes_and_their_seed_lot_codes_follow()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var manager = await host.ZoneManagerAsync(world.Zone.Id);
        var first = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)));

        var creates = await InParallelAsync(8, i => (i % 2 == 0 ? world.Owner : manager).PostReceiptAsync(
            world.Body(new LineBody(world.Seed, 10, 10_000), new LineBody(world.Other, 1, 1000)) with { Complete = i % 3 != 0 }));
        var receipts = new List<JsonElement>();
        foreach (var response in creates)
        {
            receipts.Add(await response.ReadAsync(HttpStatusCode.Created));
        }

        var codes = receipts.Select(r => r.GetProperty("code").GetString()!).ToList();
        var numbers = codes.Select(c => int.Parse(c[2..])).Order().ToList();
        var start = int.Parse(first.GetProperty("code").GetString()![2..]);
        Assert.Equal(Enumerable.Range(start + 1, 8), numbers);
        Assert.All(receipts.Where(r => r.GetProperty("status").GetString() == "Completed"),
            r => Assert.Equal($"{r.GetProperty("code").GetString()}-1", r.SeedLotCodeOf(world.Seed)));
        var audited = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "PurchaseReceipt.Created" && e.RecordId != first.Id()).Select(e => e.RecordCode).ToListAsync());
        Assert.Equal(codes.Order(), audited.Order());
        // The notification of each completed receipt names its final code.
        var completed = receipts.Where(r => r.GetProperty("status").GetString() == "Completed").ToList();
        var notified = await host.QueryAsync(db => db.Notifications.Where(n => n.Type == NotificationTypes.PurchaseReceiptCompleted).Select(n => n.RelatedRecordCode).ToListAsync());
        Assert.Equal(completed.Select(r => r.GetProperty("code").GetString()).Order(), notified.Distinct().Order());

        var returns = await InParallelAsync(completed.Count, i => world.Owner.PostReturnAsync(completed[i].Id(), new ReturnBody
        {
            DebtReductionAmount = 10_000, Lines = [new ReturnLineBody(completed[i].LineOf(world.Seed), 1)],
        }));
        foreach (var response in returns)
        {
            await response.ReadAsync(HttpStatusCode.Created);
        }

        var returnCodes = await host.QueryAsync(db => db.PurchaseReturns.Where(r => r.Receipt.SupplierId == world.SupplierId).Select(r => r.Code).ToListAsync());
        Assert.Equal(completed.Count, returnCodes.Distinct().Count());
    }

    // PURCHASE-01: a double Complete completes once and notifies once.
    [SqlServerFact]
    public async Task Parallel_completes_of_one_draft_complete_it_once_with_one_notification_per_owner()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 10, 100_000), new LineBody(world.Seed, 5, 100_000)));

        var responses = await InParallelAsync(6, _ => world.Owner.CompleteAsync(draft.Id()));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == draft.Id() && e.Action == "PurchaseReceipt.StatusChanged")));
        var owners = await host.QueryAsync(db => db.Accounts.CountAsync(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active));
        Assert.Equal(owners, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == draft.Id())));
    }

    // A Draft edit replaces its lines (the unique line numbers are freed by the deletes first).
    [SqlServerFact]
    public async Task Draft_lines_are_replaced_and_incomplete_lines_saved()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000), new LineBody(null, null, null)));

        var edited = await (await world.Owner.EditAsync(draft, world.Body(
            new LineBody(world.Seed, 10, 20_000, RaftId: world.Raft.Id), new LineBody(world.Other, 2, null), new LineBody(world.Material, 1.5m, 333)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([1, 2, 3], edited.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lineNumber").GetInt32()));
        Assert.Equal(200_500m, edited.Money("total"));
        // A lines-only edit bumps the receipt version: an editor still holding the first one gets MSG06.
        await (await world.Owner.EditAsync(draft, world.Body(new LineBody(world.Other, 5, 1000)))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal(3, await host.QueryAsync(db => db.PurchaseReceiptLines.CountAsync(l => l.ReceiptId == draft.Id())));
    }

    // GBR-04: a seeding event points to the receipt line; the line can then no longer be removed, nor the receipt cancelled.
    [SqlServerFact]
    public async Task A_seeding_event_points_to_the_seed_line_and_blocks_the_cancellation()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 10_000));
        var line = receipt.LineOf(world.Seed);
        var cycle = new Domain.Entities.Cultivation.CultivationCycle { RaftId = world.Raft.Id, OpenedAt = DateTimeOffset.UtcNow };
        await host.UseDbAsync(db =>
        {
            db.CultivationCycles.Add(cycle);
            db.SeedingEvents.Add(new Domain.Entities.Cultivation.SeedingEvent
            {
                CycleId = cycle.Id, SeedLotId = line, SeedingDate = Today, SeedQuantity = 40, RecordedByAccountId = host.Owner.Account.Id,
            });
        });

        var problem = await (await world.Owner.CancelAsync(receipt.Id(), "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");
        Assert.Equal($"{receipt.GetProperty("code").GetString()}-1", problem.GetProperty("causeCode").GetString());
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal((40m, 60m), (view.LineViewOf(world.Seed).Money("seeded"), view.LineViewOf(world.Seed).Money("returnable")));
        await using var db = host.Database.CreateContext();
        var failure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync($"DELETE FROM PurchaseReceiptLines WHERE Id = {line}"));
        Assert.Equal(547, failure.Number);
    }

    // INP-05: concurrent returns never exceed the returnable quantity (receipt row version; loser MSG06).
    [SqlServerFact]
    public async Task Parallel_returns_never_exceed_the_returnable_quantity()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 10_000));
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
        Assert.Equal(created * 300_000m, view.Money("returned"));
    }

    // Payments can never exceed the outstanding of their receipt; a repeated key records one payment.
    [SqlServerFact]
    public async Task Parallel_payments_never_exceed_the_outstanding_and_a_repeated_key_records_one_payment()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var accountant = await host.StaffAsync(null, StaffRole.Accountant);
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 100_000));

        var responses = await InParallelAsync(6, _ => accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 400_000 }));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.InRange(created, 1, 2);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.Created), "409 MSG06", "400 MSG45");
        var paid = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.ReceiptId == receipt.Id() && p.StatusId == StatusIds.SupplierPayment.Recorded).SumAsync(p => p.Amount));
        Assert.Equal(created * 400_000m, paid);

        var other = await world.CompletedAsync(new LineBody(world.Other, 10, 100_000));
        var key = $"retry-{Guid.NewGuid():N}";
        var keyed = await InParallelAsync(5, _ => accountant.PostPaymentAsync(new PaymentBody
        {
            SupplierId = world.SupplierId, ReceiptId = other.Id(), Amount = 100_000, DeduplicationKey = key,
        }));
        var ids = new HashSet<Guid>();
        foreach (var response in keyed)
        {
            ids.Add((await response.ReadAsync(HttpStatusCode.Created)).Id());
        }

        Assert.Single(ids);
        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.DeduplicationKey == key)));
    }

    [SqlServerFact]
    public async Task Parallel_allocations_of_one_payment_kept_by_a_cancellation_allocate_it_once()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var cancelled = await world.PaidAsync(200_000, new LineBody(world.Other, 2, 100_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Entered twice")).ReadAsync(HttpStatusCode.OK);
        var paymentId = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.SupplierId == world.SupplierId).Select(p => p.Id).SingleAsync());
        var targets = new[] { await world.CompletedAsync(new LineBody(world.Other, 5, 100_000)), await world.CompletedAsync(new LineBody(world.Other, 5, 100_000)) };

        var responses = await InParallelAsync(6, i => world.Owner.PostAsJsonAsync($"/api/supplier-payments/{paymentId}/allocate", new { receiptId = targets[i % 2].Id() }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
        var allocatedTo = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.Id == paymentId).Select(p => p.ReceiptId).SingleAsync());
        Assert.Contains(allocatedTo!.Value, targets.Select(t => t.Id()));
        Assert.Equal(800_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals").Money("outstanding"));
    }

    // A cancel never leaves a return on a cancelled receipt, nor a recorded payment still allocated to it.
    [SqlServerFact]
    public async Task Parallel_cancel_and_return_or_payment_never_leave_a_return_or_an_allocated_payment_on_a_cancelled_receipt()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        for (var round = 0; round < 3; round++)
        {
            var receipt = await world.CompletedAsync(new LineBody(world.Seed, 10, 10_000));
            var returnTask = world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { DebtReductionAmount = 10_000, Lines = [new ReturnLineBody(receipt.LineOf(world.Seed), 1)] });
            var cancelTask = world.Owner.CancelAsync(receipt.Id(), "Wrong");
            var paymentReceipt = await world.CompletedAsync(new LineBody(world.Other, 1, 100_000));
            var paymentTask = world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = paymentReceipt.Id(), Amount = 100_000 });
            var paymentCancelTask = world.Owner.CancelAsync(paymentReceipt.Id(), "Wrong");
            await Task.WhenAll(returnTask, cancelTask, paymentTask, paymentCancelTask);

            var state = await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == receipt.Id()).Select(r => new { r.StatusId, Returns = r.Returns.Count }).SingleAsync());
            Assert.False(state.StatusId == StatusIds.PurchaseReceipt.Cancelled && state.Returns > 0, $"round {round}: cancelled with a return");
            var paid = await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == paymentReceipt.Id())
                .Select(r => new { r.StatusId, Allocated = r.Payments.Count(p => p.StatusId == StatusIds.SupplierPayment.Recorded) }).SingleAsync());
            Assert.False(paid.StatusId == StatusIds.PurchaseReceipt.Cancelled && paid.Allocated > 0, $"round {round}: cancelled with an allocated payment");
            foreach (var task in new[] { returnTask, cancelTask, paymentTask, paymentCancelTask })
            {
                Assert.NotEqual(HttpStatusCode.InternalServerError, (await task).StatusCode);
            }
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
        object SupplierBody(string name) => new { name, supplierType = "Seed", phoneNumber = supplier.GetProperty("phoneNumber").GetString(), rowVersion = supplierVersion };
        await (await owner.PutAsJsonAsync($"/api/suppliers/{world.SupplierId}", SupplierBody("First save"))).ReadAsync(HttpStatusCode.OK);
        Assert.True((await (await owner.PutAsJsonAsync($"/api/suppliers/{world.SupplierId}", SupplierBody("Late save"))).ProblemAsync(HttpStatusCode.Conflict, "MSG06"))
            .TryGetProperty("currentRowVersion", out _));

        var item = await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{world.Other}");
        object ItemBody(string name) => new { code = item.GetProperty("code").GetString(), name, group = "Other", unit = "bag", rowVersion = item.GetProperty("rowVersion").GetString() };
        await (await owner.PutAsJsonAsync($"/api/farming-input-items/{world.Other}", ItemBody("First save"))).ReadAsync(HttpStatusCode.OK);
        await (await owner.PutAsJsonAsync($"/api/farming-input-items/{world.Other}", ItemBody("Late save"))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        var draft = await owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)));
        await (await owner.EditAsync(draft, world.Body(new LineBody(world.Other, 2, 1000)) with { Notes = "First save" })).ReadAsync(HttpStatusCode.OK);
        await (await owner.EditAsync(draft, world.Body(new LineBody(world.Other, 3, 1000)) with { Notes = "Late save" })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var completed = await world.CompletedAsync(new LineBody(world.Other, 1, 1000));
        await (await owner.PostAsJsonAsync($"/api/purchase-receipts/{completed.Id()}/cancel", new { reason = "Late", rowVersion = Convert.ToBase64String([0, 0, 0, 0, 0, 0, 0, 1]) }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        var kept = await host.QueryAsync(db => db.PurchaseReceipts.Include(r => r.Lines).SingleAsync(r => r.Id == draft.Id()));
        Assert.Equal(("First save", 2m), (kept.Notes, kept.Lines.Single().Quantity!.Value));
        Assert.Equal(StatusIds.PurchaseReceipt.Completed, await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == completed.Id()).Select(r => r.StatusId).SingleAsync()));
    }

    [SqlServerFact]
    public async Task Check_constraints_and_unique_indexes_hold_and_line_amounts_are_no_longer_thousands()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.PaidAsync(100_000, new LineBody(world.Seed, 10, 100_000), new LineBody(world.Other, 1.5m, 333));
        var returned = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody
        {
            RefundAmount = 100_000, RefundMethod = "Cash", Lines = [new ReturnLineBody(receipt.LineOf(world.Seed), 1)],
        })).ReadAsync(HttpStatusCode.Created);
        var returnId = returned.GetProperty("returns")[0].Id();
        var paymentId = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.ReceiptId == receipt.Id()).Select(p => p.Id).SingleAsync());
        var lineId = receipt.LineOf(world.Seed);

        foreach (var sql in new[]
        {
            $"UPDATE SupplierPayments SET Amount = 100500 WHERE Id = '{paymentId}'",
            $"UPDATE PurchaseReturns SET RefundAmount = 98500, DebtReductionAmount = 500 WHERE Id = '{returnId}'",
            $"UPDATE PurchaseReturnLines SET ReturnValue = 99500 WHERE ReturnId = '{returnId}'",
        })
        {
            await using var db = host.Database.CreateContext();
            var failure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(547, failure.Number);
        }

        // BR-PURCHASE-02: line amounts keep their dongs; line numbers are unique on a receipt (the seed lot code).
        Assert.Equal(500m, await host.QueryAsync(db => db.PurchaseReceiptLines.Where(l => l.ReceiptId == receipt.Id() && l.LineNumber == 2).Select(l => l.LineAmount).SingleAsync()));
        await AssertDuplicateAsync(host, $"UPDATE PurchaseReceiptLines SET LineNumber = 2 WHERE Id = '{lineId}'");
        var second = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 1000 })).ReadAsync(HttpStatusCode.Created);
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 1000, DeduplicationKey = "k-1" })).ReadAsync(HttpStatusCode.Created);
        await AssertDuplicateAsync(host, $"UPDATE SupplierPayments SET DeduplicationKey = 'k-1' WHERE Id = '{second.Id()}'");
        var otherSupplier = await world.Owner.CreateSupplierAsync();
        var phone = await host.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.PhoneNumber).SingleAsync());
        await AssertDuplicateAsync(host, $"UPDATE Suppliers SET PhoneNumber = '{phone}' WHERE Id = '{otherSupplier}'");
        var seedCode = await host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == world.Seed).Select(i => i.Code).SingleAsync());
        await AssertDuplicateAsync(host, $"UPDATE FarmingInputItems SET Code = '{seedCode.ToLowerInvariant()}' WHERE Id = '{world.Other}'");
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)));
        await AssertDuplicateAsync(host, $"UPDATE PurchaseReceipts SET Code = '{receipt.GetProperty("code").GetString()}' WHERE Id = '{draft.Id()}'");
    }

    [SqlServerFact]
    public async Task Longest_allowed_values_fit_the_columns()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;

        var supplier = await (await owner.PostAsJsonAsync("/api/suppliers", new { name = new string('Ư', 200), supplierType = "Material", phoneNumber = NewPhone(), address = new string('ộ', 500) }))
            .ReadAsync(HttpStatusCode.Created);
        var item = await (await owner.PostAsJsonAsync("/api/farming-input-items", new
        {
            code = NewCode() + new string('Đ', 40), name = new string('ố', 200), group = "Other", unit = new string('ụ', 30), description = new string('ề', 2000),
        })).ReadAsync(HttpStatusCode.Created);
        var receipt = await owner.CreateReceiptAsync(new ReceiptBody
        {
            SupplierId = supplier.Id(), Notes = new string('ả', 1000), Complete = true,
            Lines = [new LineBody(item.Id(), 999_999.999m, 1000), new LineBody(world.Seed, 1, 1, new string('ừ', 100), world.Raft.Id)],
        });
        Assert.Equal(1_000_000_000m, receipt.Money("total"));

        await (await owner.PostReturnAsync(receipt.Id(), new ReturnBody
        {
            Reason = new string('ắ', 1000), DebtReductionAmount = 1000, Lines = [new ReturnLineBody(receipt.LineOf(item.Id()), 1)],
        })).ReadAsync(HttpStatusCode.Created);
        var other = await owner.CreateReceiptAsync(new ReceiptBody { SupplierId = supplier.Id(), Complete = true, Lines = [new LineBody(item.Id(), 1, 1000)] });
        await (await owner.CancelAsync(other.Id(), new string('ỷ', 1000))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(new string('ỷ', 1000), await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == other.Id()).Select(r => r.CancelReason).SingleAsync()));
        Assert.Equal(2, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == supplier.Id() || (e.RecordId == other.Id() && e.Action == "PurchaseReceipt.Cancelled"))));
    }

    [SqlServerFact]
    public async Task Every_list_filter_the_totals_the_raft_material_cost_the_export_and_the_debt_queries_run_on_sql_server()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;
        var tag = NewCode();
        var named = await owner.CreateSupplierAsync(name: $"Hau Giong Cat Ba {tag}");
        var foundSuppliers = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers?search=HAU GIONG CAT BA {tag.ToUpperInvariant()}");
        Assert.Equal([named], foundSuppliers.GetProperty("items").EnumerateArray().Select(s => s.Id()));

        var receipt = await world.PaidAsync(200_000,
            new LineBody(world.Seed, 100, 10_000, RaftId: world.Raft.Id), world.MaterialForRaft(10, 10_000), new LineBody(world.Other, 1.5m, 333));
        await (await owner.PostReturnAsync(receipt.Id(), new ReturnBody { DebtReductionAmount = 10_000, Lines = [new ReturnLineBody(receipt.LineOf(world.Material), 1)] }))
            .ReadAsync(HttpStatusCode.Created);
        var draft = await owner.CreateReceiptAsync(world.Body(new LineBody(null, 1, 1000)));
        var code = receipt.GetProperty("code").GetString()!;
        var period = $"from={Today.AddDays(-1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";
        foreach (var filter in new[]
        {
            "itemGroup=Seed", "itemGroup=Material", $"itemId={world.Seed}", $"raftId={world.Raft.Id}", $"supplierId={world.SupplierId}",
            $"statusId={StatusIds.PurchaseReceipt.Completed}", $"statusId={StatusIds.PurchaseReceipt.Draft}", period, $"code={code[2..]}", "from=2000-01-01&to=2005-01-01",
        })
        {
            var list = await owner.GetAsync($"/api/purchase-receipts?{filter}&pageSize=5");
            Assert.True(list.StatusCode == HttpStatusCode.OK, $"{filter}: {await list.Content.ReadAsStringAsync()}");
            Assert.True((await owner.GetAsync($"/api/purchase-receipts/export?{filter}")).StatusCode == HttpStatusCode.OK, $"export {filter}");
            Assert.True((await owner.GetAsync($"/api/purchase-receipts/raft-material-costs?{filter}")).StatusCode == HttpStatusCode.OK, $"costs {filter}");
        }

        var all = await (await owner.GetAsync(
            $"/api/purchase-receipts?itemGroup=Material&itemId={world.Material}&raftId={world.Raft.Id}&supplierId={world.SupplierId}" +
            $"&statusId={StatusIds.PurchaseReceipt.Completed}&{period}&code={code[2..]}&page=1&pageSize=5"))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal([receipt.Id()], all.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Id()));
        var totals = all.GetProperty("totals");
        Assert.Equal((100_000m, 10_000m, 90_000m, 90_000m), (totals.Money("receiptsTotal"), totals.Money("returnsTotal"), totals.Money("netTotal"), totals.Money("raftMaterialCost")));
        var row = all.GetProperty("page").GetProperty("items")[0];
        Assert.Equal((1_100_500m, 100_000m), (row.Money("total"), row.Money("linesTotal")));
        var costs = await (await owner.GetAsync($"/api/purchase-receipts/raft-material-costs?{period}&raftId={world.Raft.Id}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(90_000m, costs.Money("materialCost"));
        Assert.Equal(90_000m, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));

        // 1,100,500 − 10,000 returned − 200,000 paid.
        var debts = await (await owner.GetAsync("/api/suppliers/debts")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(890_500m, debts.EnumerateArray().Single(r => r.GetProperty("supplierId").GetGuid() == world.SupplierId).Money("outstanding"));
        var payments = await (await owner.GetAsync($"/api/supplier-payments?supplierId={world.SupplierId}&method=Cash&statusId={StatusIds.SupplierPayment.Recorded}&{period}"))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(200_000m, payments.Money("totalAmount"));
        Assert.Equal(StatusIds.PurchaseReceipt.Draft, (await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{draft.Id()}")).GetProperty("statusId").GetInt32());
    }

    private static async Task AssertDuplicateAsync(FarmingHost host, string sql)
    {
        await using var db = host.Database.CreateContext();
        var failure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Contains(failure.Number, new[] { 2601, 2627 });
    }
}
