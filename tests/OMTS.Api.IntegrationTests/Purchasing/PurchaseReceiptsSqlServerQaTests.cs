using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PR P2 (OMTS-218) on a real SQL Server: a Draft removed while it is completed, the Owner's correction while a return
/// is saved (seeded + returned never above the quantity, BR-PURCHASE-09), parallel cancels of one paid receipt (one
/// cancellation, one detachment, one advance notice), a burst of creates above the ten code attempts, and the migration
/// PurchaseReceiptsV2 down and up with incomplete Draft lines, a cancelled seed line and amounts in dongs.
/// </summary>
public class PurchaseReceiptsSqlServerQaTests
{
    private const string Receipts = "/api/purchase-receipts";

    [SqlServerFact]
    public async Task Parallel_remove_and_complete_of_one_draft_leave_nothing_or_a_completed_receipt_with_its_lines_and_notifications()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var author = await host.ZoneManagerAsync(world.Zone.Id);
        var owners = await host.QueryAsync(db => db.Accounts.CountAsync(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active));

        for (var round = 0; round < 4; round++)
        {
            var draft = await author.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 10, 1_000), new LineBody(world.Other, 1, 1_000)));
            var responses = await InParallelAsync(2, i => i == 0 ? author.CompleteAsync(draft.Id()) : world.Owner.DeleteAsync($"{Receipts}/{draft.Id()}"));

            Assert.All(responses, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
            var status = await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == draft.Id()).Select(r => (int?)r.StatusId).SingleOrDefaultAsync());
            var lines = await host.QueryAsync(db => db.PurchaseReceiptLines.CountAsync(l => l.ReceiptId == draft.Id()));
            var notified = await host.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == draft.Id()));
            if (status is null)
            {
                // Removed: no line and no notification left behind.
                Assert.Equal((HttpStatusCode.NoContent, 0, 0), (responses[1].StatusCode, lines, notified));
            }
            else
            {
                Assert.Equal((StatusIds.PurchaseReceipt.Completed, HttpStatusCode.OK, 2, owners), (status.Value, responses[0].StatusCode, lines, notified));
            }
        }
    }

    [SqlServerFact]
    public async Task Parallel_correction_and_return_never_leave_more_seeded_and_returned_than_received()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        // One open cycle per raft: every round seeds into it.
        var cycle = new CultivationCycle { RaftId = world.Raft.Id, StartDate = Today };
        await host.UseDbAsync(db => db.CultivationCycles.Add(cycle));

        for (var round = 0; round < 4; round++)
        {
            var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 10_000));
            var line = receipt.LineOf(world.Seed);
            await host.UseDbAsync(db => db.SeedingRecords.Add(new SeedingRecord
            {
                CycleId = cycle.Id, SeedLotLineId = line, SeedingDate = Today, Quantity = 30, RecordedByAccountId = host.Owner.Account.Id,
            }));
            await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 10)] })).ReadAsync(HttpStatusCode.Created);

            // The correction lowers the line to exactly seeded + returned; the return takes 20 more.
            var responses = await InParallelAsync(2, i => i == 0
                ? world.Owner.PostAsJsonAsync($"{Receipts}/{receipt.Id()}/correct", new { reason = "Counted again", lines = new[] { new { lineId = line, quantity = 40 } } })
                : world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 20)] }));

            Assert.All(responses, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
            var quantity = await host.QueryAsync(db => db.PurchaseReceiptLines.Where(l => l.Id == line).Select(l => l.Quantity!.Value).SingleAsync());
            var returned = await host.QueryAsync(db => db.PurchaseReturnLines.Where(l => l.ReceiptLineId == line).SumAsync(l => l.Quantity));
            Assert.True(30 + returned <= quantity, $"round {round}: 30 seeded + {returned} returned > {quantity} received ({await CodesAsync(responses)})");
            Assert.Contains(responses, r => r.IsSuccessStatusCode);
        }
    }

    [SqlServerFact]
    public async Task Parallel_cancels_of_one_paid_receipt_cancel_it_once_keep_its_payment_and_notify_once()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.PaidAsync(300_000, new LineBody(world.Other, 5, 100_000));

        var responses = await InParallelAsync(4, _ => world.Owner.CancelAsync(receipt.Id(), "Wrong supplier"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Contains(await loser.CodeOrStatusAsync(), new[] { "409 MSG06", "409 MSG-GEN-09" });
        }

        var actions = await host.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == receipt.Id()).Select(e => e.Action).ToListAsync());
        Assert.Single(actions, a => a == "PurchaseReceipt.Cancelled");
        var recipients = await host.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == receipt.Id() && n.Type == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance)
            .Select(n => n.RecipientAccountId!.Value).ToListAsync());
        Assert.Equal(recipients.Distinct().Count(), recipients.Count);
        Assert.Contains(host.Owner.Account.Id, recipients);
        Assert.Equal(-300_000m, await world.DebtAsync());
    }

    // GBR-04 code race: every create of a burst gets its own PN code from the document sequence.
    [SqlServerFact]
    public async Task A_burst_of_sixteen_parallel_creates_gives_each_receipt_its_own_PN_code()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var manager = await host.ZoneManagerAsync(world.Zone.Id);

        var creates = await InParallelAsync(16, i => (i % 2 == 0 ? world.Owner : manager).PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)) with { Complete = i % 4 == 0 }));

        Assert.True(creates.All(r => r.StatusCode == HttpStatusCode.Created), await CodesAsync(creates));
        var codes = await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.SupplierId == world.SupplierId).Select(r => r.Code).ToListAsync());
        Assert.Equal(16, codes.Distinct().Count());
    }

    // Down is lossy (accepted) but must go through: the line without an item is dropped, empty quantities and prices become
    // 0, amounts in dongs go to thousands, only the seed lines of Completed receipts get a lot; Up again matches the model.
    [SqlServerFact]
    public async Task PurchaseReceiptsV2_goes_down_with_incomplete_draft_lines_a_cancelled_seed_line_and_amounts_in_dongs_and_up_again()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var completed = await world.CompletedAsync(new LineBody(world.Other, 1.5m, 333), new LineBody(world.Seed, 2, 1_000, "L1", world.Raft.Id));
        var cancelled = await world.CompletedAsync(new LineBody(world.Seed, 3, 1_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, null, null), new LineBody(world.Other, 2, null), new LineBody(null, 1, 1_000)));
        var (completedSeed, cancelledSeed, fractional) = (completed.LineOf(world.Seed), cancelled.LineOf(world.Seed), completed.LineOf(world.Other));

        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_PurchaseReceiptsV2", StringComparison.Ordinal)) - 1]);

        Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM PurchaseReceiptLines WHERE ReceiptId = {draft.Id()}").SingleAsync());
        Assert.Equal(0m, await db.Database.SqlQuery<decimal>($"SELECT SUM(Quantity + UnitPrice) AS [Value] FROM PurchaseReceiptLines WHERE ReceiptId = {draft.Id()} AND ItemId = {world.Seed}").SingleAsync());
        Assert.Equal(1_000m, await db.Database.SqlQuery<decimal>($"SELECT LineAmount AS [Value] FROM PurchaseReceiptLines WHERE Id = {fractional}").SingleAsync());
        Assert.Equal([completedSeed], await db.Database.SqlQuery<Guid>($"SELECT PurchaseReceiptLineId AS [Value] FROM SeedReceiptLots WHERE PurchaseReceiptLineId IN ({completedSeed}, {cancelledSeed})").ToListAsync());

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal([1, 2], await db.PurchaseReceiptLines.Where(l => l.ReceiptId == draft.Id()).Select(l => l.LineNumber).OrderBy(n => n).ToListAsync());
    }

    private static async Task<string> CodesAsync(IEnumerable<HttpResponseMessage> responses) =>
        string.Join(", ", await Task.WhenAll(responses.Select(r => r.CodeOrStatusAsync())));
}
