// PENDING – fails because of product bugs found in the OMTS-209 QA review (qa1-261023-omts-209-review.md, C1).
// Rename to .cs once fixed. Both fail with 409 MSG06 "updated by another user" on the save:
// new PurchaseReceiptLine / SeedReceiptLot objects are attached to an already tracked receipt through navigations
// (PurchaseReceiptService.UpdateAsync → Apply, CompleteAsync → line.SeedLot = new ...). Their Guid keys are
// already set (Entity.Id = Guid.NewGuid()) and keys are "generated on add", so EF Core's DetectChanges tracks them
// as existing (Modified) rows: UPDATE ... affects 0 rows → DbUpdateConcurrencyException → MSG06. Same on SQL Server
// (see PurchasingSqlServerBugTests.pending.cs.txt). Fix: add them explicitly (db.PurchaseReceiptLines.Add /
// db.SeedReceiptLots.Add) or configure Entity.Id as ValueGeneratedNever.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

public class PurchasingBugTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // INP-03: "anyone with PURCHASE_RECEIPTS Edit in its zone may edit (PUT) or complete it".
    [Fact]
    public async Task Completing_a_draft_with_seed_lines_creates_one_seed_lot_per_seed_line_and_the_paid_now_payment()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(
            new LineBody(world.Seed, 12.345m, 85_000, " SUP-LOT-7 ", "Trại giống Vân Đồn"),
            new LineBody(world.Seed, 1.5m, 333),
            new LineBody(world.Material, 2.5m, 1300)) with
        {
            RaftId = world.Raft.Id, DiscountAmount = 49_000, AmountPaidNow = 300_000, PaidNowMethod = "BankTransfer",
        });

        var completed = await (await world.Owner.PostAsJsonAsync($"/api/purchase-receipts/{draft.Id()}/complete", new { rowVersion = draft.GetProperty("rowVersion").GetString() }))
            .ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Completed", 703_000m), (completed.GetProperty("status").GetString(), completed.Money("outstanding")));
        var lots = await factory.QueryAsync(db => db.SeedReceiptLots.Where(l => l.PurchaseReceiptLine.ReceiptId == draft.Id()).ToListAsync());
        Assert.Equal(2, lots.Count);
        Assert.Single(completed.GetProperty("payments").EnumerateArray());

        await (await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal(2, await factory.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.PurchaseReceiptLine.ReceiptId == draft.Id())));
        Assert.Equal(1, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.ReceiptId == draft.Id())));
    }

    [Fact]
    public async Task A_draft_edit_replaces_its_lines_and_can_complete_in_the_same_call_with_audit()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        var edited = await (await world.Owner.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body(
            new LineBody(world.Seed, 10, 20_000), new LineBody(world.Other, 2, 5_000)) with { Notes = "Sửa", RowVersion = draft.GetProperty("rowVersion").GetString() }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, edited.GetProperty("lines").GetArrayLength());
        Assert.Equal(210_000, edited.Money("subtotal"));
        Assert.Equal(2, await factory.QueryAsync(db => db.PurchaseReceiptLines.CountAsync(l => l.ReceiptId == draft.Id())));

        var completed = await (await world.Owner.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body(
            new LineBody(world.Seed, 10, 20_000)) with { Complete = true, RowVersion = edited.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.Equal(1, await factory.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.PurchaseReceiptLine.ReceiptId == draft.Id())));
        Assert.Equal(
            new[] { "PurchaseReceipt.Created", "PurchaseReceipt.StatusChanged", "PurchaseReceipt.Updated", "PurchaseReceipt.Updated" },
            (await factory.AuditActionsAsync(draft.Id())).Order(StringComparer.Ordinal));
    }

    // INP-01/INP-02: "A Discontinued supplier cannot be selected on a new receipt"; "a Discontinued item cannot go on a new line".
    // CheckDraftAsync only checks that they exist, so a new Draft takes them (201).
    [Fact]
    public async Task A_new_draft_refuses_a_discontinued_supplier_or_item()
    {
        var world = await factory.WorldAsync();
        var oldSupplier = await world.Owner.CreateSupplierAsync();
        await (await world.Owner.PostAsync($"/api/suppliers/{oldSupplier}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        var oldItem = await world.Owner.CreateItemAsync("Seed");
        await (await world.Owner.PostAsync($"/api/farming-input-items/{oldItem}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        var supplier = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)) with { SupplierId = oldSupplier });
        Assert.Equal("Supplier", await supplier.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var item = await world.Owner.PostReceiptAsync(world.Body(new LineBody(oldItem, 1, 1000)));
        Assert.Equal("Line 1 item", await item.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    // INP-07 lists drafts by receipt date (default: this year). A Draft saved without a date gets 0001-01-01
    // (Apply: ReceiptDate = request.ReceiptDate ?? default) and never shows in the default list.
    [Fact]
    public async Task A_draft_saved_without_a_date_is_found_in_the_default_list()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body() with { ReceiptDate = null });
        var list = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?supplierId={world.SupplierId}");
        Assert.Contains(list.GetProperty("page").GetProperty("items").EnumerateArray(), r => r.Id() == draft.Id());
    }

    // INP-07 "Raft material cost per raft (filters zone, raft, period, item)": with an item filter the cost is still the
    // cost of every raft-material line of the matching receipts (RaftMaterialCostsAsync filters receipts, not lines).
    [Fact]
    public async Task The_item_filter_of_the_raft_material_cost_counts_that_item_only()
    {
        var world = await factory.WorldAsync();
        var ropes = await world.Owner.CreateItemAsync("Material", "roll");
        await world.CompletedAsync(0, 0, new LineBody(world.Material, 10, 50_000), new LineBody(ropes, 2, 100_000));

        var costs = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/raft-material-costs?raftId={world.Raft.Id}&itemId={ropes}");
        Assert.Equal(200_000, costs[0].Money("materialCost"));
    }

    // INP-05 rounding: each return value is rounded on its own, so returning a line in parts can credit more than the line
    // is worth (3 × 500 VND → line 2,000; each 1-unit return is worth 1,000) and the last unit can then never be returned
    // (refund + debt reduction must equal 1,000 but outstanding and net paid are 0 → MSG45). Cap the last return of a line
    // at what is left of its payable share, or compute cumulatively.
    [Fact]
    public async Task Returning_a_line_unit_by_unit_never_credits_more_than_the_line_and_the_last_unit_can_be_returned()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 3, 500));
        Assert.Equal(2000, receipt.Money("payable"));
        var line = receipt.LineOf(world.Other);

        JsonElement last = default;
        for (var unit = 0; unit < 3; unit++)
        {
            var outstanding = (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}")).Money("outstanding");
            var value = Math.Min(1000, outstanding);
            last = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { DebtReductionAmount = value, Lines = [new ReturnLineBody(line, 1)] }))
                .ReadAsync(HttpStatusCode.Created);
        }

        Assert.Equal((2000m, 0m), (last.Money("returned"), last.Money("outstanding")));
    }
}
