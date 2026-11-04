using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PURCHASE-02 through the API (report qa-261104-purchase-returns.md). A test named Finding_* shows a defect and fails on
/// purpose until it is fixed; the others pin what was checked and found right, or how a choice of the branch behaves.
/// </summary>
public class PurchaseReturnsQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Debts = "/api/supplier-debts";

    private static ReturnBody Return(Guid line, decimal quantity, bool approved = false) =>
        new() { OwnerApproved = approved, Lines = [new ReturnLineBody(line, quantity)] };

    private static Guid Newest(JsonElement receipt) =>
        receipt.GetProperty("returns").EnumerateArray().OrderByDescending(r => r.GetProperty("code").GetString(), StringComparer.Ordinal).First().Id();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private Task<int> ReturnCountAsync(Guid receiptId) => factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receiptId));

    private async Task<HttpClient> LoginAsNewAsync(SubjectType subject)
    {
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password, subject);
        return await factory.LoginAsync(account.Username, password);
    }

    // FINDING L1. PURCHASE-02.0.E1/E2: invalid data is MSG-GEN-01. `lines: [null]` reaches PurchaseReturnRules.InvalidFields, which reads lines[i].ReceiptLineId.
    [Fact]
    public async Task Finding_L1_a_null_line_in_a_return_is_MSG_GEN_01_not_a_server_error()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));

        var response = await world.Owner.PostAsync($"/api/purchase-receipts/{receipt.Id()}/returns", Json("""{"reason":"Torn","lines":[null]}"""));

        await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    // FINDING L1, the same in the Owner's correction (CorrectAsync reads l.LineId of each input).
    [Fact]
    public async Task Finding_L1_a_null_line_in_a_correction_is_MSG_GEN_01_not_a_server_error()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);

        var response = await world.Owner.PostAsync($"/api/purchase-receipts/{receipt.Id()}/correct", Json("""{"reason":"Price","lines":[null]}"""));

        await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Theory]
    [InlineData("""{"reason":"Torn","lines":[{"receiptLineId":"not-a-guid","quantity":1}]}""")]
    [InlineData("""{"reason":"Torn","lines":[{"receiptLineId":"LINE","quantity":"abc"}]}""")]
    [InlineData("""{"reason":"Torn","lines":[{"receiptLineId":"LINE","quantity":1e400}]}""")]
    [InlineData("""{"reason":"Torn","lines":[{"receiptLineId":"LINE","quantity":1}],"ownerApproved":"yes"}""")]
    [InlineData("""{"reason":5,"lines":"none"}""")]
    [InlineData("""not json""")]
    public async Task A_body_that_cannot_be_read_answers_400_and_saves_nothing(string body)
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));

        var response = await world.Owner.PostAsync(
            $"/api/purchase-receipts/{receipt.Id()}/returns", Json(body.Replace("LINE", receipt.LineOf(world.Other).ToString())));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await ReturnCountAsync(receipt.Id()));
    }

    // The limit is received − seeded − returned: only Recorded returns count, the message names each part, and the correction's minimum reads the same numbers.
    [Fact]
    public async Task The_limit_of_a_seed_line_and_the_minimum_of_the_correction_read_seeded_and_recorded_returns_only()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 1_000));
        var line = receipt.LineOf(world.Seed);
        var itemName = await factory.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == world.Seed).Select(i => i.Name).SingleAsync());
        await factory.SeedAsync(world.Raft.Id, line, 30);
        var mistaken = Newest(await world.ReturnAsync(receipt.Id(), line, 10));
        await (await world.Owner.CancelReturnAsync(mistaken, "Not sent back")).ReadAsync(HttpStatusCode.OK);
        await world.ReturnAsync(receipt.Id(), line, 20);

        var over = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 50.01m))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.Equal(
            $"{itemName}: enter a quantity greater than 0 and at most 50 kg. Received 100 kg, seeded 30 kg, already returned 20 kg.",
            over.GetProperty("detail").GetString());
        Assert.Equal(("Line 1", line, 50m, 100m, 30m, 20m, "kg", "MSG59"),
            (over.GetProperty("field").GetString(), over.GetProperty("receiptLineId").GetGuid(), over.Money("maxQuantity"), over.Money("received"), over.Money("seeded"), over.Money("returned"),
                over.GetProperty("unit").GetString(), over.GetProperty("legacyCode").GetString()));
        Assert.False(over.TryGetProperty("limit", out _));

        var below = await (await world.Owner.CorrectAsync(receipt.Id(), "Counted again", new { lineId = line, quantity = 49.99m })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-10");
        Assert.Equal((50m, 30m, 20m), (below.Money("minQuantity"), below.Money("seededQuantity"), below.Money("returnedQuantity")));
        var corrected = await (await world.Owner.CorrectAsync(receipt.Id(), "Counted again", new { lineId = line, quantity = 50 })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((0m, 30m, 20m), (corrected.LineViewOf(world.Seed).Money("returnable"), corrected.LineViewOf(world.Seed).Money("seeded"), corrected.LineViewOf(world.Seed).Money("returnedQuantity")));
    }

    // FINDING L2 (reachable once CYCLE-02 writes seeding sessions). GBR-04: seeding does not compare with what is left, so seeded + returned can pass the quantity received.
    // PurchaseReceiptCorrectionService.CheckLine compares even an unchanged quantity with seeded + returned, so the Owner can no longer correct the unit price.
    [Fact]
    public async Task Finding_L2_a_price_correction_is_not_refused_for_a_quantity_it_does_not_change_after_seeding_passed_what_is_left()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 10, 1_000));
        var line = receipt.LineOf(world.Seed);
        await world.ReturnAsync(receipt.Id(), line, 4);
        await factory.SeedAsync(world.Raft.Id, line, 8);

        var corrected = await (await world.Owner.CorrectAsync(receipt.Id(), "Invoice price", new { lineId = line, unitPrice = 900 })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(9_000m, corrected.Money("total"));
    }

    // The same rule, with a session that holds no quantity: it counts as all seeded, so the minimum is always above the quantity.
    [Fact]
    public async Task Finding_L2_a_price_correction_is_not_refused_after_a_seeding_without_a_quantity_on_a_line_with_a_return()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 10, 1_000));
        var line = receipt.LineOf(world.Seed);
        await world.ReturnAsync(receipt.Id(), line, 4);
        await factory.SeedAsync(world.Raft.Id, line, null);

        var corrected = await (await world.Owner.CorrectAsync(receipt.Id(), "Invoice price", new { lineId = line, unitPrice = 900 })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(9_000m, corrected.Money("total"));
    }

    // §I.4.4 PURCHASE-02 Owner Full, Zone Manager Full* (no zone: role only, BR-FARM-08), others No; PURCHASE-03 adds Accountant R; the cancel is the Owner's.
    [Fact]
    public async Task Each_return_endpoint_answers_each_actor_as_the_matrix_says()
    {
        var world = await factory.WorldAsync();
        var (otherZone, otherRaft) = await factory.ZoneWithRaftAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var otherManager = await factory.ZoneManagerAsync(otherZone);
        var accountant = await factory.AccountantAsync();
        var trader = await LoginAsNewAsync(SubjectType.Trader);
        var administrator = await LoginAsNewAsync(SubjectType.SystemAdministrator);
        var anonymous = factory.CreateClient();

        // A line for a raft of another zone: receipts and returns follow the role only, also there.
        var receipt = await world.CompletedAsync(new LineBody(world.Material, 100, 1_000, RaftId: otherRaft.Id));
        var line = receipt.LineOf(world.Material);
        var returnId = Newest(await world.ReturnAsync(receipt.Id(), line, 1));
        var returnsUrl = $"/api/purchase-receipts/{receipt.Id()}/returns";
        var returnUrl = $"/api/purchase-returns/{returnId}";

        await (await manager.PostReturnAsync(receipt.Id(), Return(line, 1, approved: true))).ReadAsync(HttpStatusCode.Created);
        await (await otherManager.PostReturnAsync(receipt.Id(), Return(line, 1, approved: true))).ReadAsync(HttpStatusCode.Created);
        foreach (var reader in new[] { world.Owner, manager, otherManager, accountant })
        {
            await (await reader.GetAsync(returnsUrl)).ReadAsync(HttpStatusCode.OK);
            await (await reader.GetAsync(returnUrl)).ReadAsync(HttpStatusCode.OK);
        }

        foreach (var refused in new[] { accountant, trader, administrator })
        {
            await (await refused.PostReturnAsync(receipt.Id(), Return(line, 1, approved: true))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        foreach (var refused in new[] { trader, administrator })
        {
            await (await refused.GetAsync(returnsUrl)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await refused.GetAsync(returnUrl)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        // The refusal comes before the body is read, so a body that could not be saved tells a non-Owner nothing.
        foreach (var refused in new[] { manager, otherManager, accountant, trader, administrator })
        {
            await (await refused.CancelReturnAsync(returnId, "Mine")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await refused.PostAsync($"{returnUrl}/cancel", Json("not json"))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostReturnAsync(receipt.Id(), Return(line, 1, approved: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(returnsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(returnUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.CancelReturnAsync(returnId, "Nobody")).StatusCode);

        Assert.Equal(3, await ReturnCountAsync(receipt.Id()));
        await (await world.Owner.CancelReturnAsync(returnId, "Not sent back")).ReadAsync(HttpStatusCode.OK);
    }

    // What the flag really is: the Zone Manager's word, kept in the audit. The API cannot know the Owner agreed, a client that always sends it never asks,
    // and the amount the Owner approved is not part of the request. SRS asks no more ("the Zone Manager confirms this approval to the system", BR-PURCHASE-07).
    [Fact]
    public async Task Owner_approved_is_only_the_managers_word_sent_first_it_skips_the_question_and_it_does_not_hold_the_amount_asked()
    {
        var world = await factory.WorldAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 25_000));
        var line = receipt.LineOf(world.Other);

        // Never asked, and the audit holds true all the same.
        var first = await (await manager.PostReturnAsync(receipt.Id(), Return(line, 1, approved: true))).ReadAsync(HttpStatusCode.Created);
        var firstId = Newest(first);
        Assert.Contains("\"OwnerApprovalConfirmed\":true", await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == firstId).Select(e => e.After!).SingleAsync()));

        // Asked at 25,000; the Owner corrects the price; the same request is saved at 5,000 with no new question.
        var asked = await (await manager.PostReturnAsync(receipt.Id(), Return(line, 1))).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-02");
        Assert.Equal(25_000m, asked.Money("returnAmount"));
        await (await world.Owner.CorrectAsync(receipt.Id(), "Invoice price", new { lineId = line, unitPrice = 5_000 })).ReadAsync(HttpStatusCode.OK);
        var saved = await (await manager.PostReturnAsync(receipt.Id(), Return(line, 1, approved: true))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(5_000m, saved.GetProperty("returns").EnumerateArray().Single(r => r.Id() == Newest(saved)).Money("returnValue"));
    }

    // FINDING L3. Parts of a line round one by one (client list §2): the whole line sent back in parts takes a dong more than the line. Nothing was paid, yet BR-PURCHASE-04
    // makes the debt below zero "paid in advance": the supplier appears on the debt list, in the total advance. PM 03/11 rounds "each line" and does not say what parts add up to.
    [Fact]
    public async Task Finding_L3_a_line_sent_back_whole_in_parts_leaves_no_debt_and_no_advance_when_nothing_was_paid()
    {
        var world = await factory.WorldAsync();
        var tag = NewCode();
        world = world with { SupplierId = await world.Owner.CreateSupplierAsync(name: $"{tag} NCC") };
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1.5m, 333));
        var line = receipt.LineOf(world.Other);
        Assert.Equal(500m, receipt.Money("total"));

        for (var part = 0; part < 3; part++)
        {
            await world.ReturnAsync(receipt.Id(), line, 0.5m);
        }

        Assert.Equal(0m, await world.DebtAsync());
        var list = await (await world.Owner.GetAsync($"{Debts}?search={tag}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0m, list.Money("totalAdvance"));
    }

    // The choice of the ledger: what a stored return is worth above its receipt lowers the debt, so it covers other receipts, and the unpaid receipts add up to the debt.
    // Cancelling that return takes all of it away again.
    [Fact]
    public async Task A_return_above_its_corrected_receipt_covers_others_as_credit_until_it_is_cancelled()
    {
        var world = await factory.WorldAsync();
        var older = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 40_000)) with { ReceiptDate = Today.AddDays(-9), Complete = true });
        var corrected = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Material, 10, 10_000)) with { ReceiptDate = Today.AddDays(-5), Complete = true });
        var newer = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 80_000)) with { ReceiptDate = Today.AddDays(-1), Complete = true });
        var line = corrected.LineOf(world.Material);
        var returned = await world.ReturnAsync(corrected.Id(), line, 10);
        await (await world.Owner.CorrectAsync(corrected.Id(), "Invoice price", new { lineId = line, unitPrice = 5_000 })).ReadAsync(HttpStatusCode.OK);
        await world.PayAsync(10_000);

        // 40,000 + 50,000 + 80,000 − 100,000 − 10,000 = 60,000; held: 10,000 paid + 50,000 above the corrected receipt.
        var detail = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((60_000m, 1), (detail.Money("debtToday"), detail.GetProperty("debtAgeDays").GetInt32()));
        var unpaid = Assert.Single(detail.GetProperty("unpaidReceipts").EnumerateArray());
        Assert.Equal((newer.Id(), 20_000m, 60_000m), (unpaid.Id(), unpaid.Money("covered"), unpaid.Money("remaining")));

        await (await world.Owner.CancelReturnAsync(Newest(returned), "Not sent back")).ReadAsync(HttpStatusCode.OK);

        var after = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((160_000m, 9), (after.Money("debtToday"), after.GetProperty("debtAgeDays").GetInt32()));
        Assert.Equal(
            [(older.Id(), 10_000m, 30_000m), (corrected.Id(), 0m, 50_000m), (newer.Id(), 0m, 80_000m)],
            after.GetProperty("unpaidReceipts").EnumerateArray().Select(r => (r.Id(), r.Money("covered"), r.Money("remaining"))).ToList());
    }

    // BR-PURCHASE-05: a return that takes the debt below zero and a cancelled return notify nobody; the receipt's cancel it unblocks does, also to the Owner who did it.
    [Fact]
    public async Task A_return_below_zero_and_its_cancel_notify_nobody_and_the_receipt_cancel_they_unblock_sends_MSG_PURCHASE_08_with_the_true_advance()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.PaidAsync(60_000, new LineBody(world.Other, 4, 25_000));
        var count = await factory.QueryAsync(db => db.Notifications.CountAsync());

        // Debt 40,000; the return takes it to −10,000.
        var returned = await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 2);
        Assert.Equal(-10_000, await world.DebtAsync());
        await (await world.Owner.CancelReturnAsync(Newest(returned), "Not sent back")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(count, await factory.QueryAsync(db => db.Notifications.CountAsync()));

        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);

        var told = await factory.QueryAsync(db => db.Notifications
            .Where(n => n.RelatedRecordId == receipt.Id() && n.Type == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance).Select(n => n.MessageParams!).ToListAsync());
        Assert.NotEmpty(told);
        Assert.All(told, p => Assert.Equal(60_000m, JsonDocument.Parse(p).RootElement.GetProperty("advance_amount").GetDecimal()));
    }

    // MSG41 and MSG45 are gone from the code and from what an old client's fields can trigger.
    [Fact]
    public async Task The_legacy_codes_are_gone_and_the_old_money_fields_trigger_nothing()
    {
        Assert.Null(typeof(MessageCodes.Legacy).GetField("Msg41"));
        Assert.Null(typeof(MessageCodes.Legacy).GetField("Msg45"));
        Assert.DoesNotContain("MSG41", MessageCodes.Legacy.KeptAsCode);
        Assert.DoesNotContain("MSG45", MessageCodes.Legacy.KeptAsCode);

        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);

        // A refund not a multiple of 1,000 and far above the amount, a debt reduction that does not add up.
        var saved = await (await world.Owner.PostAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/returns", new
        {
            reason = "Torn", lines = new[] { new { receiptLineId = line, quantity = 1 } }, refundAmount = 1_500_001, refundMethod = "Cash", debtReductionAmount = 1,
        })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal((25_000m, 75_000m), (saved.Money("returned"), await world.DebtAsync()));

        // Over the limit was MSG45 next to MSG-PURCHASE-03.
        var over = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 4))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.Equal("MSG59", over.GetProperty("legacyCode").GetString());
        Assert.DoesNotContain("MSG4", await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 4))).Content.ReadAsStringAsync());
    }

    // One return of several lines: all or nothing, one statement entry, and only its Material line that names the raft moves the raft (BR-FARM-05). A Seed line that names the raft does not.
    [Fact]
    public async Task One_return_of_several_lines_is_all_or_nothing_and_only_a_material_line_with_the_raft_moves_its_cost()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(world.MaterialForRaft(10, 10_000), new LineBody(world.Seed, 10, 1_000, RaftId: world.Raft.Id), new LineBody(world.Other, 10, 500));
        var (material, seed, other) = (receipt.LineOf(world.Material), receipt.LineOf(world.Seed), receipt.LineOf(world.Other));
        var costBefore = (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost");
        Assert.Equal(100_000m, costBefore);

        var refused = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(material, 1), new ReturnLineBody(other, 11)] }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.Equal(("Line 2", other), (refused.GetProperty("field").GetString(), refused.GetProperty("receiptLineId").GetGuid()));
        Assert.Equal(0, await ReturnCountAsync(receipt.Id()));

        var saved = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(material, 1), new ReturnLineBody(seed, 1), new ReturnLineBody(other, 1)] }))
            .ReadAsync(HttpStatusCode.Created);

        Assert.Equal(11_500m, saved.Money("returned"));
        Assert.Equal(90_000m, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));
        var filtered = (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?supplierId={world.SupplierId}&raftId={world.Raft.Id}")).GetProperty("totals");
        Assert.Equal((110_000m, 11_000m, 90_000m), (filtered.Money("receiptsTotal"), filtered.Money("returnsTotal"), filtered.Money("raftMaterialCost")));
        var statement = await world.Owner.GetFromJsonAsync<JsonElement>($"{Debts}/{world.SupplierId}/statement");
        Assert.Equal([("Receipt", 115_000m), ("Return", -11_500m)], statement.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("kind").GetString(), e.Money("amount"))).ToList());
    }

    // Unspecified: GBR-01 only says a discontinued record is not chosen for new records, and BR-PURCHASE-06 keeps its returns. Pinned as it is; question in the report.
    [Fact]
    public async Task A_return_on_a_receipt_of_a_discontinued_supplier_is_accepted()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        await (await world.Owner.PostAsync($"/api/suppliers/{world.SupplierId}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);

        Assert.Equal(75_000, await world.DebtAsync());
    }

    [Fact]
    public async Task A_cancel_without_a_body_or_with_an_empty_one_is_refused_and_changes_nothing()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var returnId = Newest(await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1));

        Assert.Equal(HttpStatusCode.BadRequest, (await world.Owner.PostAsync($"/api/purchase-returns/{returnId}/cancel", Json(""))).StatusCode);
        Assert.Equal("Reason", await (await world.Owner.PostAsync($"/api/purchase-returns/{returnId}/cancel", Json("{}"))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        Assert.Equal(75_000, await world.DebtAsync());
    }
}
