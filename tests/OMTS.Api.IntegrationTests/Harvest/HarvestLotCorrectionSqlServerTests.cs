using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Common;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-01.1 on SQL Server: real row versions, and parallel saves. A race answers 2xx or 409, or 404 when the lot is already gone, never a 500. Run these alone as well as in a group.</summary>
public class HarvestLotCorrectionSqlServerTests
{
    private const int Rounds = 4;

    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        return (host, host.Owner.Client, (await host.Owner.Client.CreateZoneAsync()).Id());
    }

    private static async Task<(Guid RaftId, Guid CycleId, SeedLotMade Lot)> SeededAsync(HttpClient owner, Guid zoneId, string? raftCode = null)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody(raftCode))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var lot = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, lot.Session([1, 2, 3, 4, 5]));
        return (raftId, cycleId, lot);
    }

    private static async Task AssertAnsweredAsync(params HttpResponseMessage[] answers)
    {
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.IsSuccessStatusCode || answer.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound, $"{(int)answer.StatusCode}: {text}");
        }
    }

    private static HarvestCorrection Correction(JsonElement lot, HarvestFields fields) => new(fields, "Wrong count", lot.Text("rowVersion"), lot.PhotoKeysOfLot());

    private static async Task AssertNoFileLeftBehindAsync(FarmingSqlServerHost host)
    {
        var inDatabase = await host.QueryAsync(db => db.HarvestLotPhotos.Select(p => p.FileKey).OrderBy(k => k).ToListAsync());
        Assert.Equal(inDatabase, host.StoredKeys(FileCategories.HarvestLotPhotos));
    }

    [SqlServerFact]
    public async Task A_correction_moves_the_version_of_the_lot_and_the_cycle_and_the_form_read_before_it_is_stale()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        var saved = await owner.RecordHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var lot = saved.GetProperty("lot");

        var corrected = await (await owner.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1, 2)))).ReadAsync(HttpStatusCode.OK);

        Assert.NotEqual(lot.Text("rowVersion"), corrected.GetProperty("lot").Text("rowVersion"));
        Assert.NotEqual(saved.Text("cycleRowVersion"), corrected.Text("cycleRowVersion"));
        var stale = await (await owner.PutHarvestLotAsync(lot.Id(), Correction(lot, new HarvestFields { Cages = "9", Photos = [Png] }))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG06", corrected.GetProperty("lot").Text("rowVersion")), (stale.Text("code"), stale.Text("currentRowVersion")));
        Assert.Equal("MSG06", await (await owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"))).ErrorCodeAsync());
        Assert.Equal("MSG06", await (await owner.PostSeedingCompleteAsync(raft.CycleId, saved.Text("cycleRowVersion"))).ErrorCodeAsync());

        // A change of the lanes alone gives the lot a new version too: a photo change the same.
        var again = corrected.GetProperty("lot");
        var noPhotos = await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(1, 2), "No photo", again.Text("rowVersion")))).ReadAsync(HttpStatusCode.OK);
        Assert.NotEqual(again.Text("rowVersion"), noPhotos.GetProperty("lot").Text("rowVersion"));
        await AssertNoFileLeftBehindAsync(host);

        var removal = await owner.PostRemoveLotAsync(lot.Id(), "mistake", noPhotos.GetProperty("lot").Text("rowVersion"));
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
        Assert.Equal(0, await host.QueryAsync(db => db.HarvestLotLanes.CountAsync(l => l.LotId == lot.Id())));
    }

    [SqlServerFact]
    public async Task Parallel_corrections_of_one_lot_save_one_and_the_rest_answer_409()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = [(round % 5 + 1).ToString()], Photos = [Jpeg] });
            var lanes = lot.Numbers("laneNumbers").Select(n => n.ToString()).ToList();

            var answers = await Task.WhenAll(Enumerable.Range(2, 5).Select(cages =>
                owner.PutHarvestLotAsync(lot.Id(), Correction(lot, new HarvestFields { Lanes = lanes, Cages = cages.ToString(), Photos = [Png] }))));

            await AssertAnsweredAsync(answers);
            Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
            var cagesSaved = await host.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot.Id()).Select(l => l.CageCount).SingleAsync());
            Assert.InRange(cagesSaved, 2, 6);
            Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Corrected" && e.RecordId == lot.Id())));
            await AssertNoFileLeftBehindAsync(host);
        }
    }

    [SqlServerFact]
    public async Task A_correction_against_a_removal_of_one_lot_never_answers_500_and_leaves_one_or_the_other_whole()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);
            var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });

            var answers = await Task.WhenAll(
                owner.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1, 2).FinishedLanes(2) with { Photos = [Png] })),
                owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion")),
                owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion")));

            await AssertAnsweredAsync(answers);
            var left = await host.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot.Id()).Select(l => new { l.CageCount, Lanes = l.Lanes.Count, Photos = l.Photos.Count }).SingleOrDefaultAsync());
            var removedRows = await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Removed" && e.RecordId == lot.Id()));
            Assert.Equal(left is null ? 1 : 0, removedRows);
            if (left is null)
            {
                Assert.Equal(0, await host.QueryAsync(db => db.HarvestLotLanes.CountAsync(l => l.LotId == lot.Id())));
            }

            await AssertNoFileLeftBehindAsync(host);
        }
    }

    // The lane states depend on the cycle row: of a lot and a removal, or a correction, in one moment, one fails with MSG06 and is checked against the new states when sent again.
    [SqlServerFact]
    public async Task A_new_lot_against_a_removal_and_a_correction_of_the_cycle_never_answers_500_and_the_lanes_add_up()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);
            var first = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
            var second = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));

            var answers = await Task.WhenAll(
                manager.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(3)),
                manager.Client.PostRemoveLotAsync(first.Id(), "mistake", first.Text("rowVersion")),
                manager.Client.PutHarvestLotAsync(second.Id(), Correction(second, HarvestFields.OfLanes(2, 4))));

            await AssertAnsweredAsync(answers);
            Assert.True(answers.Any(a => a.IsSuccessStatusCode), "One of the saves must stand.");
            var screen = await manager.Client.HarvestScreenAsync(raft.RaftId);
            var lanesInLots = screen.GetProperty("lots").EnumerateArray().SelectMany(l => l.Numbers("laneNumbers")).ToHashSet();
            foreach (var lane in new[] { 1, 2, 3, 4, 5 })
            {
                Assert.Equal(lanesInLots.Contains(lane) ? "BeingHarvested" : "Growing", screen.HarvestStateOf(lane));
            }
        }
    }

    // SALE-05 marks each lot it sells changed (EnsureForSale, CloseSoldWholeLot), and the row version of the lot makes one side fail: an invoice always keeps its lots.
    [SqlServerFact]
    public async Task A_sale_or_close_held_against_a_removal_of_the_lot_loses_on_the_row_version_and_answers_409()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        var lot = await owner.HarvestLotAsync(raft.RaftId);

        using var scope = host.Api.Services.CreateScope();
        var lots = scope.ServiceProvider.GetRequiredService<IHarvestLotRepository>();
        var status = scope.ServiceProvider.GetRequiredService<HarvestLotStatusService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var read = await lots.GetForEditAsync(lot.Id(), default);
        Assert.Null(status.CloseSoldWholeLot(read!, Today, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"))).StatusCode);

        await Assert.ThrowsAsync<OMTS.Application.Common.Exceptions.ConcurrencyConflictException>(() => unitOfWork.SaveChangesAsync());
    }

    // BR-CYCLE-07 on a real database: of two parallel corrections finishing the last lanes, the second to save sees the first, so the reminder goes once.
    [SqlServerFact]
    public async Task Parallel_corrections_finishing_the_last_lanes_send_the_reminder_at_most_once_per_arming()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);
            await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2, 3).FinishedLanes(1, 2, 3));
            var fourth = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(4));
            var fifth = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(5));

            var answers = await Task.WhenAll(
                manager.Client.PutHarvestLotAsync(fourth.Id(), Correction(fourth, HarvestFields.OfLanes(4).FinishedLanes(4))),
                manager.Client.PutHarvestLotAsync(fifth.Id(), Correction(fifth, HarvestFields.OfLanes(5).FinishedLanes(5))));

            await AssertAnsweredAsync(answers);
            foreach (var (answer, lot, lane) in new[] { (answers[0], fourth, 4), (answers[1], fifth, 5) })
            {
                if (answer.StatusCode != HttpStatusCode.OK)
                {
                    var current = await manager.Client.GetHarvestLotAsync(lot.Id());
                    await (await manager.Client.PutHarvestLotAsync(lot.Id(), Correction(current, HarvestFields.OfLanes(lane).FinishedLanes(lane)))).ReadAsync(HttpStatusCode.OK);
                }
            }

            Assert.Equal(1, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "CYCLE_CLOSE_REMINDER" && n.RelatedRecordId == raft.CycleId && n.RecipientAccountId == manager.Account.Id)));
        }
    }

    [SqlServerFact]
    public async Task The_for_sale_list_and_count_read_in_parallel_with_lots_and_removals_never_fail()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var answers = await Task.WhenAll(
            owner.GetAsync("/api/harvest-lots/for-sale"),
            owner.GetAsync($"/api/harvest-lots/for-sale?zoneId={zoneId}"),
            owner.GetAsync("/api/harvest-lots/for-sale/count"),
            owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(2)),
            owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion")));

        await AssertAnsweredAsync(answers);
        var count = await owner.ForSaleCountAsync(zoneId);
        Assert.Equal(await host.QueryAsync(db => db.HarvestLots.CountAsync()), count);
        Assert.Equal(count, (await owner.LotsForSaleAsync(zoneId)).ForSaleCodes().Count);
    }
}
