using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Files;
using OMTS.Domain.Common;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// A removed lot code is not given again, on a real database and at the worst moment. The unique index on the <c>HarvestLot.Created</c> audit rows refuses a save that read the codes before another raft took
/// the code and removed it again; rafts that share a code share no cycle row, so nothing else would stop it. Run these alone as well as in a group.
/// </summary>
public class HarvestLotCodeRaceSqlServerTests
{
    private const int Rounds = 4;

    private static async Task<(Guid RaftId, Guid CycleId)> SeededAsync(HttpClient owner, Guid zoneId, string raftCode)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody(raftCode))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var lot = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, lot.Session([1, 2, 3, 4, 5]));
        return (raftId, cycleId);
    }

    /// <summary>The API over the same database, whose first save stops until released.</summary>
    private static async Task<(WebApplicationFactory<Program> Api, HttpClient Client, SaveHold Hold)> HeldAsync(FarmingSqlServerHost host, string username)
    {
        var hold = new SaveHold();
        var api = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<UnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new HeldUnitOfWork(provider.GetRequiredService<UnitOfWork>(), hold));
        }));
        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(host.Api.CreateClient(), username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (api, client, hold);
    }

    private sealed class HeldUnitOfWork(IUnitOfWork inner, SaveHold hold) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await hold.PassAsync();
            return await inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
    }

    private static async Task AssertAnsweredAsync(IEnumerable<HttpResponseMessage> answers)
    {
        foreach (var answer in answers)
        {
            Assert.True(answer.IsSuccessStatusCode || answer.StatusCode == HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
        }
    }

    private static async Task AssertEachCodeGivenOnceAsync(FarmingSqlServerHost host)
    {
        var given = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "HarvestLot.Created").Select(e => e.RecordCode!).ToListAsync());
        Assert.Equal(given.Count, given.Distinct().Count());
        var inUse = await host.QueryAsync(db => db.HarvestLots.Select(l => l.LotCode).ToListAsync());
        var removed = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "HarvestLot.Removed").Select(e => e.RecordCode!).ToListAsync());
        Assert.Empty(inUse.Intersect(removed));
    }

    [SqlServerFact]
    public async Task Removing_the_top_lot_does_not_free_its_code_on_a_real_database()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV71"))).Id();
        var raft = await SeededAsync(owner, zoneId, "R1");
        var first = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var second = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        Assert.Equal($"{first.Text("lotCode")}-2", second.Text("lotCode"));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(second.Id(), "mistake", second.Text("rowVersion"))).StatusCode);

        var third = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        Assert.Equal($"{first.Text("lotCode")}-3", third.Text("lotCode"));
    }

    // The worst moment: this save read the codes and stopped; meanwhile the other raft took the code and removed the lot. Only the index can stop the code coming back; the loser takes the next one, with its photo.
    [SqlServerFact]
    public async Task A_lot_held_at_its_save_cannot_take_a_code_another_raft_gave_and_removed_meanwhile()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV72"))).Id();
        var manager = await host.StaffInZoneAsync(zoneId);
        var plain = await SeededAsync(owner, zoneId, "R1");
        var prefixed = await SeededAsync(owner, zoneId, "KV72-R1");
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _ = api;

        var pending = held.PostHarvestAsync(plain.RaftId, new HarvestFields { Photos = [Jpeg] });
        await hold.Reached;
        var between = await owner.HarvestLotAsync(prefixed.RaftId, HarvestFields.OfLanes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(between.Id(), "mistake", between.Text("rowVersion"))).StatusCode);
        hold.Release();

        var saved = (await (await pending).ReadAsync(HttpStatusCode.Created)).GetProperty("lot");
        Assert.Equal($"{between.Text("lotCode")}-2", saved.Text("lotCode"));
        Assert.Single(saved.PhotoKeysOfLot());
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Created" && e.RecordCode == saved.Text("lotCode"))));
        Assert.Equal(saved.PhotoKeysOfLot(), host.StoredKeys(FileCategories.HarvestLotPhotos));
        Assert.Single(await host.QueryAsync(db => db.Notifications.Where(n => n.Type == "HARVEST_LOT_RECORDED" && n.RelatedRecordId == saved.Id()).ToListAsync()));
    }

    // A removal held at its save while a lot of the same raft is recorded: the cycle row makes the removal fail, so the lot it would have left stands.
    [SqlServerFact]
    public async Task A_removal_held_at_its_save_against_a_new_lot_answers_409_and_the_new_lot_took_the_next_code()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV75"))).Id();
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId, "R1");
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _ = api;

        var pending = held.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"));
        await hold.Reached;
        var next = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Equal($"{lot.Text("lotCode")}-2", next.Text("lotCode"));
        Assert.Equal(2, await host.QueryAsync(db => db.HarvestLots.CountAsync(l => l.RaftId == raft.RaftId)));
        Assert.Equal(0, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Removed")));
    }

    // A record against a removal of the same raft: whichever way the cycle row goes, no code is seen twice.
    [SqlServerFact]
    public async Task Parallel_records_and_removals_never_give_a_code_twice()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV73"))).Id();
        var raft = await SeededAsync(owner, zoneId, "R1");
        var top = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        for (var round = 0; round < Rounds * 2; round++)
        {
            await AssertAnsweredAsync(await Task.WhenAll(
                owner.PostRemoveLotAsync(top.Id(), "mistake", top.Text("rowVersion")),
                owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1)),
                owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(2))));

            top = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(3));
        }

        await AssertEachCodeGivenOnceAsync(host);
    }

    // Two rafts that share a code, one removing as the other records: the loser moves on, nobody gets an error for the code.
    [SqlServerFact]
    public async Task Two_rafts_that_share_a_code_racing_with_a_removal_answer_2xx_or_409_and_give_each_code_once()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV74"))).Id();
        for (var round = 0; round < Rounds; round++)
        {
            var plain = await SeededAsync(owner, zoneId, $"R{round}");
            var prefixed = await SeededAsync(owner, zoneId, $"KV74-R{round}");
            var removable = await owner.HarvestLotAsync(plain.RaftId, HarvestFields.OfLanes(1));

            await AssertAnsweredAsync(await Task.WhenAll(
                owner.PostRemoveLotAsync(removable.Id(), "mistake", removable.Text("rowVersion")),
                owner.PostHarvestAsync(prefixed.RaftId, HarvestFields.OfLanes(1)),
                owner.PostHarvestAsync(prefixed.RaftId, HarvestFields.OfLanes(2)),
                owner.PostHarvestAsync(plain.RaftId, HarvestFields.OfLanes(2))));
        }

        await AssertEachCodeGivenOnceAsync(host);
    }
}
