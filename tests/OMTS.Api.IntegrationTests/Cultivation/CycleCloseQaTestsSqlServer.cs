using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Files;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// QA of PR C5 on SQL Server: the close (by a Zone Manager, so the Owner is told) against every parallel write the developer's races leave out (material usage, cultivation log,
/// harvest correction), parallel closes, and each race held at its worst moment. Exactly one side saves, nothing answers 500, nothing is half saved, and MSG-CYCLE-06 is kept
/// and pushed once per saved close and never for a refused one. Run alone, at least twice.
/// </summary>
public class CycleCloseQaTestsSqlServer
{
    private const int Rounds = 6;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, Actor Manager, Guid ZoneId);

    private static async Task<Setup> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var zoneId = (await host.Owner.Client.CreateZoneAsync()).Id();
        return new Setup(host, host.Owner.Client, await host.StaffInZoneAsync(zoneId), zoneId);
    }

    /// <summary>A raft with a cycle started five days ago and seed on lanes 1 and 2 yesterday: closing it is early.</summary>
    private static async Task<(Guid RaftId, Guid CycleId)> SeededAsync(HttpClient owner, Guid zoneId)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)))).Id();
        var lot = await owner.SeedLotAsync(received: Today.AddDays(-5));
        await owner.RecordSeedingAsync(cycleId, lot.Session([1, 2]));
        return (raftId, cycleId);
    }

    private static async Task<bool> SucceededAsync(HttpResponseMessage answer, HttpStatusCode success)
    {
        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.StatusCode == success || answer.StatusCode == HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {text}");
        return answer.StatusCode == success;
    }

    private static Task<HttpResponseMessage> CloseAsync(HttpClient client, Guid cycleId, string version) =>
        client.PostCloseAsync(cycleId, CycleCloseTestKit.Assessment, version, ownerApproved: true);

    private static async Task<string> VersionAsync(HttpClient client, Guid cycleId) => (await client.ClosePreviewAsync(cycleId)).Text("cycleRowVersion");

    private static Task<int> NoticesAsync(FarmingSqlServerHost host, Guid cycleId) =>
        host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == CycleCloseTestKit.ClosedType && n.RelatedRecordId == cycleId));

    private static Task<int> StatusAsync(FarmingSqlServerHost host, Guid cycleId) =>
        host.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == cycleId).Select(c => c.StatusId).SingleAsync());

    private static int Pushes(IServiceProvider services, Guid accountId) =>
        services.GetRequiredService<FakePushSender>().Inbox.List(accountId.ToString()).Count(m => m.MessageCode == "MSG-CYCLE-06");

    /// <summary>Checks one round of a race: one side saved, the cycle and the notice agree with the close.</summary>
    private static async Task AssertOneWonAsync(Setup s, Guid cycleId, int round, bool written, bool closed)
    {
        Assert.True(written ^ closed, $"Round {round}: write saved {written}, close saved {closed}.");
        Assert.Equal(closed ? StatusIds.CultivationCycle.Closed : StatusIds.CultivationCycle.Open, await StatusAsync(s.Host, cycleId));
        Assert.Equal(closed ? 1 : 0, await NoticesAsync(s.Host, cycleId));
    }

    [SqlServerFact]
    public async Task A_close_racing_a_material_usage_of_the_close_day_lets_exactly_one_save()
    {
        var s = await StartAsync();
        await using var _ = s.Host;
        var item = await s.Host.ItemAsync();
        var wins = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var (raftId, cycleId) = await SeededAsync(s.Owner, s.ZoneId);
            var version = await VersionAsync(s.Manager.Client, cycleId);

            var usage = s.Owner.PostUsageAsync(raftId, new UsageBody(Today, MaterialUsageTestKit.BrokenBuoy, [new UsageLine(item)]));
            var close = CloseAsync(s.Manager.Client, cycleId, version);
            await Task.WhenAll(usage, close);

            var used = await SucceededAsync(await usage, HttpStatusCode.Created);
            var closed = await SucceededAsync(await close, HttpStatusCode.OK);
            await AssertOneWonAsync(s, cycleId, round, used, closed);
            Assert.Equal(used ? 1 : 0, await s.Host.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == raftId)));
            wins += closed ? 1 : 0;
        }

        Assert.Equal(wins, Pushes(s.Host.Api.Services, s.Host.Owner.Account.Id));
    }

    [SqlServerFact]
    public async Task A_close_racing_a_cultivation_log_lets_exactly_one_save_and_leaves_no_stray_photo()
    {
        var s = await StartAsync();
        await using var _ = s.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var (_, cycleId) = await SeededAsync(s.Owner, s.ZoneId);
            var version = await VersionAsync(s.Manager.Client, cycleId);

            var log = s.Owner.PostLogAsync(cycleId);
            var close = CloseAsync(s.Manager.Client, cycleId, version);
            await Task.WhenAll(log, close);

            var logged = await SucceededAsync(await log, HttpStatusCode.Created);
            var closed = await SucceededAsync(await close, HttpStatusCode.OK);
            await AssertOneWonAsync(s, cycleId, round, logged, closed);
            Assert.Equal(logged ? 1 : 0, await s.Host.QueryAsync(db => db.CultivationLogs.CountAsync(l => l.CycleId == cycleId)));
        }

        var kept = await s.Host.QueryAsync(db => db.CultivationLogPhotos.Select(p => p.FileKey).OrderBy(k => k).ToListAsync());
        Assert.Equal(kept, s.Host.StoredKeys(FileCategories.CultivationLogPhotos));
    }

    [SqlServerFact]
    public async Task A_close_racing_a_harvest_correction_lets_exactly_one_save()
    {
        var s = await StartAsync();
        await using var _ = s.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var (raftId, cycleId) = await SeededAsync(s.Owner, s.ZoneId);
            var lot = await s.Owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
            var version = await VersionAsync(s.Manager.Client, cycleId);

            var correction = s.Owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(1) with { Cages = "3" }, "Wrong count", lot.Text("rowVersion"), lot.PhotoKeysOfLot()));
            var close = CloseAsync(s.Manager.Client, cycleId, version);
            await Task.WhenAll(correction, close);

            var corrected = await SucceededAsync(await correction, HttpStatusCode.OK);
            var closed = await SucceededAsync(await close, HttpStatusCode.OK);
            await AssertOneWonAsync(s, cycleId, round, corrected, closed);
            Assert.Equal(corrected ? 3 : 8, await s.Host.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot.Id()).Select(l => l.CageCount).SingleAsync()));
        }
    }

    // A cycle with no record can be cancelled (BR-CYCLE-01) or closed early: one of the two saves, the other answers 404 or 409, never 500.
    [SqlServerFact]
    public async Task A_close_racing_a_cancel_lets_exactly_one_save()
    {
        var s = await StartAsync();
        await using var _ = s.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var raftId = (await s.Owner.CreateRaftAsync(s.ZoneId)).Id();
            var cycleId = (await s.Owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-1)))).Id();
            var version = await VersionAsync(s.Manager.Client, cycleId);

            var cancel = s.Owner.CancelCycleAsync(cycleId, version);
            var close = CloseAsync(s.Manager.Client, cycleId, version);
            await Task.WhenAll(cancel, close);

            var cancelled = (await cancel).StatusCode == HttpStatusCode.NoContent;
            Assert.True(cancelled || (await cancel).StatusCode == HttpStatusCode.Conflict, $"cancel {(int)(await cancel).StatusCode}");
            var closeAnswer = await close;
            Assert.True(closeAnswer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.NotFound, $"close {(int)closeAnswer.StatusCode}: {await closeAnswer.Content.ReadAsStringAsync()}");
            var closed = closeAnswer.StatusCode == HttpStatusCode.OK;
            Assert.True(cancelled ^ closed, $"Round {round}: cancel {cancelled}, close {closed}.");
            Assert.Equal(cancelled ? 0 : 1, await s.Host.QueryAsync(db => db.CultivationCycles.CountAsync(c => c.Id == cycleId)));
            Assert.Equal(closed ? 1 : 0, await NoticesAsync(s.Host, cycleId));
        }
    }

    [SqlServerFact]
    public async Task Parallel_closes_by_a_zone_manager_tell_and_push_the_owner_once()
    {
        var s = await StartAsync();
        await using var _ = s.Host;
        var (_, cycleId) = await SeededAsync(s.Owner, s.ZoneId);
        var version = await VersionAsync(s.Manager.Client, cycleId);

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => CloseAsync(s.Manager.Client, cycleId, version)));

        var saved = 0;
        foreach (var answer in answers)
        {
            saved += await SucceededAsync(answer, HttpStatusCode.OK) ? 1 : 0;
        }

        Assert.Equal(1, saved);
        Assert.Equal(1, await NoticesAsync(s.Host, cycleId));
        Assert.Equal(1, Pushes(s.Host.Api.Services, s.Host.Owner.Account.Id));
    }

    // Worst moment, close side: a harvest commits while the close waits to save. The close must answer MSG06, keep the cycle Open, write no audit and tell nobody;
    // a fresh close then counts the lot.
    [SqlServerFact]
    public async Task A_harvest_saved_while_the_close_waits_makes_the_close_answer_msg06_and_tell_nobody()
    {
        var s = await StartAsync();
        await using var _ = s.Host;
        var (raftId, cycleId) = await SeededAsync(s.Owner, s.ZoneId);
        var version = await VersionAsync(s.Manager.Client, cycleId);
        var (api, held, hold) = await HeldAsync(s.Host, s.Manager.Account.Username);
        await using var _api = api;

        var close = CloseAsync(held, cycleId, version);
        await hold.Reached;
        await s.Owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2) with { EstimatedKg = "77" });
        hold.Release();

        Assert.Equal("MSG06", (await (await close).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal(StatusIds.CultivationCycle.Open, await StatusAsync(s.Host, cycleId));
        Assert.Equal(0, await NoticesAsync(s.Host, cycleId));
        Assert.Equal(0, await s.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == CycleCloseTestKit.ClosedAction && e.RecordId == cycleId)));
        Assert.Equal(0, Pushes(api.Services, s.Host.Owner.Account.Id));

        var closed = await s.Owner.CloseCycleAsync(cycleId);
        Assert.Equal((false, 77m), (closed.GetProperty("cycle").Flag("closedEarly"), closed.GetProperty("summary").Number("kgHarvested")));
    }

    // Worst moment, write side: a usage dated today that read the cycle as Open waits to save while the cycle closes; it must not land in the closed cycle (BR-CYCLE-04).
    [SqlServerFact]
    public async Task A_usage_waiting_to_save_while_the_cycle_closes_is_not_saved_into_the_closed_cycle()
    {
        var s = await StartAsync();
        await using var _ = s.Host;
        var item = await s.Host.ItemAsync();
        var (raftId, cycleId) = await SeededAsync(s.Owner, s.ZoneId);
        var other = await s.Host.StaffInZoneAsync(s.ZoneId);
        var (api, held, hold) = await HeldAsync(s.Host, other.Account.Username);
        await using var _api = api;

        var usage = held.PostUsageAsync(raftId, new UsageBody(Today, MaterialUsageTestKit.BrokenBuoy, [new UsageLine(item)]));
        await hold.Reached;
        await s.Manager.Client.CloseCycleAsync(cycleId, ownerApproved: true);
        hold.Release();

        Assert.Equal(HttpStatusCode.Conflict, (await usage).StatusCode);
        Assert.Equal(0, await s.Host.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == raftId)));
        Assert.Equal(1, await NoticesAsync(s.Host, cycleId));
    }

    // Worst moment, write side: a log whose photo is uploaded and whose save waits while the cycle closes is refused, and its photo does not stay in storage.
    [SqlServerFact]
    public async Task A_log_waiting_to_save_while_the_cycle_closes_is_refused_and_its_photo_removed()
    {
        var s = await StartAsync();
        await using var _ = s.Host;
        var (_, cycleId) = await SeededAsync(s.Owner, s.ZoneId);
        var other = await s.Host.StaffInZoneAsync(s.ZoneId);
        var (api, held, hold) = await HeldAsync(s.Host, other.Account.Username);
        await using var _api = api;

        var log = held.PostLogAsync(cycleId);
        await hold.Reached;
        await s.Manager.Client.CloseCycleAsync(cycleId, ownerApproved: true);
        hold.Release();

        Assert.Equal(HttpStatusCode.Conflict, (await log).StatusCode);
        Assert.Equal(0, await s.Host.QueryAsync(db => db.CultivationLogs.CountAsync(l => l.CycleId == cycleId)));
        Assert.Empty(s.Host.StoredKeys(FileCategories.CultivationLogPhotos));
    }

    /// <summary>The API over the same database, whose first save stops until released, and a client logged in with it (login goes through the normal API).</summary>
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

        public void DiscardChanges() => inner.DiscardChanges();
    }
}
