using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PR P2 (OMTS-218), purchase receipts of SRS 27/10: every receipt endpoint against the §I.4.4 matrix (anonymous,
/// trader, System Administrator, Accountant R), the own-draft and own-cancel rules against the Owner's receipts, the rafts of
/// the Zone Manager pairs only (BR-PURCHASE-01), amounts to the dong half away from zero (BR-PURCHASE-02), the copy saved as
/// it is (PURCHASE-01.4) and the PN sequence after a removed draft (BR-PURCHASE-03). Cancel, correction and list are in
/// <see cref="PurchaseReceiptCancelCorrectListQaTests"/>; races and the migration in <see cref="PurchaseReceiptsSqlServerQaTests"/>.
/// </summary>
public class PurchaseReceiptsQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Receipts = "/api/purchase-receipts";

    /// <summary>
    /// Every receipt endpoint; <c>Writes</c> are the ones the Accountant (PURCHASE-03 R) may not call. The export is part of
    /// PURCHASE-03 ("includes exporting to a file"), so the Accountant calls it (lead decision, QA P2 Q1).
    /// </summary>
    private static IEnumerable<(HttpMethod Method, string Path, Func<HttpContent?> Body, bool Write)> Endpoints(PurchasingWorld world, Guid draft, Guid completed, Guid returnId)
    {
        HttpContent Json(object body) => JsonContent.Create(body);
        yield return (HttpMethod.Get, Receipts, () => null, false);
        yield return (HttpMethod.Get, $"{Receipts}/raft-material-costs", () => null, false);
        yield return (HttpMethod.Get, $"{Receipts}/{completed}", () => null, false);
        yield return (HttpMethod.Get, $"{Receipts}/{completed}/returns", () => null, false);
        yield return (HttpMethod.Get, $"/api/purchase-returns/{returnId}", () => null, false);
        yield return (HttpMethod.Get, $"{Receipts}/export", () => null, false);
        yield return (HttpMethod.Post, Receipts, () => Json(world.Body(new LineBody(world.Other, 1, 1_000)) with { Complete = true }), true);
        yield return (HttpMethod.Put, $"{Receipts}/{draft}", () => Json(world.Body(new LineBody(world.Other, 9, 1_000))), true);
        yield return (HttpMethod.Post, $"{Receipts}/{draft}/complete", () => null, true);
        yield return (HttpMethod.Delete, $"{Receipts}/{draft}", () => null, true);
        yield return (HttpMethod.Get, $"{Receipts}/{completed}/cancel-preview", () => null, true);
        yield return (HttpMethod.Post, $"{Receipts}/{completed}/cancel", () => Json(new { reason = "QA" }), true);
        yield return (HttpMethod.Post, $"{Receipts}/{completed}/correct", () => Json(new { reason = "QA", lines = Array.Empty<object>() }), true);
        yield return (HttpMethod.Get, $"{Receipts}/{completed}/copy", () => null, true);
        yield return (HttpMethod.Post, $"{Receipts}/{completed}/returns", () => Json(new ReturnBody { Lines = [] }), true);
        yield return (HttpMethod.Post, $"/api/purchase-returns/{returnId}/cancel", () => Json(new { reason = "QA" }), true);
    }

    [Fact]
    public async Task Anonymous_gets_401_traders_and_administrators_MSG_GEN_08_and_the_accountant_only_reads_on_every_receipt_endpoint()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));
        var completed = await world.CompletedAsync(new LineBody(world.Seed, 10, 1_000));
        var returned = await world.CompletedAsync(new LineBody(world.Other, 2, 1_000));
        var returnId = (await (await world.Owner.PostReturnAsync(returned.Id(), new ReturnBody { Lines = [new ReturnLineBody(returned.LineOf(world.Other), 1)] }))
            .ReadAsync(HttpStatusCode.Created)).GetProperty("returns")[0].Id();
        var receiptsBefore = await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync(r => r.SupplierId == world.SupplierId));
        var anonymous = factory.CreateClient();
        var refused = new[] { await LoginAsAsync(SubjectType.Trader), await LoginAsAsync(SubjectType.SystemAdministrator) };
        var accountant = await factory.AccountantAsync();

        foreach (var (method, path, body, write) in Endpoints(world, draft.Id(), completed.Id(), returnId))
        {
            using var noToken = await anonymous.SendAsync(new HttpRequestMessage(method, path) { Content = body() });
            Assert.True(noToken.StatusCode == HttpStatusCode.Unauthorized, $"anonymous {method} {path}: {(int)noToken.StatusCode}");
            foreach (var caller in refused)
            {
                using var response = await caller.SendAsync(new HttpRequestMessage(method, path) { Content = body() });
                Assert.True(await response.CodeOrStatusAsync() == "403 MSG-GEN-08", $"{method} {path}: {await response.CodeOrStatusAsync()}");
            }

            using var read = await accountant.SendAsync(new HttpRequestMessage(method, path) { Content = body() });
            var outcome = write ? await read.CodeOrStatusAsync() : $"{(int)read.StatusCode}";
            Assert.True(outcome == (write ? "403 MSG-GEN-08" : "200"), $"accountant {method} {path}: {outcome}");
        }

        // Nothing changed: no receipt added, the draft is still a Draft, the receipt still Completed without a return.
        Assert.Equal(receiptsBefore, await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync(r => r.SupplierId == world.SupplierId)));
        Assert.Equal(StatusIds.PurchaseReceipt.Draft, await StatusAsync(draft.Id()));
        Assert.Equal(StatusIds.PurchaseReceipt.Completed, await StatusAsync(completed.Id()));
        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == completed.Id())));
    }

    // BR-PURCHASE-03/09: a Zone Manager changes, completes, removes and cancels only what they recorded, never the Owner's.
    [Fact]
    public async Task A_zone_manager_reads_and_copies_the_owners_receipts_but_never_changes_removes_or_cancels_them()
    {
        var world = await factory.WorldAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var ownersDraft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));
        var ownersReceipt = await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));

        await (await manager.GetAsync($"{Receipts}/{ownersDraft.Id()}")).ReadAsync(HttpStatusCode.OK);
        await (await manager.GetAsync($"{Receipts}/{ownersReceipt.Id()}/copy")).ReadAsync(HttpStatusCode.OK);
        await (await manager.EditAsync(ownersDraft, world.Body(new LineBody(world.Other, 2, 1_000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.CompleteAsync(ownersDraft.Id())).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.DeleteAsync($"{Receipts}/{ownersDraft.Id()}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.GetAsync($"{Receipts}/{ownersReceipt.Id()}/cancel-preview")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.CancelAsync(ownersReceipt.Id(), "Wrong supplier")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        Assert.Equal((StatusIds.PurchaseReceipt.Draft, StatusIds.PurchaseReceipt.Completed), (await StatusAsync(ownersDraft.Id()), await StatusAsync(ownersReceipt.Id())));
    }

    // BR-PURCHASE-01: the rafts of the zones of the Zone Manager pairs only; a pair of another role in a zone does not count,
    // a Zone Manager pair for All zones covers every zone, and a pair revoked after the draft was saved is checked again.
    [Fact]
    public async Task Line_rafts_follow_the_zone_manager_pairs_only_and_are_checked_again_when_completing()
    {
        var world = await factory.WorldAsync();
        var (zoneB, raftB) = await factory.ZoneWithRaftAsync();
        var password = NewPassword();
        var staff = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, world.Zone);
        await factory.GrantAsync(staff, StaffRole.Accountant, zoneB);
        var client = await factory.LoginAsync(staff.Username, password);

        Assert.Equal("Line 1 raft", await (await client.PostReceiptAsync(world.Body(new LineBody(world.Material, 1, 1_000, RaftId: raftB.Id)))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var allZones = await factory.PurchasingStaffAsync(null, StaffRole.ZoneManager);
        await (await allZones.PostReceiptAsync(world.Body(new LineBody(world.Material, 1, 1_000, RaftId: raftB.Id)) with { Complete = true })).ReadAsync(HttpStatusCode.Created);

        // The pair of zone B becomes a Zone Manager pair: the draft is saved; once it is revoked, completing refuses the raft.
        var managerPair = await factory.GrantAsync(staff, StaffRole.ZoneManager, zoneB);
        var draft = await client.CreateReceiptAsync(world.Body(new LineBody(world.Material, 1, 1_000, RaftId: raftB.Id)));
        await factory.UpdateAsync<AccessGrant>(managerPair.Id, g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; g.RevokeReason = "QA"; });
        Assert.Equal("Line 1 raft", await (await client.CompleteAsync(draft.Id())).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(StatusIds.PurchaseReceipt.Draft, await StatusAsync(draft.Id()));
    }

    // BR-PURCHASE-02: each line half up to the dong (banker's rounding would give 2 and 0), and the total is the sum of the
    // rounded lines (1,504, not the 1,502 of the rounded sum), the same in every figure that shows it.
    [Fact]
    public async Task Halves_round_up_and_the_total_is_the_sum_of_the_rounded_lines_everywhere()
    {
        var world = await factory.WorldAsync();
        var lines = new[]
        {
            new LineBody(world.Other, 2.5m, 1), new LineBody(world.Other, 0.5m, 1), world.MaterialForRaft(1.5m, 333),
            new LineBody(world.Seed, 1.5m, 333), new LineBody(world.Other, 1.5m, 333),
        };

        var receipt = await world.CompletedAsync(lines);

        Assert.Equal([3m, 1m, 500m, 500m, 500m], receipt.GetProperty("lines").EnumerateArray().Select(l => l.Money("lineAmount")));
        Assert.Equal(1_504m, receipt.Money("total"));
        var row = Assert.Single((await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}?supplierId={world.SupplierId}")).GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal(1_504m, row.Money("total"));
        Assert.Equal(1_504m, await world.DebtAsync());
        Assert.Equal(1_504m, (await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}/{receipt.Id()}/cancel-preview")).Money("debtBefore"));
        Assert.Equal(500m, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));
        var notification = Assert.Single((await world.Owner.NotificationPageAsync()).Items(),
            n => n.GetProperty("type").GetString() == NotificationTypes.PurchaseReceiptCompleted && n.GetProperty("relatedRecordId").GetGuid() == receipt.Id());
        Assert.Equal(1_504m, notification.GetProperty("messageParams").GetProperty("total_amount").GetDecimal());
        Assert.EndsWith("5 lines, 1,504 VND.", notification.GetProperty("body").GetString());
    }

    // QA P2 F3 (Low): PURCHASE-01.4 returns to step 2 with the copy, where Save draft needs only a supplier (01.2). A copied
    // line whose item is no longer In Use keeps its raft (left empty: the item only), so the copy as returned is refused
    // with "Line 1 raft": the raft check needs an item even on a Draft. Fix: on a Draft, check a raft only once the line has
    // an item (or leave the raft empty in the copy when the item is left empty).
    [Fact]
    public async Task A_copy_whose_item_is_no_longer_in_use_can_be_saved_as_a_draft_as_it_is()
    {
        var world = await factory.WorldAsync();
        var oldItem = await world.Owner.CreateItemAsync("Material", "piece");
        var source = await world.CompletedAsync(new LineBody(oldItem, 2, 5_000, RaftId: world.Raft.Id), new LineBody(world.Other, 1, 1_000));
        await (await world.Owner.PostAsync($"/api/farming-input-items/{oldItem}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        var copy = await (await world.Owner.GetAsync($"{Receipts}/{source.Id()}/copy")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Line 1 item"], copy.GetProperty("leftEmpty").EnumerateArray().Select(e => e.GetString()));

        var saved = await world.Owner.PostAsJsonAsync(Receipts, copy.GetProperty("receipt"));

        var draft = await saved.ReadAsync(HttpStatusCode.Created);
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
    }

    // QA P2 F2 (Low): the next PN number is read from the receipts that still exist, so removing the latest Draft gives its
    // code to the next receipt. The audit log (searched by record code, ACCOUNT-10) then holds two records under one code,
    // and a code once shown on the screen (and maybe written down) points to another receipt. Fix: never reuse a number (a
    // SQL sequence, or the highest code ever given, including those of removed drafts in the audit log).
    [Fact]
    public async Task Removing_the_latest_draft_never_gives_its_code_to_the_next_receipt()
    {
        var world = await factory.WorldAsync();
        var removed = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));
        Assert.Equal(HttpStatusCode.NoContent, (await world.Owner.DeleteAsync($"{Receipts}/{removed.Id()}")).StatusCode);

        var next = await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));

        Assert.NotEqual(removed.GetProperty("code").GetString(), next.GetProperty("code").GetString());
    }

    private async Task<HttpClient> LoginAsAsync(SubjectType subjectType)
    {
        var password = NewPassword();
        return await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), password, subjectType)).Username, password);
    }

    private Task<int> StatusAsync(Guid receiptId) =>
        factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == receiptId).Select(r => r.StatusId).SingleAsync());
}
