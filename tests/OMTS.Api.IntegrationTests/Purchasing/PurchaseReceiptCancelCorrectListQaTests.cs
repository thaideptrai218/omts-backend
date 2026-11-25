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
/// advance (MSG-PURCHASE-07, the preview equal to the cancel), and the PURCHASE-03.2 export holding the totals shown.
/// </summary>
public class PurchaseReceiptCancelCorrectListQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Receipts = "/api/purchase-receipts";

    [Fact]
    public async Task A_draft_or_a_cancelled_receipt_is_never_previewed_cancelled_returned_and_a_cancelled_one_never_removed()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));
        var cancelled = await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Entered twice")).ReadAsync(HttpStatusCode.OK);

        foreach (var receipt in new[] { draft, cancelled })
        {
            await (await world.Owner.GetAsync($"{Receipts}/{receipt.Id()}/cancel-preview")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
            await (await world.Owner.CancelAsync(receipt.Id(), "Again")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
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
            .Where(n => n.RelatedRecordId == receipt.Id() && n.Type == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance).Select(n => n.RecipientAccountId!.Value).ToListAsync());
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

    // PURCHASE-03.2: the file holds the receipts and the three totals shown; the raft material cost is not a receipt total.
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
            ($"supplierId={world.SupplierId}&raftId={world.Raft.Id}", new[] { (completed, "Completed", 1_500m, 500m) }, new decimal?[] { 500m, 0m, 500m, null }),
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
