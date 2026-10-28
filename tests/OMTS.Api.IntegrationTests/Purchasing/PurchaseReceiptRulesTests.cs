using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-03 rules and every "Errors and edge cases" bullet of creating, editing and completing a purchase receipt.</summary>
public class PurchaseReceiptRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_draft_needs_only_supplier_and_zone_and_counts_nowhere_until_completed()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(new ReceiptBody
        {
            SupplierId = world.SupplierId, ZoneId = world.Zone.Id, ReceiptDate = null, AmountPaidNow = 500_000, PaidNowMethod = "Cash",
            Lines = [new LineBody(world.Seed, null, null)],
        });

        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Matches($"^PR-{Today:yyyyMMdd}-\\d{{3}}$", draft.GetProperty("code").GetString());
        Assert.Equal(0, await factory.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.SupplierId == world.SupplierId)));
        Assert.Equal(0, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.SupplierId == world.SupplierId)));
        Assert.Equal(0, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals").Money("outstanding"));

        // Completing validates everything that the draft could leave out.
        var complete = await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null);
        var problem = await complete.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Receipt date", problem.GetProperty("field").GetString());
        Assert.Equal("Draft", (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{draft.Id()}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Missing_supplier_zone_date_or_lines_and_unknown_items_return_MSG01_naming_the_field()
    {
        var world = await factory.WorldAsync();
        var line = new LineBody(world.Other, 1, 10_000);
        foreach (var (body, field) in new (ReceiptBody, string)[]
        {
            (world.Body(line) with { SupplierId = null }, "Supplier"),
            (world.Body(line) with { SupplierId = Guid.NewGuid() }, "Supplier"),
            (world.Body(line) with { ZoneId = null }, "Zone"),
            (world.Body(line) with { ZoneId = Guid.NewGuid() }, "Zone"),
            (world.Body(new LineBody(Guid.NewGuid(), 1, 10_000)), "Line 1 item"),
            (world.Body(line, new LineBody(null, 1, 10_000)), "Line 2 item"),
            (world.Body(line) with { ReceiptDate = null, Complete = true }, "Receipt date"),
            (world.Body(line) with { ReceiptDate = Today.AddDays(1), Complete = true }, "Receipt date"),
            (world.Body() with { Complete = true }, "Lines"),
            (world.Body(line) with { AmountPaidNow = 10_000, Complete = true }, "Paid now method"),
            (world.Body(line) with { Notes = new string('n', 1001) }, "Notes"),
            (world.Body(new LineBody(world.Seed, 1, 10_000, SupplierLotCode: new string('s', 101))), "Line 1 supplier lot code"),
            (world.Body(new LineBody(world.Seed, 1, 10_000, ActualSource: new string('s', 501))), "Line 1 actual source"),
            (world.Body(line) with { DiscountAmount = -1000 }, "Discount amount"),
            (world.Body(line) with { AmountPaidNow = -1000 }, "Amount paid now"),
        })
        {
            var response = await world.Owner.PostReceiptAsync(body);
            Assert.Equal(field, await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync(r => r.SupplierId == world.SupplierId)));
    }

    [Theory]
    [InlineData(0, 10_000)]
    [InlineData(-2, 10_000)]
    [InlineData(1, -1)]
    [InlineData(1, 1500.5)]
    public async Task Quantity_not_above_zero_or_a_negative_or_fractional_unit_price_return_MSG59_naming_the_line(decimal quantity, decimal unitPrice)
    {
        var world = await factory.WorldAsync();
        var response = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000), new LineBody(world.Other, quantity, unitPrice)) with { Complete = true });
        Assert.Equal("Line 2", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task A_zero_unit_price_is_allowed()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 5, 0), new LineBody(world.Other, 1, 100_000));
        Assert.Equal(100_000, receipt.Money("subtotal"));
    }

    [Theory]
    [InlineData(1500, 0, "Discount amount")]
    [InlineData(0, 2500, "Amount paid now")]
    public async Task Discount_or_paid_now_not_a_multiple_of_1000_returns_MSG41(decimal discount, decimal paidNow, string field)
    {
        var world = await factory.WorldAsync();
        var response = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 10, 10_000)) with
        {
            DiscountAmount = discount, AmountPaidNow = paidNow, PaidNowMethod = "Cash", Complete = true,
        });
        Assert.Equal(field, await response.FieldAsync(HttpStatusCode.BadRequest, "MSG41"));
    }

    [Fact]
    public async Task Discount_above_subtotal_or_paid_now_above_payable_return_MSG45_with_the_limit()
    {
        var world = await factory.WorldAsync();
        var line = new LineBody(world.Other, 10, 10_000);
        var discount = await world.Owner.PostReceiptAsync(world.Body(line) with { DiscountAmount = 101_000, Complete = true });
        var discountProblem = await discount.ProblemAsync(HttpStatusCode.BadRequest, "MSG45");
        Assert.Equal(("Discount amount", 100_000m), (discountProblem.GetProperty("field").GetString(), discountProblem.Money("limit")));

        var paid = await world.Owner.PostReceiptAsync(world.Body(line) with { DiscountAmount = 20_000, AmountPaidNow = 81_000, PaidNowMethod = "Cash", Complete = true });
        var paidProblem = await paid.ProblemAsync(HttpStatusCode.BadRequest, "MSG45");
        Assert.Equal(("Amount paid now", 80_000m), (paidProblem.GetProperty("field").GetString(), paidProblem.Money("limit")));

        // Exactly at the limits is accepted: everything discounted, or everything paid now.
        var full = await world.CompletedAsync(20_000, 80_000, line);
        Assert.Equal(0, full.Money("outstanding"));
        var free = await world.CompletedAsync(100_000, 0, line);
        Assert.Equal(0, free.Money("payable"));
    }

    [Fact]
    public async Task Raft_material_needs_an_in_use_raft_of_the_receipt_zone_and_zone_supplier_item_must_be_in_use()
    {
        var world = await factory.WorldAsync();
        var (otherZone, otherRaft) = await factory.ZoneWithRaftAsync();
        var stoppedRaft = new Raft { ZoneId = world.Zone.Id, Code = NewCode(), Size = "20 m x 10 m", StatusId = StatusIds.Usage.Discontinued };
        await factory.UseDbAsync(db => db.Rafts.Add(stoppedRaft));
        var material = new LineBody(world.Material, 10, 50_000);

        foreach (var (raftId, field) in new (Guid?, string)[] { (null, "Raft"), (otherRaft.Id, "Raft"), (stoppedRaft.Id, "Raft") })
        {
            var response = await world.Owner.PostReceiptAsync(world.Body(material) with { RaftId = raftId, Complete = true });
            Assert.Equal(field, await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        // A raft is optional for other goods but must still be valid when given.
        var seedWithForeignRaft = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Seed, 1, 1000)) with { RaftId = otherRaft.Id, Complete = true });
        Assert.Equal("Raft", await seedWithForeignRaft.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await world.CompletedAsync(0, 0, new LineBody(world.Seed, 1, 1000));

        var stoppedZone = await factory.AddZoneAsync(NewCode());
        await factory.UpdateAsync<FarmingZone>(stoppedZone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);
        var zone = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)) with { ZoneId = stoppedZone.Id, Complete = true });
        Assert.Equal("Zone", await zone.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync(r => r.ZoneId == otherZone.Id || r.ZoneId == stoppedZone.Id)));
    }

    // Done when: a completed receipt with 2 seed lines and 1 float line creates exactly 2 seed lots and 1 payment, totals match the rules.
    // (Completing an existing Draft that has seed lines fails: see PurchasingBugTests.pending.cs.txt.)
    [Fact]
    public async Task Create_and_complete_makes_one_seed_lot_per_seed_line_with_the_receipt_data_and_the_paid_now_payment()
    {
        var world = await factory.WorldAsync();
        var completed = await world.Owner.CreateReceiptAsync(world.Body(
            new LineBody(world.Seed, 12.345m, 85_000, " SUP-LOT-7 ", "Trại giống Vân Đồn"),
            new LineBody(world.Seed, 1.5m, 333),
            new LineBody(world.Material, 2.5m, 1300, "ignored", "ignored")) with
        {
            RaftId = world.Raft.Id, DiscountAmount = 49_000, AmountPaidNow = 300_000, PaidNowMethod = "BankTransfer", Notes = "Hóa đơn số 12", Complete = true,
        });

        // Line amounts are rounded to the nearest 1,000 VND: 1,049,325 → 1,049,000; 499.5 → 0; 3,250 → 3,000.
        Assert.Equal(1_052_000, completed.Money("subtotal"));
        Assert.Equal(1_003_000, completed.Money("payable"));
        Assert.Equal(300_000, completed.Money("paid"));
        Assert.Equal(703_000, completed.Money("outstanding"));
        Assert.NotNull(completed.GetProperty("completedAt").GetString());
        var materialLine = completed.LineViewOf(world.Material);
        Assert.Equal(JsonValueKind.Null, materialLine.GetProperty("supplierLotCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, materialLine.GetProperty("seedLotId").ValueKind);
        Assert.Equal("piece", materialLine.GetProperty("unit").GetString());

        var lots = await factory.QueryAsync(db => db.SeedReceiptLots.Where(l => l.PurchaseReceiptLine.ReceiptId == completed.Id()).OrderBy(l => l.InternalCode).ToListAsync());
        Assert.Equal(2, lots.Count);
        var code = completed.GetProperty("code").GetString();
        Assert.Equal([$"{code}-S1", $"{code}-S2"], lots.Select(l => l.InternalCode));
        var big = lots.Single(l => l.Quantity == 12.345m);
        Assert.Equal((world.Seed, world.SupplierId, "kg", "SUP-LOT-7", "Trại giống Vân Đồn"), (big.SeedTypeId, big.SupplierId, big.Unit, big.SupplierLotCode, big.ActualSource));
        Assert.Equal(Today, DateOnly.FromDateTime(big.ReceivedAt.ToOffset(TimeSpan.FromHours(7)).DateTime));
        Assert.Equal(lots.Select(l => l.Id).Order(), completed.GetProperty("lines").EnumerateArray()
            .Where(l => l.GetProperty("seedLotId").ValueKind != JsonValueKind.Null).Select(l => l.GetProperty("seedLotId").GetGuid()).Order());
        Assert.All(completed.GetProperty("lines").EnumerateArray(), l => Assert.Equal(0, l.Money("stocked")));

        var payment = Assert.Single(completed.GetProperty("payments").EnumerateArray());
        Assert.Equal((300_000m, "BankTransfer", "Recorded"), (payment.Money("amount"), payment.GetProperty("method").GetString(), payment.GetProperty("status").GetString()));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), payment.GetProperty("paymentDate").GetString());

        // A second complete is a status error and creates nothing more.
        var again = await world.Owner.PostAsync($"/api/purchase-receipts/{completed.Id()}/complete", null);
        await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal(2, await factory.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.PurchaseReceiptLine.ReceiptId == completed.Id())));
        Assert.Equal(1, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.ReceiptId == completed.Id())));

        var audit = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == completed.Id()).ToListAsync());
        Assert.Equal(new[] { "PurchaseReceipt.Created", "PurchaseReceipt.StatusChanged" }, audit.Select(e => e.Action).Order(StringComparer.Ordinal));
        Assert.All(audit, e => Assert.Equal(world.Zone.Id, e.ZoneId));
        var recorded = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == payment.Id() && e.Action == "SupplierPayment.Recorded"));
        Assert.Equal(world.Zone.Id, recorded.ZoneId);
    }

    [Fact]
    public async Task A_draft_without_lines_can_be_edited_and_a_draft_without_seed_lines_completed()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body());
        var edited = await (await world.Owner.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body() with
        {
            Notes = "Invoice pending 7", RowVersion = draft.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Invoice pending 7", edited.GetProperty("notes").GetString());
        var update = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == draft.Id() && e.Action == "PurchaseReceipt.Updated"));
        Assert.Contains("Invoice pending 7", update.After);

        var other = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 3, 100_000)) with { AmountPaidNow = 100_000, PaidNowMethod = "Cash" });
        var completed = await (await world.Owner.PostAsJsonAsync($"/api/purchase-receipts/{other.Id()}/complete", new { rowVersion = other.GetProperty("rowVersion").GetString() }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Completed", 200_000m), (completed.GetProperty("status").GetString(), completed.Money("outstanding")));
        Assert.Single(completed.GetProperty("payments").EnumerateArray());
    }

    [Fact]
    public async Task Editing_or_completing_a_receipt_that_is_not_a_draft_returns_MSG61()
    {
        var world = await factory.WorldAsync();
        var completed = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 10_000));
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        await (await world.Owner.CancelAsync(draft.Id(), "Not bought", null)).ReadAsync(HttpStatusCode.OK);

        foreach (var id in new[] { completed.Id(), draft.Id() })
        {
            await (await world.Owner.PutAsJsonAsync($"/api/purchase-receipts/{id}", world.Body(new LineBody(world.Other, 9, 10_000)))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
            await (await world.Owner.PostAsync($"/api/purchase-receipts/{id}/complete", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        }

        var cancelled = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{draft.Id()}");
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(1, cancelled.GetProperty("lines")[0].Money("quantity"));
    }

    [Fact]
    public async Task Draft_edit_with_a_stale_row_version_returns_MSG06_and_keeps_the_other_save()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        await factory.UpdateAsync<PurchaseReceipt>(draft.Id(), r => { r.Notes = "Saved by someone else"; r.RowVersion = [0, 0, 0, 0, 0, 0, 9, 3]; });

        var stale = await world.Owner.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body(new LineBody(world.Other, 7, 10_000)) with
        {
            RowVersion = "AAAAAAAACQA=",
        });
        // The row-version check answers (naming who saved and the current version), not a failed save.
        Assert.True((await stale.ProblemAsync(HttpStatusCode.Conflict, "MSG06")).TryGetProperty("currentRowVersion", out _));
        var staleComplete = await world.Owner.PostAsJsonAsync($"/api/purchase-receipts/{draft.Id()}/complete", new { rowVersion = "AAAAAAAACQA=" });
        Assert.True((await staleComplete.ProblemAsync(HttpStatusCode.Conflict, "MSG06")).TryGetProperty("currentRowVersion", out _));

        var kept = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{draft.Id()}");
        Assert.Equal(("Saved by someone else", "Draft"), (kept.GetProperty("notes").GetString(), kept.GetProperty("status").GetString()));
        Assert.Equal(1, kept.GetProperty("lines")[0].Money("quantity"));
    }

    [Fact]
    public async Task A_zone_manager_works_only_on_receipts_of_granted_zones()
    {
        var world = await factory.WorldAsync();
        var (otherZone, _) = await factory.ZoneWithRaftAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var foreign = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)) with { ZoneId = otherZone.Id });

        // In the granted zone: create, edit and complete.
        var own = await manager.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        await (await manager.PostAsync($"/api/purchase-receipts/{own.Id()}/complete", null)).ReadAsync(HttpStatusCode.OK);

        // Another zone: no create, no read, no edit, no complete, and a draft cannot be moved there.
        await (await manager.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)) with { ZoneId = otherZone.Id })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.GetAsync($"/api/purchase-receipts/{foreign.Id()}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.GetAsync($"/api/purchase-receipts/{foreign.Id()}/returns")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.PutAsJsonAsync($"/api/purchase-receipts/{foreign.Id()}", world.Body(new LineBody(world.Other, 5, 10_000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.PutAsJsonAsync($"/api/purchase-receipts/{foreign.Id()}", world.Body(new LineBody(world.Other, 5, 10_000)) with { ZoneId = otherZone.Id }))
            .ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.PostAsync($"/api/purchase-receipts/{foreign.Id()}/complete", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        var ownDraft = await manager.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        await (await manager.PutAsJsonAsync($"/api/purchase-receipts/{ownDraft.Id()}", world.Body(new LineBody(world.Other, 1, 10_000)) with { ZoneId = otherZone.Id }))
            .ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        var foreignNow = await factory.QueryAsync(db => db.PurchaseReceipts.Include(r => r.Lines).SingleAsync(r => r.Id == foreign.Id()));
        Assert.Equal((StatusIds.PurchaseReceipt.Draft, 1m), (foreignNow.StatusId, foreignNow.Lines.Single().Quantity));
        Assert.Equal(world.Zone.Id, (await factory.QueryAsync(db => db.PurchaseReceipts.SingleAsync(r => r.Id == ownDraft.Id()))).ZoneId);
    }

    [Fact]
    public async Task View_only_role_is_respected_and_any_zone_manager_of_the_zone_completes_a_draft()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        // The Accountant views purchase receipts only (§I.4.4 PURCHASE-03 R).
        var viewer = await factory.AccountantAsync(world.Zone);
        await (await viewer.GetAsync($"/api/purchase-receipts/{draft.Id()}")).ReadAsync(HttpStatusCode.OK);
        await (await viewer.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await viewer.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body(new LineBody(world.Other, 2, 10_000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await viewer.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await viewer.CancelAsync(draft.Id(), "x", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        // Any Zone Manager of the zone may edit or complete a draft somebody else recorded.
        var manager = await factory.ZoneManagerAsync(world.Zone);
        await (await manager.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null)).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Receipt_codes_count_up_per_day()
    {
        var world = await factory.WorldAsync();
        var first = await world.Owner.CreateReceiptAsync(world.Body());
        var second = await world.Owner.CreateReceiptAsync(world.Body());
        static string Code(JsonElement r) => r.GetProperty("code").GetString()!;
        static int Number(string code) => int.Parse(code[^3..]);
        static string Day(string code) => code[..^3];
        var (firstCode, secondCode) = (Code(first), Code(second));
        // The day is the server's: when the two saves straddle midnight (UTC+7) the sequence starts again at 001.
        Assert.Matches(@"^PR-\d{8}-\d{3}$", secondCode);
        Assert.Equal(Day(firstCode) == Day(secondCode) ? Number(firstCode) + 1 : 1, Number(secondCode));
        Assert.Contains(Day(secondCode), new[] { $"PR-{Today:yyyyMMdd}-", $"PR-{Today.AddDays(-1):yyyyMMdd}-" });
    }
}
