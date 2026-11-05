using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-01 (SRS 27/10): PN codes and seed lot codes (GBR-04), lines half up to the dong (BR-PURCHASE-02), drafts with only a
/// supplier and incomplete lines (BR-PURCHASE-03), a raft per Seed or Material line among the caller's zones
/// (BR-PURCHASE-01), receipts without zone (BR-FARM-08) and the own-draft rule.
/// </summary>
public class PurchaseReceiptSaveTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static int Number(JsonElement receipt) => int.Parse(receipt.GetProperty("code").GetString()![2..]);

    [Fact]
    public async Task Codes_follow_one_PN_sequence_and_each_seed_line_of_a_completed_receipt_is_the_seed_lot_code_and_line_number()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 10, 900)));
        var completed = await world.CompletedAsync(
            new LineBody(world.Other, 1, 1000), new LineBody(world.Seed, 10, 900, " SUP-LOT-7 "), new LineBody(world.Seed, 5, 900));

        var code = completed.GetProperty("code").GetString()!;
        Assert.Matches(@"^PN\d{6}$", code);
        Assert.Matches(@"^PN\d{6}$", draft.GetProperty("code").GetString()!);
        Assert.Equal(Number(draft) + 1, Number(completed));
        var lines = completed.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], lines.Select(l => l.GetProperty("lineNumber").GetInt32()));
        Assert.Equal([null, $"{code}-2", $"{code}-3"], lines.Select(l => l.GetProperty("seedLotCode").GetString()));
        Assert.Equal("SUP-LOT-7", lines[1].GetProperty("supplierLotCode").GetString());
        // A Draft creates no seed lot (BR-PURCHASE-03).
        Assert.Null(draft.SeedLotCodeOf(world.Seed));
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("completedAt").ValueKind);
    }

    [Fact]
    public async Task Line_amounts_are_quantity_times_unit_price_to_the_dong_and_the_total_is_their_sum()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 1.5m, 333), new LineBody(world.Other, 2.25m, 1001), new LineBody(world.Other, 3, 0));

        // 1.5 × 333 = 499.5 → 500; 2.25 × 1,001 = 2,252.25 → 2,252; never to thousands (BR-PURCHASE-02).
        Assert.Equal([500m, 2_252m, 0m], receipt.GetProperty("lines").EnumerateArray().Select(l => l.Money("lineAmount")));
        Assert.Equal(2_752m, receipt.Money("total"));
        Assert.Equal(500m, await factory.QueryAsync(db => db.PurchaseReceiptLines.Where(l => l.ReceiptId == receipt.Id() && l.LineNumber == 1).Select(l => l.LineAmount).SingleAsync()));
        var debt = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt");
        Assert.Equal(2_752m, debt.GetProperty("totals").Money("outstanding"));
    }

    [Fact]
    public async Task A_draft_needs_only_an_in_use_supplier_holds_incomplete_lines_and_completing_checks_every_line()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(new ReceiptBody
        {
            SupplierId = world.SupplierId, ReceiptDate = null,
            Lines = [new LineBody(null, 2, 1000), new LineBody(world.Seed, null, null), new LineBody(world.Other, 2, null)],
        });

        Assert.Equal(("Draft", Today.ToString("yyyy-MM-dd")), (draft.GetProperty("status").GetString(), draft.GetProperty("receiptDate").GetString()));
        var lines = draft.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("itemId").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("quantity").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("lineAmount").ValueKind);
        Assert.Equal(2_000m, draft.Money("total"));

        var refused = await (await world.Owner.CompleteAsync(draft.Id())).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Line 1 item", refused.GetProperty("field").GetString());
        var edited = await (await world.Owner.EditAsync(draft, world.Body(new LineBody(world.Seed, 2, null)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Line 1", await (await world.Owner.CompleteAsync(draft.Id())).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var completed = await (await world.Owner.EditAsync(edited, world.Body(new LineBody(world.Seed, 2, 0)) with { Complete = true })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Completed", 0m), (completed.GetProperty("status").GetString(), completed.Money("total")));
    }

    [Fact]
    public async Task Supplier_date_notes_items_and_line_values_are_checked_on_every_save_with_MSG_GEN_01()
    {
        var world = await factory.WorldAsync();
        var oldSupplier = await world.Owner.CreateSupplierAsync();
        await (await world.Owner.PostAsync($"/api/suppliers/{oldSupplier}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        var oldItem = await world.Owner.CreateItemAsync("Other", "bag");
        await (await world.Owner.PostAsync($"/api/farming-input-items/{oldItem}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        var line = new LineBody(world.Other, 1, 10_000);

        foreach (var (body, field) in new (ReceiptBody, string)[]
        {
            (world.Body(line) with { SupplierId = null }, "Supplier"),
            (world.Body(line) with { SupplierId = Guid.NewGuid() }, "Supplier"),
            (world.Body(line) with { SupplierId = oldSupplier }, "Supplier"),
            (world.Body(line) with { ReceiptDate = Today.AddDays(1) }, "Receipt date"),
            (world.Body(line) with { Notes = new string('n', 1001) }, "Notes"),
            (world.Body(new LineBody(Guid.NewGuid(), 1, 1000)), "Line 1 item"),
            (world.Body(line, new LineBody(oldItem, 1, 1000)), "Line 2 item"),
            (world.Body(new LineBody(world.Seed, 1, 1000, new string('s', 101))), "Line 1 supplier lot code"),
            (world.Body() with { Complete = true }, "Lines"),
            (world.Body(line, new LineBody(world.Other, 0, 1000)), "Line 2"),
            (world.Body(new LineBody(world.Other, 1.234m, 1000)), "Line 1"),
            (world.Body(new LineBody(world.Other, 1, 1000.5m)), "Line 1"),
            (world.Body(new LineBody(world.Other, 1, -1)), "Line 1"),
            (world.Body(new LineBody(world.Other, 999_999_999, 999_999_999_999)), "Line 1"),
        })
        {
            Assert.Equal(field, await (await world.Owner.PostReceiptAsync(body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync(r => r.SupplierId == world.SupplierId)));
        // On the day itself and earlier days the date is accepted.
        await (await world.Owner.PostReceiptAsync(world.Body(line) with { ReceiptDate = Today.AddDays(-30), Complete = true })).ReadAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_raft_goes_on_seed_or_material_lines_in_use_and_a_zone_manager_names_rafts_of_own_zones_only()
    {
        var world = await factory.WorldAsync();
        var (_, foreignRaft) = await factory.ZoneWithRaftAsync();
        var stoppedRaft = new Raft { ZoneId = world.Zone.Id, Code = NewCode(), Size = "20 m x 10 m", StatusId = StatusIds.Usage.Discontinued };
        await factory.UseDbAsync(db => db.Rafts.Add(stoppedRaft));
        var manager = await factory.ZoneManagerAsync(world.Zone);

        foreach (var line in new[]
        {
            new LineBody(world.Other, 1, 1000, RaftId: world.Raft.Id),
            new LineBody(world.Material, 1, 1000, RaftId: foreignRaft.Id),
            new LineBody(world.Material, 1, 1000, RaftId: stoppedRaft.Id),
            new LineBody(world.Seed, 1, 1000, RaftId: Guid.NewGuid()),
        })
        {
            Assert.Equal("Line 1 raft", await (await manager.PostReceiptAsync(world.Body(line))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        // A Draft line without an item yet may hold a raft of the caller's zones; the group is checked once the item is chosen.
        var draft = await manager.CreateReceiptAsync(world.Body(new LineBody(null, 1, 1000, RaftId: world.Raft.Id)));
        Assert.Equal(world.Raft.Id, draft.GetProperty("lines")[0].GetProperty("raft").Id());
        Assert.Equal("Line 1 raft", await (await manager.PostReceiptAsync(world.Body(new LineBody(null, 1, 1000, RaftId: foreignRaft.Id)))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Line 1 raft", await (await manager.EditAsync(draft, world.Body(new LineBody(world.Other, 1, 1000, RaftId: world.Raft.Id)))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var own = await manager.CreateReceiptAsync(world.Body(world.MaterialForRaft(2, 50_000), new LineBody(world.Seed, 10, 900, RaftId: world.Raft.Id)) with { Complete = true });
        var raft = own.LineViewOf(world.Material).GetProperty("raft");
        Assert.Equal((world.Raft.Id, world.Raft.Code, world.Zone.Id, world.Zone.Code),
            (raft.Id(), raft.GetProperty("code").GetString(), raft.GetProperty("zoneId").GetGuid(), raft.GetProperty("zoneCode").GetString()));
        Assert.Equal(world.Raft.Id, own.LineViewOf(world.Seed).GetProperty("raft").Id());

        // The Owner names any raft In Use.
        await (await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Material, 1, 1000, RaftId: foreignRaft.Id)) with { Complete = true })).ReadAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Receipts_have_no_zone_any_zone_manager_reads_lists_and_creates_and_the_accountant_only_reads()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1, 10_000));
        var (otherZone, otherRaft) = await factory.ZoneWithRaftAsync();
        var elsewhere = await factory.ZoneManagerAsync(otherZone);

        await (await elsewhere.GetAsync($"/api/purchase-receipts/{receipt.Id()}")).ReadAsync(HttpStatusCode.OK);
        var listed = await (await elsewhere.GetAsync($"/api/purchase-receipts?supplierId={world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(receipt.Id(), listed.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Id()));
        // A receipt of any supplier, with a raft of the manager's own zone; a raft of zone A stays refused.
        await (await elsewhere.PostReceiptAsync(world.Body(new LineBody(world.Material, 1, 1000, RaftId: otherRaft.Id)) with { Complete = true })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("Line 1 raft", await (await elsewhere.PostReceiptAsync(world.Body(world.MaterialForRaft(1, 1000)))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.GetAsync($"/api/purchase-receipts/export?supplierId={world.SupplierId}")).StatusCode);

        // PURCHASE-03 R for the Accountant: reads and exports every receipt (the export is part of PURCHASE-03), writes nothing.
        var accountant = await factory.AccountantAsync(otherZone);
        await (await accountant.GetAsync($"/api/purchase-receipts/{receipt.Id()}")).ReadAsync(HttpStatusCode.OK);
        await (await accountant.GetAsync("/api/purchase-receipts")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/api/purchase-receipts/export")).StatusCode);
        await (await accountant.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.GetAsync($"/api/purchase-receipts/{receipt.Id()}/copy")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.CancelAsync(receipt.Id(), "x")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task Only_the_author_or_the_owner_continues_or_completes_a_draft_and_other_zone_managers_read_it()
    {
        var world = await factory.WorldAsync();
        var author = await factory.ZoneManagerAsync(world.Zone);
        var colleague = await factory.ZoneManagerAsync(world.Zone);
        var draft = await author.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        Assert.Equal(draft.GetProperty("recordedBy").Id(), await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == draft.Id()).Select(r => r.RecordedByAccountId).SingleAsync()));

        var read = await (await colleague.GetAsync($"/api/purchase-receipts/{draft.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Draft", read.GetProperty("status").GetString());
        await (await colleague.EditAsync(draft, world.Body(new LineBody(world.Other, 9, 10_000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await colleague.CompleteAsync(draft.Id())).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        var edited = await (await author.EditAsync(draft, world.Body(new LineBody(world.Other, 2, 10_000)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2m, edited.GetProperty("lines")[0].Money("quantity"));
        // The Owner continues any draft.
        await (await world.Owner.CompleteAsync(draft.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.PurchaseReceipt.Completed, await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == draft.Id()).Select(r => r.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task A_draft_edit_replaces_and_renumbers_its_lines_needs_the_row_version_read_and_is_audited()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        var url = $"/api/purchase-receipts/{draft.Id()}";
        var body = world.Body(new LineBody(world.Seed, 10, 20_000), new LineBody(world.Other, 2, 5_000));

        Assert.Equal("RowVersion", await (await world.Owner.PutAsJsonAsync(url, body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await world.Owner.PutAsJsonAsync(url, body with { RowVersion = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]) })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var edited = await (await world.Owner.EditAsync(draft, body with { Notes = "Invoice 7" })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal([1, 2], edited.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lineNumber").GetInt32()));
        Assert.Equal(210_000m, edited.Money("total"));
        Assert.Equal(2, await factory.QueryAsync(db => db.PurchaseReceiptLines.CountAsync(l => l.ReceiptId == draft.Id())));
        var completed = await (await world.Owner.EditAsync(edited, world.Body(new LineBody(world.Seed, 10, 20_000)) with { Complete = true })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal($"{completed.GetProperty("code").GetString()}-1", completed.SeedLotCodeOf(world.Seed));
        Assert.Equal(
            ["PurchaseReceipt.Created", "PurchaseReceipt.StatusChanged", "PurchaseReceipt.Updated", "PurchaseReceipt.Updated"],
            (await factory.AuditActionsAsync(draft.Id())).Order(StringComparer.Ordinal));
        Assert.Contains("Invoice 7", await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == draft.Id() && e.Action == "PurchaseReceipt.Updated")
            .OrderBy(e => e.OccurredAt).Select(e => e.After).FirstAsync()));
        Assert.All(await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == draft.Id()).ToListAsync()), e => Assert.Null(e.ZoneId));
    }

    [Fact]
    public async Task A_completed_or_cancelled_receipt_is_never_edited_or_completed_again_and_creates_no_second_notification()
    {
        var world = await factory.WorldAsync();
        var completed = await world.CompletedAsync(new LineBody(world.Other, 1, 10_000));
        var cancelled = await world.CompletedAsync(new LineBody(world.Other, 1, 10_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Nhập nhầm")).ReadAsync(HttpStatusCode.OK);

        foreach (var receipt in new[] { completed, cancelled })
        {
            await (await world.Owner.EditAsync(receipt, world.Body(new LineBody(world.Other, 9, 10_000)))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
            await (await world.Owner.CompleteAsync(receipt.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        }

        var owners = await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.PurchaseReceiptCompleted && n.RelatedRecordId == completed.Id()));
        var ownerAccounts = await factory.QueryAsync(db => db.Accounts.CountAsync(a => a.SubjectType == Domain.Enums.SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active));
        Assert.Equal(ownerAccounts, owners);
    }
}
