using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// NURSERY-01 and NURSERY-02 (ươm giống, decision of 12/11/2026): a nursery takes ONE seed lot out of the stock for a zone, with 1 to 3 photos; only a Nursing batch is edited or cancelled; a Zone Manager's
/// change tells the Owner. Each test starts its own host, as it counts files and notices.
/// </summary>
public sealed class NurseryStartEditCancelTests
{
    [Fact]
    public async Task Starting_a_nursery_takes_the_pieces_out_of_the_stock_with_a_linked_zone_issue_and_keeps_the_photo()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();

        var batch = await world.Owner.Client.StartNurseryAsync(world.ZoneId, world.Lot, "4000");

        Assert.StartsWith("NU", batch.Text("code"));
        Assert.Equal((StatusIds.NurseryBatch.Nursing, "Nursing", "Đang ươm"), (batch.GetProperty("statusId").GetInt32(), batch.Text("status"), batch.Text("statusLabel")));
        Assert.Equal((4000m, 6000m, world.Lot.Code, Today), (batch.Pieces("quantityIn"), batch.Pieces("seedLotOnHand"), batch.GetProperty("seedLot").Text("code"), batch.Date("startDate")));
        Assert.True(batch.Flag("canEdit") && batch.Flag("canComplete") && !batch.Flag("canCorrectSurvivors"));
        Assert.Single(batch.GetProperty("startPhotos").EnumerateArray());
        Assert.Empty(batch.GetProperty("completionPhotos").EnumerateArray());

        // One Completed use issue for the zone and no raft, linked both ways; the lot is short of exactly those pieces.
        var issue = await api.QueryAsync(db => db.InputIssues.Include(i => i.Lines).SingleAsync(i => i.SourceRecordId == batch.Id()));
        Assert.Equal((InputIssueKind.Use, InputIssueSource.Nursery, (Guid?)world.ZoneId, (Guid?)null, StatusIds.InputIssue.Completed, (Guid?)InputIssueTypeIds.Nursery),
            (issue.Kind, issue.SourceKind, issue.ZoneId, issue.RaftId, issue.StatusId, issue.IssueTypeId));
        Assert.Equal((world.Lot.ItemId, world.Lot.LineId, 4000m), (issue.Lines.Single().ItemId, issue.Lines.Single().SeedLotLineId, issue.Lines.Single().Quantity));
        Assert.Equal((issue.Id, issue.Code), (batch.GetProperty("issueId").GetGuid(), batch.Text("issueCode")));

