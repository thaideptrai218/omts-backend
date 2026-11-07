using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Storage;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>Stops the first save it is armed for just before it starts, until released: the worst moment of a race, every time.</summary>
public sealed class SaveHold
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed = 1;

    public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(60));

    public void Release() => _released.TrySetResult();

    internal async Task PassAsync()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _reached.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }
}

/// <summary>QA of PR H1a on SQL Server: the column limits only it enforces, and every race answered 2xx or 409, never 500. These run alone as well as in a group.</summary>
public class HarvestLotsQaTestsSqlServer
{
    private const int Rounds = 3;

    private sealed record Seeded(Guid RaftId, Guid CycleId, SeedLotMade Lot, JsonElement Session);

    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        return (host, host.Owner.Client, (await host.Owner.Client.CreateZoneAsync()).Id());
    }

    private static async Task<Seeded> SeededAsync(HttpClient owner, Guid zoneId, string? raftCode = null, int[]? lanes = null, int laneCount = 5)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody(raftCode, laneCount))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var lot = await owner.SeedLotAsync();
        return new Seeded(raftId, cycleId, lot, await owner.SeedingRecordAsync(cycleId, lot.Session(lanes ?? [1, 2, 3, 4, 5])));
    }

    private static async Task AssertNoServerErrorAsync(params HttpResponseMessage[] answers)
    {
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.True((int)answer.StatusCode < 500, $"{(int)answer.StatusCode}: {text}");
        }
    }

    /// <summary>The API over the same database, whose first save stops until released, and a client logged in with it.</summary>
    private static async Task<(WebApplicationFactory<Program> Api, HttpClient Client, SaveHold Hold)> HeldAsync(FarmingSqlServerHost host, string username)
    {
        var hold = new SaveHold();
        var api = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<UnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new HeldUnitOfWork(provider.GetRequiredService<UnitOfWork>(), hold));
        }));
        var client = api.CreateClient();
        // Logging in saves too: the hold is armed for the first save after it.
        var login = host.Api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(login, username);
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

    // BR-HARVEST-04: the catalog holds three statuses and only the three moves.
    [SqlServerFact]
    public async Task The_catalog_holds_the_three_statuses_and_only_the_three_moves()
    {
        var (host, _, _) = await StartAsync();
        await using var _host = host;

        var statuses = await host.QueryAsync(db => db.Statuses.Where(s => s.Group == StatusGroups.HarvestLot).OrderBy(s => s.Id).Select(s => new { s.Id, s.Code, s.IsInitial, s.IsFinal }).ToListAsync());
        Assert.Equal(
            [(2901, "OPEN", true, false), (2902, "REOPENED", false, false), (2903, "CLOSED", false, false)],
            statuses.Select(s => (s.Id, s.Code, s.IsInitial, s.IsFinal)));

        var moves = await host.QueryAsync(db => db.StatusTransitions
            .Where(t => (t.FromStatusId >= 2901 && t.FromStatusId <= 2903) || (t.ToStatusId >= 2901 && t.ToStatusId <= 2903))
            .Select(t => new { t.FromStatusId, t.ToStatusId }).ToListAsync());
        Assert.Equal(new[] { (2901, 2903), (2902, 2903), (2903, 2902) }.Order(), moves.Select(m => (m.FromStatusId, m.ToStatusId)).Order());
    }

    // BR-HARVEST-03 "the raft code and the harvest date, with the zone code put first": every code the zone and raft columns accept must make a lot. Raft 50 + zone 50 give 110 characters; the lot code column holds 120.
    [SqlServerFact]
    public async Task M1_a_zone_and_raft_with_the_longest_codes_they_accept_can_record_a_harvest()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody(new string('Z', 50)))).Id();
        var raft = await SeededAsync(owner, zoneId, new string('R', 50));

        var answer = await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });

        var text = await answer.Content.ReadAsStringAsync();
        var failure = host.Api.Logs.Lines.FirstOrDefault(l => l.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        Assert.True(answer.StatusCode == HttpStatusCode.Created, $"{(int)answer.StatusCode}: {text}\nStored files left: {host.StoredKeys(FileCategories.HarvestLotPhotos).Count}, lots saved: {await host.QueryAsync(db => db.HarvestLots.CountAsync())}\nSQL Server said: {failure}");
    }

    // Where the limit starts: the first lot of the day just fits at 100 characters, the second, at 102, does not.
    [SqlServerFact]
    public async Task M1_a_second_lot_of_the_day_on_a_100_character_code_is_saved_too()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody(new string('Z', 45)))).Id();
        var raft = await SeededAsync(owner, zoneId, new string('R', 45));

        var first = await owner.HarvestLotAsync(raft.RaftId);
        Assert.Equal(100, first.Text("lotCode").Length);

        var second = await owner.PostHarvestAsync(raft.RaftId);

        Assert.True(second.StatusCode == HttpStatusCode.Created, $"{(int)second.StatusCode}: {await second.Content.ReadAsStringAsync()}");
    }

    // Characters LIKE treats as patterns stay plain text in the next-code query, on SQL Server.
    [SqlServerFact]
    public async Task Pattern_characters_in_a_raft_code_do_not_mix_lots_of_other_rafts()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var zone = (await owner.GetFromJsonAsync(zoneId));
        var zoneCode = zone.Text("code");
        foreach (var (pattern, plain) in new[] { ("R_1", "RX1"), ("R%2", "RAB2"), ("R[3]", "R3") })
        {
            var (patterned, other) = (await SeededAsync(owner, zoneId, pattern), await SeededAsync(owner, zoneId, plain));

            var (otherFirst, otherSecond) = (await owner.HarvestLotAsync(other.RaftId), await owner.HarvestLotAsync(other.RaftId));
            var first = await owner.HarvestLotAsync(patterned.RaftId);

            var day = first.Date("harvestDate").ToString("yyyyMMdd");
            Assert.Equal($"{zoneCode}-{plain}-{day}-2", otherSecond.Text("lotCode"));
            Assert.Equal($"{zoneCode}-{plain}-{day}", otherFirst.Text("lotCode"));
            Assert.Equal($"{zoneCode}-{pattern}-{day}", first.Text("lotCode"));
        }
    }

    // BR-HARVEST-03 "unique in the cooperative": zones KV and KV-01 with rafts 01-R and R come to one code, in the very same moment.
    [SqlServerFact]
    public async Task Two_zones_that_race_for_one_code_are_both_saved_with_different_codes()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var suffix = NewCode();
        var (zoneA, zoneB) = ((await owner.CreateZoneAsync(NewZoneBody(suffix))).Id(), (await owner.CreateZoneAsync(NewZoneBody($"{suffix}-01"))).Id());
        for (var round = 0; round < Rounds * 2; round++)
        {
            var (first, second) = (await SeededAsync(owner, zoneA, $"01-R{round}"), await SeededAsync(owner, zoneB, $"R{round}"));

            var answers = await Task.WhenAll(
                owner.PostHarvestAsync(first.RaftId, new HarvestFields { Photos = [Jpeg] }),
                owner.PostHarvestAsync(second.RaftId, new HarvestFields { Photos = [Png] }));

            await AssertNoServerErrorAsync(answers);
            Assert.All(answers, a => Assert.Equal(HttpStatusCode.Created, a.StatusCode));
            var codes = await host.QueryAsync(db => db.HarvestLots.Where(l => l.RaftId == first.RaftId || l.RaftId == second.RaftId).Select(l => l.LotCode).ToListAsync());
            Assert.Equal(2, codes.Distinct().Count());
            Assert.Contains(codes, c => c.EndsWith("-2", StringComparison.Ordinal));
        }

        var keys = await host.QueryAsync(db => db.HarvestLotPhotos.Select(p => p.FileKey).OrderBy(k => k).ToListAsync());
        Assert.Equal(keys, host.StoredKeys(FileCategories.HarvestLotPhotos));
    }

    // A real duplicate on the unique index, not a thrown one: the lot held at its save loses the code to the other raft, and takes the next with its photo, one audit row, one notice.
    [SqlServerFact]
    public async Task A_lot_held_at_its_save_that_loses_the_code_to_another_raft_takes_the_next_code_with_everything_once()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var zoneCode = NewCode();
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody(zoneCode))).Id();
        var (plain, prefixed) = (await SeededAsync(owner, zoneId, "R1"), await SeededAsync(owner, zoneId, $"{zoneCode}-R1"));
        var manager = await host.StaffInZoneAsync(zoneId);
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PostHarvestAsync(plain.RaftId, new HarvestFields { Photos = [Jpeg] });
        await hold.Reached;
        var winner = await owner.HarvestLotAsync(prefixed.RaftId);
        hold.Release();

        var loser = (await (await pending).ReadAsync(HttpStatusCode.Created)).GetProperty("lot");
        Assert.Equal($"{winner.Text("lotCode")}-2", loser.Text("lotCode"));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Created" && e.RecordId == loser.Id())));
        Assert.Equal(1, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "HARVEST_LOT_RECORDED" && n.RelatedRecordId == loser.Id())));
        Assert.Equal(1, await host.QueryAsync(db => db.HarvestLotPhotos.CountAsync(p => p.LotId == loser.Id())));
        Assert.Equal(2, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Created")));
        Assert.Single(host.StoredKeys(FileCategories.HarvestLotPhotos));
    }

    [SqlServerFact]
    public async Task Parallel_records_on_many_rafts_of_one_zone_are_all_saved()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        for (var round = 0; round < Rounds; round++)
        {
            var rafts = new List<Seeded>();
            for (var i = 0; i < 8; i++)
            {
                rafts.Add(await SeededAsync(owner, zoneId));
            }

            var answers = await Task.WhenAll(rafts.Select(r => manager.Client.PostHarvestAsync(r.RaftId, new HarvestFields { Photos = [Jpeg], Lanes = ["1", "2"], Finished = ["1"] })));

            await AssertNoServerErrorAsync(answers);
            Assert.All(answers, a => Assert.Equal(HttpStatusCode.Created, a.StatusCode));
        }
    }

    // One lane in one moment: lots finishing it never both stand, and the second that is saved sees the first.
    [SqlServerFact]
    public async Task Parallel_lots_finishing_one_lane_leave_it_finished_once()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        for (var round = 0; round < Rounds * 2; round++)
        {
            var raft = await SeededAsync(owner, zoneId);

            var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1))));

            await AssertNoServerErrorAsync(answers);
            Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.Created));
            Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.Created), a => Assert.True(a.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest));
            Assert.Equal(1, await host.QueryAsync(db => db.HarvestLots.CountAsync(l => l.RaftId == raft.RaftId)));
        }
    }

    // A correction that takes the seed off lane 1, in the moment a lot harvests lane 1: never a lot on a lane with no seed, never a corrected session with a lane in a lot.
    [SqlServerFact]
    public async Task A_lot_against_a_correction_moving_its_lane_never_stands_on_a_lane_with_no_seed()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds * 2; round++)
        {
            var raft = await SeededAsync(owner, zoneId, lanes: [1]);
            var correction = raft.Session.AsCorrection() with { LaneNumbers = [2] };

            var answers = await Task.WhenAll(owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1)), owner.PutSeedingAsync(raft.Session.Id(), correction));

            await AssertNoServerErrorAsync(answers);
            var lots = await host.QueryAsync(db => db.HarvestLots.CountAsync(l => l.RaftId == raft.RaftId));
            var onLane1 = await host.QueryAsync(db => db.SeedingRecordLanes.CountAsync(l => l.SeedingRecordId == raft.Session.Id() && l.Lane.LaneNumber == 1));
            Assert.False(lots == 1 && onLane1 == 0, $"A lot stands on a lane with no seed: lot {answers[0].StatusCode}, correction {answers[1].StatusCode}");
        }
    }

    [SqlServerFact]
    public async Task A_lot_against_a_new_session_a_mark_complete_or_a_cycle_edit_answers_2xx_or_4xx_never_500()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId, lanes: [1, 2]);
            var list = await owner.SeedingListAsync(raft.CycleId);
            var version = list.Text("cycleRowVersion");

            var answers = await Task.WhenAll(
                owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1)),
                owner.PostSeedingAsync(raft.CycleId, raft.Lot.Session([3])),
                owner.PostSeedingCompleteAsync(raft.CycleId, version),
                owner.PutCycleAsync(raft.CycleId, new CycleBody(Today.AddDays(-9), RowVersion: version)));

            await AssertNoServerErrorAsync(answers);
        }
    }

    // The save of the lot is held after it decided, and in the meantime the session goes: the cycle row refuses the lot.
    [SqlServerFact]
    public async Task A_lot_held_at_its_save_while_the_session_is_removed_answers_409_and_leaves_nothing()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var raft = await SeededAsync(owner, zoneId);
        var manager = await host.StaffInZoneAsync(zoneId);
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });
        await hold.Reached;
        (await owner.RemoveSeedingAsync(raft.Session.Id(), rowVersion: raft.Session.Text("rowVersion"))).EnsureSuccessStatusCode();
        hold.Release();

        var answer = await pending;
        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Equal(0, await host.QueryAsync(db => db.HarvestLots.CountAsync(l => l.RaftId == raft.RaftId)));
        Assert.Empty(host.StoredKeys(FileCategories.HarvestLotPhotos));
    }

    // The worst order for the foreign key: the lot decided, then the session goes and the cycle is cancelled, then the lot saves on a cycle that is gone. 409, not a 500 on the foreign key.
    [SqlServerFact]
    public async Task A_lot_held_at_its_save_while_its_cycle_is_cancelled_answers_409_and_leaves_nothing()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var raft = await SeededAsync(owner, zoneId);
        var manager = await host.StaffInZoneAsync(zoneId);
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });
        await hold.Reached;
        (await owner.RemoveSeedingAsync(raft.Session.Id(), rowVersion: raft.Session.Text("rowVersion"))).EnsureSuccessStatusCode();
        var version = (await owner.SeedingListAsync(raft.CycleId)).Text("cycleRowVersion");
        (await owner.CancelCycleAsync(raft.CycleId, version)).EnsureSuccessStatusCode();
        hold.Release();

        var answer = await pending;
        Assert.True(answer.StatusCode == HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
        Assert.Equal(0, await host.QueryAsync(db => db.HarvestLots.CountAsync(l => l.RaftId == raft.RaftId)));
        Assert.Empty(host.StoredKeys(FileCategories.HarvestLotPhotos));
    }

    // And the other way round: the removal of the session decided with no lot, and a lot comes before it saves.
    [SqlServerFact]
    public async Task A_removal_held_at_its_save_while_a_lot_is_recorded_answers_409_and_the_session_stays()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var raft = await SeededAsync(owner, zoneId);
        var (api, held, hold) = await HeldAsync(host, host.Owner.Account.Username);
        await using var _api = api;

        var pending = held.RemoveSeedingAsync(raft.Session.Id(), rowVersion: raft.Session.Text("rowVersion"));
        await hold.Reached;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        hold.Release();

        Assert.Equal(HttpStatusCode.Conflict, (await pending).StatusCode);
        Assert.Equal(1, await host.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.Id == raft.Session.Id())));
    }

    // The same for a correction that keeps the lanes but changes the date: a session in a lot is corrected only in TRACE-03.
    [SqlServerFact]
    public async Task A_correction_held_at_its_save_while_a_lot_is_recorded_answers_409_and_the_session_stays_as_it_was()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var raft = await SeededAsync(owner, zoneId);
        var (api, held, hold) = await HeldAsync(host, host.Owner.Account.Username);
        await using var _api = api;

        var pending = held.PutSeedingAsync(raft.Session.Id(), raft.Session.AsCorrection() with { Quantity = 11m });
        await hold.Reached;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        hold.Release();

        Assert.Equal(HttpStatusCode.Conflict, (await pending).StatusCode);
        Assert.Equal(10m, await host.QueryAsync(db => db.SeedingRecords.Where(r => r.Id == raft.Session.Id()).Select(r => r.Quantity).SingleAsync()));
    }

    // A raft with an Open cycle cannot be discontinued, so the two never both go through; a zone with a raft In Use neither.
    [SqlServerFact]
    public async Task A_lot_against_the_discontinuation_of_its_raft_or_zone_answers_201_and_409()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);

            var answers = await Task.WhenAll(
                owner.PostHarvestAsync(raft.RaftId),
                owner.PostAsJsonNullableAsync($"/api/rafts/{raft.RaftId}/discontinue"),
                owner.PostAsJsonNullableAsync($"/api/zones/{zoneId}/discontinue"));

            await AssertNoServerErrorAsync(answers);
            Assert.Equal(HttpStatusCode.Created, answers[0].StatusCode);
            Assert.False(answers[1].IsSuccessStatusCode);
            Assert.False(answers[2].IsSuccessStatusCode);
        }
    }

    [SqlServerFact]
    public async Task Parallel_saves_of_the_settings_answer_one_200_and_the_rest_409()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds * 2; round++)
        {
            var current = await owner.HarvestSettingsAsync();

            var answers = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => owner.PutHarvestSettingsAsync(60m + round + i, 3 + i, current.Text("rowVersion"))));

            await AssertNoServerErrorAsync(answers);
            Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
            Assert.Equal(3, answers.Count(a => a.StatusCode == HttpStatusCode.Conflict));
        }
    }

    // Reads go on while lots are saved: the screens never answer an error, on a database with no snapshot isolation.
    [SqlServerFact]
    public async Task Reads_of_the_screens_in_parallel_with_lots_never_fail()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);

            var calls = new List<Task<HttpResponseMessage>>();
            for (var lane = 1; lane <= 5; lane++)
            {
                calls.Add(manager.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(lane).FinishedLanes(lane)));
                calls.Add(owner.GetAsync($"/api/rafts/{raft.RaftId}/harvest-lots"));
                calls.Add(owner.GetAsync($"/api/rafts/{raft.RaftId}/progress"));
                calls.Add(owner.GetAsync($"/api/cultivation-cycles/{raft.CycleId}/seeding-records"));
            }

            await AssertNoServerErrorAsync(await Task.WhenAll(calls));
        }
    }

    // SQL Server gives 60.50 back with its scale: the same value sent again is no change and writes no audit row.
    [SqlServerFact]
    public async Task The_same_settings_saved_again_write_no_audit_row_whatever_the_scale()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        await owner.SetHarvestSettingsAsync(60.5m, 4);
        var rows = await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Cooperative.HarvestSettingsChanged"));
        var read = await owner.HarvestSettingsAsync();

        var answer = await owner.PutHarvestSettingsAsync(60.50m, 4, read.Text("rowVersion"));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(rows, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Cooperative.HarvestSettingsChanged")));
        Assert.Equal(read.Text("rowVersion"), (await owner.HarvestSettingsAsync()).Text("rowVersion"));
    }
    /// <summary>The API over the same database, whose first upload stops after it is stored until released, on the real row versions.</summary>
    private static async Task<(WebApplicationFactory<Program> Api, HttpClient Client, UploadHold Hold)> HeldUploadAsync(FarmingSqlServerHost host, string username)
    {
        var hold = new UploadHold();
        var api = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IFileStorage>(provider => new HeldStorage(provider.GetRequiredService<LocalFileStorage>(), hold))));
        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(host.Api.CreateClient(), username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (api, client, hold);
    }

    // BR-CYCLE-05, NFR-09: logs have no version check. A lot now also moves the cycle: a log with its photos at sea is not refused for it (M2 of the C4 report, on real row versions).
    [SqlServerFact]
    public async Task A_cultivation_log_uploading_while_a_lot_is_saved_is_not_refused()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var raft = await SeededAsync(owner, zoneId);
        var manager = await host.StaffInZoneAsync(zoneId);
        var (api, held, hold) = await HeldUploadAsync(host, manager.Account.Username);
        await using var _api = api;

        hold.Arm();
        var log = held.PostLogAsync(raft.CycleId, new LogFields { Notes = "at sea" });
        await hold.Reached;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        hold.Release();

        var answer = await log;
        Assert.True(answer.StatusCode == HttpStatusCode.Created, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
    }

    // The other way round: a lot uploading while a log, a session, a plan edit and another lot are saved reads the cycle again and goes through, on real row versions.
    [SqlServerFact]
    public async Task A_lot_uploading_while_other_saves_of_the_cycle_are_made_reads_the_cycle_again_and_goes_through()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var raft = await SeededAsync(owner, zoneId, lanes: [1, 2]);
        var manager = await host.StaffInZoneAsync(zoneId);
        var (api, held, hold) = await HeldUploadAsync(host, manager.Account.Username);
        await using var _api = api;

        hold.Arm();
        var pending = held.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1) with { Photos = [Jpeg] });
        await hold.Reached;
        await owner.RecordLogAsync(raft.CycleId);
        await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([3]));
        var between = await owner.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(2).FinishedLanes(2));
        (await owner.PutCycleAsync(raft.CycleId, new CycleBody(Today.AddDays(-9), RowVersion: between.Text("cycleRowVersion")))).EnsureSuccessStatusCode();
        hold.Release();

        var saved = await (await pending).ReadAsync(HttpStatusCode.Created);
        Assert.Equal($"{between.GetProperty("lot").Text("lotCode")}-2", saved.GetProperty("lot").Text("lotCode"));
        Assert.Equal(host.StoredKeys(FileCategories.HarvestLotPhotos), await host.QueryAsync(db => db.HarvestLotPhotos.Select(p => p.FileKey).OrderBy(k => k).ToListAsync()));
    }
    // The settings and the profile share a row (client list): each makes the other's open form stale, MSG06 with the version, and neither touches the other's values.
    [SqlServerFact]
    public async Task The_settings_and_the_profile_make_each_others_form_stale_and_keep_each_others_values()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var settings = await owner.HarvestSettingsAsync();
        var profileVersion = await CooperativeProfileRequests.RowVersionAsync(owner);
        Assert.Equal(settings.Text("rowVersion"), profileVersion);

        await (await owner.PutHarvestSettingsAsync(61m, 4, settings.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);

        var stale = await (await owner.PutAsJsonAsync("/api/cooperative/profile", CooperativeProfileRequests.Valid(profileVersion))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG06", stale.Text("code"));

        var fresh = await CooperativeProfileRequests.RowVersionAsync(owner);
        (await owner.PutAsJsonAsync("/api/cooperative/profile", CooperativeProfileRequests.Valid(fresh))).EnsureSuccessStatusCode();

        var afterProfile = await owner.HarvestSettingsAsync();
        Assert.Equal((61m, 4), (afterProfile.Kg("averageKgPerCage"), afterProfile.GetProperty("daysBeforeAutoClose").GetInt32()));
        Assert.Equal("MSG06", await (await owner.PutHarvestSettingsAsync(70m, 5, fresh)).ErrorCodeAsync());
        Assert.Equal(61m, (await owner.HarvestSettingsAsync()).Kg("averageKgPerCage"));
        await (await owner.PutHarvestSettingsAsync(70m, 5, afterProfile.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("HTX Trung Nam", (await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonElement>(owner, "/api/cooperative/profile")).Text("shortName"));
    }
    // Two sales in parallel closing one lot (§8.2): the row version makes one fail, so the lot is not closed twice.
    [SqlServerFact]
    public async Task Two_saves_changing_one_lot_status_conflict_on_its_row_version()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _host = host;
        var raft = await SeededAsync(owner, zoneId);
        var lotId = (await owner.HarvestLotAsync(raft.RaftId)).Id();
        var day = new DateOnly(2026, 10, 30);

        using var first = host.Api.Services.CreateScope();
        using var second = host.Api.Services.CreateScope();
        var (firstLot, secondLot) = (
            await first.ServiceProvider.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lotId, CancellationToken.None),
            await second.ServiceProvider.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lotId, CancellationToken.None));

        Assert.Null(first.ServiceProvider.GetRequiredService<HarvestLotStatusService>().CloseSoldWholeLot(firstLot!, day, Guid.NewGuid()));
        Assert.Null(second.ServiceProvider.GetRequiredService<HarvestLotStatusService>().Close(secondLot!, day, 1m, HarvestLotCloseReason.Spoiled));

        await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        var saved = await host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));
        Assert.Equal((StatusIds.HarvestLot.Closed, HarvestLotCloseReason.SoldWholeLot, 0m), (saved.StatusId, saved.CloseReason!.Value, saved.LeftoverKg!.Value));
    }
}

file static class SqlServerQaCalls
{
    public static Task<HttpResponseMessage> PostAsJsonNullableAsync(this HttpClient client, string url) =>
        System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, url, new { reason = "Stopped using it", rowVersion = Convert.ToBase64String(new byte[8]) });

    public static Task<JsonElement> GetFromJsonAsync(this HttpClient client, Guid zoneId) =>
        System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonElement>(client, $"/api/zones/{zoneId}");
}
