using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PR P2 (OMTS-218): PURCHASE-06 on statuses other than Completed (MSG-GEN-09), the MSG-PURCHASE-08 recipients (every
/// Owner and every Accountant with an Effective pair, the Owner also when the Owner cancelled), a supplier already paid in
/// advance (MSG-PURCHASE-07, the preview equal to the cancel), the Owner's correction (PURCHASE-06.2) after a seeding with no
/// quantity and its effect on the list and the raft material cost, and the PURCHASE-03.2 export holding the totals shown.
/// </summary>
public class PurchaseReceiptCancelCorrectListQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Receipts = "/api/purchase-receipts";

    private static Task<HttpResponseMessage> CorrectAsync(HttpClient client, Guid receiptId, object body) =>
        client.PostAsJsonAsync($"{Receipts}/{receiptId}/correct", body);

    [Fact]
    public async Task A_draft_or_a_cancelled_receipt_is_never_previewed_cancelled_corrected_returned_and_a_cancelled_one_never_removed()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));
        var cancelled = await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Entered twice")).ReadAsync(HttpStatusCode.OK);

        foreach (var receipt in new[] { draft, cancelled })
        {
            await (await world.Owner.GetAsync($"{Receipts}/{receipt.Id()}/cancel-preview")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
            await (await world.Owner.CancelAsync(receipt.Id(), "Again")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
            await (await CorrectAsync(world.Owner, receipt.Id(), new { reason = "Price", lines = new[] { new { lineId = receipt.LineOf(world.Other), unitPrice = 900 } } }))
                .ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
            await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(receipt.LineOf(world.Other), 1)] }))
                .ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        }

        await (await world.Owner.DeleteAsync($"{Receipts}/{cancelled.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var kept = await (await world.Owner.GetAsync($"{Receipts}/{cancelled.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Cancelled", "Entered twice", 1), (kept.GetProperty("status").GetString(), kept.GetProperty("cancelReason").GetString(), kept.GetProperty("lines").GetArrayLength()));
    }

    // PURCHASE-06 step 7, BR-PURCHASE-05: the Accountant and the Owner, the Owner also when the Owner cancelled; the
    // Accountant whatever the zone of the pair; never a locked account, a revoked pair or a Zone Manager.
    [Fact]
    public async Task The_advance_notice_goes_to_every_owner_and_effective_accountant_and_never_to_a_locked_or_revoked_one()
    {
        var password = NewPassword();
        await factory.EnsureCooperativeAsync();
        var owner = await factory.AddAccountAsync(NewCode(), password);
        var world = await factory.WorldAsync(await factory.LoginAsync(owner.Username, password));
        var allZones = await StaffAsync(StaffRole.Accountant, null);
        var oneZone = await StaffAsync(StaffRole.Accountant, world.Zone.Id);
        var locked = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff, a => a.StatusId = StatusIds.Account.Locked);
        await factory.GrantAsync(locked, StaffRole.Accountant, null);
        var revoked = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await factory.GrantAsync(revoked, StaffRole.Accountant, null, g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; g.RevokeReason = "QA"; });
        var manager = await StaffAsync(StaffRole.ZoneManager, world.Zone.Id);
        var receipt = await world.PaidAsync(300_000, new LineBody(world.Other, 5, 100_000));

        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong supplier")).ReadAsync(HttpStatusCode.OK);

        var recipients = await factory.QueryAsync(db => db.Notifications
            .Where(n => n.RelatedRecordId == receipt.Id() && n.Type == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance).Select(n => n.RecipientAccountId).ToListAsync());
        var owners = await factory.QueryAsync(db => db.Accounts.Where(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active).Select(a => a.Id).ToListAsync());
        Assert.Equal(owners.Append(allZones).Append(oneZone).Order(), recipients.Order());
        Assert.Contains(owner.Id, recipients);
        Assert.DoesNotContain(locked.Id, recipients);
        Assert.DoesNotContain(revoked.Id, recipients);
        Assert.DoesNotContain(manager, recipients);
    }

    // PURCHASE-06 steps 2 and 7 when the supplier was already paid in advance: the debt goes further below zero, so the
    // preview warns (MSG-PURCHASE-07) and the cancel notifies (MSG-PURCHASE-08) with the advance the preview announced.
    [Fact]
    public async Task A_supplier_already_paid_in_advance_is_warned_and_notified_again_and_the_preview_matches_the_cancel()
    {
        var world = await factory.WorldAsync();
        var paid = await world.PaidAsync(100_000, new LineBody(world.Other, 1, 100_000));
        var unpaid = await world.CompletedAsync(new LineBody(world.Other, 1, 50_000));
        await (await world.Owner.CancelAsync(paid.Id(), "Wrong supplier")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(-50_000m, await world.DebtAsync());

        var preview = await (await world.Owner.GetAsync($"{Receipts}/{unpaid.Id()}/cancel-preview")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((true, -50_000m, -100_000m, 100_000m, "MSG-PURCHASE-07"),
            (preview.GetProperty("canCancel").GetBoolean(), preview.Money("debtBefore"), preview.Money("debtAfter"), preview.Money("advanceAfter"), preview.GetProperty("warningCode").GetString()));

        await (await world.Owner.CancelAsync(unpaid.Id(), "Not delivered")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(preview.Money("debtAfter"), await world.DebtAsync());
        var advance = await factory.QueryAsync(db => db.Notifications
            .Where(n => n.RelatedRecordId == unpaid.Id() && n.Type == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance).Select(n => n.MessageParams).FirstAsync());
        Assert.Equal(preview.Money("advanceAfter"), JsonDocument.Parse(advance!).RootElement.GetProperty("advance_amount").GetDecimal());
    }

    // GBR-04 / PURCHASE-06.2.E1: a seeding session without a quantity counts as the whole line seeded, so the Owner can
    // raise the quantity but never lower it; the receipt stays impossible to cancel.
    [Fact]
    public async Task After_a_seeding_without_a_quantity_the_owner_can_only_raise_the_quantity_of_the_line()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 50, 9_000));
        var line = receipt.LineOf(world.Seed);
        await factory.SeedAsync(world.Raft.Id, line, null);

        var below = await (await CorrectAsync(world.Owner, receipt.Id(), new { reason = "Counted again", lines = new[] { new { lineId = line, quantity = 49.99m } } }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-10");
        Assert.Equal(("Line 1", 50m, 50m, 0m), (below.GetProperty("field").GetString(), below.Money("minQuantity"), below.Money("seededQuantity"), below.Money("returnedQuantity")));

        var raised = await (await CorrectAsync(world.Owner, receipt.Id(), new { reason = "Supplier invoice", lines = new[] { new { lineId = line, quantity = 60 } } })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((60m, 540_000m, 60m, 0m), (raised.LineViewOf(world.Seed).Money("quantity"), raised.LineViewOf(world.Seed).Money("lineAmount"),
            raised.LineViewOf(world.Seed).Money("seeded"), raised.LineViewOf(world.Seed).Money("returnable")));
        Assert.Equal("Seeded", (await (await world.Owner.CancelAsync(receipt.Id(), "x")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09")).GetProperty("cause").GetString());
    }

    // PURCHASE-06.2 step 5: the list and the raft material cost follow the corrected line amounts; repeated or missing lines
    // and a version read before another change are refused.
    [Fact]
    public async Task A_correction_moves_the_list_and_the_raft_material_cost_and_refuses_repeated_lines_or_a_stale_version()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(world.MaterialForRaft(4, 25_000), new LineBody(world.Other, 1, 10_000));
        var material = receipt.LineOf(world.Material);
        await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(material, 1)] })).ReadAsync(HttpStatusCode.Created);
        var current = await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}/{receipt.Id()}");

        Assert.Equal("Lines", await (await CorrectAsync(world.Owner, receipt.Id(), new { reason = "x", lines = new[] { new { lineId = material, unitPrice = 1 }, new { lineId = material, unitPrice = 2 } } }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Lines", await (await CorrectAsync(world.Owner, receipt.Id(), new { reason = "x", lines = Array.Empty<object>() })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await CorrectAsync(world.Owner, receipt.Id(), new { reason = "x", rowVersion = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]), lines = new[] { new { lineId = material, unitPrice = 1 } } }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        await (await CorrectAsync(world.Owner, receipt.Id(), new { reason = "Invoice price", rowVersion = current.GetProperty("rowVersion").GetString(), lines = new[] { new { lineId = material, unitPrice = 20_000 } } }))
            .ReadAsync(HttpStatusCode.OK);

        var list = await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}?supplierId={world.SupplierId}&raftId={world.Raft.Id}");
        var row = Assert.Single(list.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal((90_000m, 80_000m, 80_000m), (row.Money("total"), row.Money("linesTotal"), list.GetProperty("totals").Money("receiptsTotal")));
        var costs = await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}/raft-material-costs?raftId={world.Raft.Id}");
        Assert.Equal(80_000m, Assert.Single(costs.GetProperty("rafts").EnumerateArray()).Money("purchased"));
    }

    // PURCHASE-03.2: the file holds the receipts and the three totals shown (and the raft material cost with a raft filter).
    [Fact]
    public async Task The_export_holds_the_rows_and_the_totals_shown_with_and_without_a_raft_filter()
    {
        var world = await factory.WorldAsync();
        var completed = await world.CompletedAsync(world.MaterialForRaft(1.5m, 333), new LineBody(world.Other, 1, 1_000));
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 2, 1_000)));
        await (await world.Owner.PostReturnAsync(completed.Id(), new ReturnBody { Lines = [new ReturnLineBody(completed.LineOf(world.Other), 1)] }))
            .ReadAsync(HttpStatusCode.Created);

        foreach (var (filter, rows, totals) in new[]
        {
            ($"supplierId={world.SupplierId}", new[] { (completed, "Completed", 1_500m, 1_500m), (draft, "Draft", 2_000m, 2_000m) }, new decimal?[] { 1_500m, 1_000m, 500m, null }),
            ($"supplierId={world.SupplierId}&raftId={world.Raft.Id}", new[] { (completed, "Completed", 1_500m, 500m) }, new decimal?[] { 500m, 0m, 500m, 500m }),
        })
        {
            var shown = (await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}?{filter}")).GetProperty("totals");
            var export = (await world.Owner.GetAsync($"{Receipts}/export?{filter}")).EnsureSuccessStatusCode();
            using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
            var sheetRows = workbook.Worksheet("Purchase receipts").RowsUsed().Skip(1).ToList();

            var data = sheetRows.Take(rows.Length).ToDictionary(r => r.Cell(1).GetString());
            foreach (var (receipt, status, total, linesTotal) in rows)
            {
                var row = data[receipt.GetProperty("code").GetString()!];
                Assert.Equal((status, total, linesTotal), (row.Cell(4).GetString(), row.Cell(5).GetValue<decimal>(), row.Cell(6).GetValue<decimal>()));
            }

            var footer = sheetRows.Skip(rows.Length).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(6).GetValue<decimal>());
            Assert.Equal((totals[0], totals[1], totals[2]), (footer["Completed receipts"], footer["Purchase returns in the period"], footer["Net total"]));
            Assert.Equal((shown.Money("receiptsTotal"), shown.Money("returnsTotal"), shown.Money("netTotal")), (totals[0]!.Value, totals[1]!.Value, totals[2]!.Value));
            Assert.Equal(totals[3], footer.TryGetValue("Raft material cost", out var raftCost) ? raftCost : (decimal?)null);
        }
    }

    // PURCHASE-03.0.E2: a group or a raft that matches no line of the period is MSG-GEN-10 with zero totals.
    [Fact]
    public async Task A_group_or_raft_that_matches_no_line_is_MSG_GEN_10_with_zero_totals()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));

        foreach (var filter in new[] { "itemGroup=Seed", $"raftId={world.Raft.Id}", $"itemId={world.Material}" })
        {
            var result = await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}?supplierId={world.SupplierId}&{filter}");
            Assert.Equal(("MSG-GEN-10", 0, 0m), (result.GetProperty("messageCode").GetString(), result.GetProperty("totals").GetProperty("count").GetInt32(), result.GetProperty("totals").Money("receiptsTotal")));
        }
    }

    private async Task<Guid> StaffAsync(StaffRole role, Guid? zoneId)
    {
        var staff = await factory.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        await FixedRoleTestKit.GrantAsync(factory, staff, role, zoneId);
        return staff.Id;
    }
}