        // The photo is stored under its own category and the orphan clean-up knows its column.
        var key = await api.QueryAsync(db => db.NurseryBatchPhotos.Where(p => p.NurseryBatchId == batch.Id()).Select(p => p.FileKey).SingleAsync());
        Assert.StartsWith($"{FileCategories.NurseryStartPhotos}/", key);
        Assert.Single(api.StoredFiles(FileCategories.NurseryStartPhotos));
        Assert.Contains(FileCategories.NurseryStartPhotos, OrphanFileCleanupService.Categories);
        Assert.Contains(FileCategories.NurseryCompletionPhotos, OrphanFileCleanupService.Categories);
        using (var scope = api.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IStoredFileReferenceQuery>().IsReferencedAsync(key, default));
        }

        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == batch.Id() && e.Action == "NurseryBatch.Created"));
        Assert.Equal((batch.Text("code"), world.ZoneId), (audit.RecordCode, audit.ZoneId));
    }

    [Fact]
    public async Task A_start_refuses_missing_or_too_many_photos_a_bad_quantity_and_a_future_date_and_saves_nothing()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync(1000m);
        var client = world.Owner.Client;
        var fields = new NurseryFields { SeedLotId = world.Lot.LineId };

        Assert.Equal(["Photos"], await (await client.PostNurseryAsync(world.ZoneId, fields with { Photos = [] })).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PostNurseryAsync(world.ZoneId, fields with { Photos = [Jpeg, Jpeg, Png, Jpeg] })).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PostNurseryAsync(world.ZoneId, fields with { Photos = [TextPretendingToBeAFile] })).InvalidFieldsAsync());
        Assert.Equal(["Quantity", "Start date"], await (await client.PostNurseryAsync(world.ZoneId, fields with { Quantity = "0", StartDate = Today.AddDays(1).ToString("yyyy-MM-dd") })).InvalidFieldsAsync());
        Assert.Equal(["Seed lot"], await (await client.PostNurseryAsync(world.ZoneId, fields with { SeedLotId = null })).InvalidFieldsAsync());
        Assert.Equal(["Quantity"], await (await client.PostNurseryAsync(world.ZoneId, fields with { Quantity = "12,5" })).InvalidFieldsAsync());

        Assert.Empty(await api.NurseryBatchesAsync(world.ZoneId));
        Assert.Equal(0, await api.QueryAsync(db => db.InputIssues.CountAsync(i => i.SourceKind == InputIssueSource.Nursery)));
        Assert.Empty(api.StoredFiles(FileCategories.NurseryStartPhotos));
    }

    // Owner decision of 12/11/2026: no negative stock for a nursery, even when the cooperative allows one (the default does).
    [Fact]
    public async Task A_quantity_above_what_is_on_hand_is_refused_with_msg_input_01_and_leaves_no_file()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync(1000m);
        var client = world.Owner.Client;
        await client.StartNurseryAsync(world.ZoneId, world.Lot, "600");

        var refused = await (await client.PostNurseryAsync(world.ZoneId, new NurseryFields { SeedLotId = world.Lot.LineId, Quantity = "401" })).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-INPUT-01", 400m, -1m, "Quantity"), (refused.Text("code"), refused.Pieces("onHand"), refused.Pieces("balanceAfter"), refused.Text("field")));
        Assert.Single(await api.NurseryBatchesAsync(world.ZoneId));
        Assert.Single(api.StoredFiles(FileCategories.NurseryStartPhotos));

        // Exactly what is left is fine and the lot is then empty.
        var rest = await client.StartNurseryAsync(world.ZoneId, world.Lot, "400");
        Assert.Equal(0m, rest.Pieces("seedLotOnHand"));
    }

    [Fact]
    public async Task A_zone_manager_starts_a_nursery_in_the_zone_and_tells_the_owner_but_the_owner_tells_nobody()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var manager = await api.ManagerAsync(world.ZoneId);

        var byManager = await manager.Client.StartNurseryAsync(world.ZoneId, world.Lot);
        var byOwner = await world.Owner.Client.StartNurseryAsync(world.ZoneId, world.Lot);

        var notice = Assert.Single(await api.NurseryNoticesAsync(byManager.Id()), n => n.RecipientAccountId == world.Owner.Account.Id);
        Assert.Equal(("MINI_APP_NOTIFICATION", "NurseryBatch", byManager.Text("code")), (notice.MessageCode, notice.RelatedRecordType, notice.RelatedRecordCode));
        Assert.DoesNotContain(await api.NurseryNoticesAsync(byOwner.Id()), n => n.RecipientAccountId == world.Owner.Account.Id);
        Assert.Equal(manager.Account.Id, byManager.GetProperty("recordedByAccountId").GetGuid());
    }

    [Fact]
    public async Task A_zone_manager_of_another_zone_and_an_accountant_cannot_start_read_or_list_and_an_unknown_zone_is_404()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var batch = await world.Owner.Client.StartNurseryAsync(world.ZoneId, world.Lot);
        var otherZone = (await world.Owner.Client.CreateZoneAsync()).Id();
        var outsider = await api.ManagerAsync(otherZone);
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);

        foreach (var client in new[] { outsider.Client, accountant.Client })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostNurseryAsync(world.ZoneId, new NurseryFields { SeedLotId = world.Lot.LineId })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/nursery-batches/{batch.Id()}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/nursery-batches/{batch.Id()}/history")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/nursery-batches?zoneId={world.ZoneId}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await world.Owner.Client.PostNurseryAsync(Guid.NewGuid(), new NurseryFields { SeedLotId = world.Lot.LineId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Owner.Client.GetAsync($"/api/nursery-batches/{Guid.NewGuid()}")).StatusCode);
        Assert.Single(await api.NurseryBatchesAsync(world.ZoneId));
    }

    [Fact]
    public async Task The_list_filters_by_zone_status_and_search_and_totals_the_whole_selection()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var first = await client.StartNurseryAsync(world.ZoneId, world.Lot, "3000");
        var second = await client.StartNurseryAsync(world.ZoneId, world.Lot, "2000");
        await (await client.CancelNurseryAsync(api, second.Id(), "Nhập nhầm")).ReadAsync(HttpStatusCode.OK);
        await client.CompleteNurseryAsync(api, first.Id(), "2700");

        var all = await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneId={world.ZoneId}");
        var completed = await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneId={world.ZoneId}&status=completed");
        var byCode = await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneId={world.ZoneId}&search={second.Text("code")}");

        Assert.Equal(2, all.GetProperty("total").GetInt32());
        var summary = all.GetProperty("summary");
        // A cancelled batch counts in no quantity; the loss of the completed one is an estimate.
        Assert.Equal((3000m, 0m, 3000m, 2700m, 300m, 10.0m, 0, 1), (summary.Pieces("quantityIn"), summary.Pieces("nursingQuantityIn"), summary.Pieces("completedQuantityIn"), summary.Pieces("survivors"),
            summary.Pieces("lossPieces"), summary.Pieces("lossPercent"), summary.GetProperty("nursingCount").GetInt32(), summary.GetProperty("completedCount").GetInt32()));
        var item = Assert.Single(completed.GetProperty("items").EnumerateArray());
        Assert.Equal((first.Id(), 2700m, 300m, 10.0m, true, 0m, 2700m), (item.Id(), item.Pieces("survivors"), item.Pieces("lossPieces"), item.Pieces("lossPercent"), item.Flag("estimated"), item.Pieces("seeded"), item.Pieces("remaining")));
        Assert.Equal(second.Id(), Assert.Single(byCode.GetProperty("items").EnumerateArray()).Id());

        var lots = await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches/seed-lots?zoneId={world.ZoneId}");
        Assert.Equal((world.Lot.LineId, 7000m), (lots.GetProperty("lots")[0].Id(), lots.GetProperty("lots")[0].Pieces("onHand"))); // 10 000 less the 3 000 of the batch that stays
    }

    [Fact]
    public async Task Editing_a_nursing_batch_changes_the_pieces_the_issue_and_the_photos_and_keeps_one_to_three()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");
        var version = await api.IssueRowVersionAsync<NurseryBatch>(batch.Id());
        var first = batch.GetProperty("startPhotos")[0].Id();

        var edited = (await (await client.PutNurseryAsync(batch.Id(), new NurseryFields { Quantity = "2500", Note = "Lô tốt", RowVersion = version, Photos = [] })).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((2500m, 7500m, "Lô tốt"), (edited.Pieces("quantityIn"), edited.Pieces("seedLotOnHand"), edited.Text("note")));
        Assert.Equal(first, Assert.Single(edited.GetProperty("startPhotos").EnumerateArray()).Id()); // no photo field: the photos stay
        var issue = await api.QueryAsync(db => db.InputIssues.Include(i => i.Lines).SingleAsync(i => i.SourceRecordId == batch.Id() && i.StatusId == StatusIds.InputIssue.Completed));
        Assert.Equal(2500m, issue.Lines.Single().Quantity);
        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == batch.Id() && e.Action == "NurseryBatch.Edited"));
        Assert.Contains("2500", audit.After!);
        Assert.Contains("1000", audit.Before!);

        // Keep the photo and add one: two. Send only a new one: the old one goes. Four or none: refused.
        var two = (await (await client.PutNurseryAsync(batch.Id(), new NurseryFields { RowVersion = version, KeepPhotoIds = [first], Photos = [Png], Quantity = null })).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");
        Assert.Equal(2, two.GetProperty("startPhotos").GetArrayLength());
        var replaced = (await (await client.PutNurseryAsync(batch.Id(), new NurseryFields { RowVersion = version, Photos = [Jpeg], Quantity = null })).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");
        Assert.Equal(1, replaced.GetProperty("startPhotos").GetArrayLength());
        Assert.NotEqual(first, replaced.GetProperty("startPhotos")[0].Id());
        Assert.Equal(["Photos"], await (await client.PutNurseryAsync(batch.Id(), new NurseryFields { RowVersion = version, Photos = [Jpeg, Jpeg, Jpeg, Jpeg], Quantity = null })).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PutNurseryAsync(batch.Id(), new NurseryFields { RowVersion = version, KeepPhotoIds = [Guid.NewGuid()], Photos = [], Quantity = null })).InvalidFieldsAsync());
        // Above what is on hand (7500 + the 2500 the batch holds = 10 000): refused, the batch stays as it was.
        var tooMany = await (await client.PutNurseryAsync(batch.Id(), new NurseryFields { RowVersion = version, Quantity = "10001", Photos = [] })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-INPUT-01", tooMany.Text("code"));
        Assert.Equal(2500m, (await client.GetNurseryAsync(batch.Id())).Pieces("quantityIn"));
        Assert.Equal(1, (await client.GetNurseryAsync(batch.Id())).GetProperty("startPhotos").GetArrayLength());
    }

    [Fact]
    public async Task Cancelling_a_nursing_batch_needs_a_reason_returns_the_pieces_and_cancels_the_issue()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "4000");

        Assert.Equal(["Reason"], await (await client.CancelNurseryAsync(api, batch.Id(), " ")).InvalidFieldsAsync());
        var cancelled = (await (await client.CancelNurseryAsync(api, batch.Id(), "Nhập nhầm lô")).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((StatusIds.NurseryBatch.Cancelled, "Đã hủy", "Nhập nhầm lô", 10000m), (cancelled.GetProperty("statusId").GetInt32(), cancelled.Text("statusLabel"), cancelled.Text("cancelReason"), cancelled.Pieces("seedLotOnHand")));
        Assert.False(cancelled.Flag("canEdit"));
        Assert.Equal(StatusIds.InputIssue.Cancelled, await api.QueryAsync(db => db.InputIssues.Where(i => i.SourceRecordId == batch.Id()).Select(i => i.StatusId).SingleAsync()));
        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == batch.Id() && e.Action == "NurseryBatch.Cancelled"));
        Assert.Equal("Nhập nhầm lô", audit.Reason);

        // Nothing more can be done with a cancelled batch.
        var problem = await (await client.CancelNurseryAsync(api, batch.Id(), "Again")).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_NOT_NURSING", problem.Text("reasonCode"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutNurseryAsync(batch.Id(), new NurseryFields { RowVersion = await api.IssueRowVersionAsync<NurseryBatch>(batch.Id()), Quantity = "1" })).StatusCode);
    }

    // Owner decision of 12/11/2026: a Completed ("đã ươm xong") nursery cannot be cancelled, nor edited.
    [Fact]
    public async Task A_completed_batch_is_neither_cancelled_nor_edited_and_the_pieces_stay_out_of_the_stock()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "4000");
        await client.CompleteNurseryAsync(api, batch.Id(), "3800");

        var cancel = await (await client.CancelNurseryAsync(api, batch.Id(), "Muốn hủy")).ReadAsync(HttpStatusCode.Conflict);
        var edit = await (await client.PutNurseryAsync(batch.Id(), new NurseryFields { RowVersion = await api.VersionOfAsync(batch.Id()), Quantity = "100" })).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("NURSERY_NOT_NURSING", "NURSERY_NOT_NURSING", "MSG-GEN-09"), (cancel.Text("reasonCode"), edit.Text("reasonCode"), cancel.Text("code")));
        var current = await client.GetNurseryAsync(batch.Id());
        Assert.Equal((StatusIds.NurseryBatch.Completed, 6000m), (current.GetProperty("statusId").GetInt32(), current.Pieces("seedLotOnHand")));
        Assert.Equal(StatusIds.InputIssue.Completed, await api.QueryAsync(db => db.InputIssues.Where(i => i.SourceRecordId == batch.Id()).Select(i => i.StatusId).SingleAsync()));
    }
}
