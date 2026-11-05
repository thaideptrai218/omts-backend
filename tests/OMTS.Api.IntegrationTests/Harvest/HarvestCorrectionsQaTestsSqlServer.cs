using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using Xunit.Abstractions;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H1b on SQL Server: what only a real database enforces, and each race at its worst moment and in parallel. A race answers 2xx, 404 or 409, never 500. Run these alone as well as in a group.</summary>
public class HarvestCorrectionsQaTestsSqlServer(ITestOutputHelper output)
{
    private const int Rounds = 6;

    private sealed record Seeded(Guid RaftId, Guid CycleId, SeedLotMade Lot, JsonElement Session);

    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId)> StartAsync(string? zoneCode = null)
    {
        var host = await FarmingSqlServerHost.StartAsync();
        return (host, host.Owner.Client, (await host.Owner.Client.CreateZoneAsync(zoneCode is null ? null : NewZoneBody(zoneCode))).Id());
    }

    private static async Task<Seeded> SeededAsync(HttpClient owner, Guid zoneId, string? raftCode = null, int[]? lanes = null)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody(raftCode))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var lot = await owner.SeedLotAsync();
        return new Seeded(raftId, cycleId, lot, await owner.SeedingRecordAsync(cycleId, lot.Session(lanes ?? [1, 2, 3, 4, 5])));
    }

    private static HarvestCorrection Correction(JsonElement lot, HarvestFields fields, string reason = "Wrong count") => new(fields, reason, lot.Text("rowVersion"), lot.PhotoKeysOfLot());

    private static async Task AssertAnsweredAsync(params HttpResponseMessage[] answers)
    {
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.IsSuccessStatusCode || answer.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound, $"{(int)answer.StatusCode}: {text}");
        }
    }

    private static async Task AssertFilesMatchAsync(FarmingSqlServerHost host)
    {
        var inDatabase = await host.QueryAsync(db => db.HarvestLotPhotos.Select(p => p.FileKey).OrderBy(k => k).ToListAsync());
        Assert.Equal(inDatabase, host.StoredKeys(FileCategories.HarvestLotPhotos));
    }

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

    // The unique index on a slot: the removed photo and the new photo share slot 1 in one save, so the delete must go first. Only SQL Server would refuse the other order.
    [SqlServerFact]
    public async Task A_new_photo_takes_the_slot_of_a_removed_photo_in_one_save()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png, Jpeg] });
        var keys = lot.PhotoKeysOfLot();

        var replaced = (await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Photos = [Png] }, "Wrong photo", lot.Text("rowVersion"), [keys[1], keys[2]]))).ReadAsync(HttpStatusCode.OK)).GetProperty("lot");
        Assert.Equal([1, 2, 3], replaced.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("sortOrder").GetInt32()));
        Assert.Equal(keys[1], replaced.GetProperty("photos")[1].Text("key"));
        Assert.DoesNotContain(keys[0], replaced.PhotoKeysOfLot());
        await AssertFilesMatchAsync(host);

        var all = (await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Photos = [Jpeg, Png, Jpeg] }, "All new", replaced.Text("rowVersion")))).ReadAsync(HttpStatusCode.OK)).GetProperty("lot");
        Assert.Equal(3, all.PhotoKeysOfLot().Count);
        Assert.Empty(all.PhotoKeysOfLot().Intersect(replaced.PhotoKeysOfLot()));
        await AssertFilesMatchAsync(host);
    }

    [SqlServerFact]
    public async Task Lanes_taken_out_and_added_in_one_save_move_the_versions_and_a_save_of_the_same_values_does_not()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        var recorded = await owner.RecordHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["1", "2"], Finished = ["1"], EstimatedKg = "400", Photos = [Jpeg] });
        var lot = recorded.GetProperty("lot");

        var same = await (await owner.PutHarvestLotAsync(lot.Id(), Correction(lot, new HarvestFields { Lanes = ["2", "1"], Finished = ["1"], EstimatedKg = "400.00" }))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(lot.Text("rowVersion"), same.GetProperty("lot").Text("rowVersion"));
        Assert.Equal(recorded.Text("cycleRowVersion"), same.Text("cycleRowVersion"));
        Assert.Equal(0, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == HarvestLotAudit.Corrected)));

        var moved = await (await owner.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(2, 3).FinishedLanes(3)))).ReadAsync(HttpStatusCode.OK);
        Assert.NotEqual(lot.Text("rowVersion"), moved.GetProperty("lot").Text("rowVersion"));
        Assert.NotEqual(recorded.Text("cycleRowVersion"), moved.Text("cycleRowVersion"));
        var saved = moved.GetProperty("lot");
        Assert.Equal([2, 3], saved.Numbers("laneNumbers"));
        Assert.Equal([3], saved.Numbers("finishedLaneNumbers"));
        Assert.Equal(2, await host.QueryAsync(db => db.HarvestLotLanes.CountAsync(l => l.LotId == lot.Id())));

        // The stale form is told who saved, as NFR-09 and MSG06 say: lanes alone also count as a save of that person.
        var stale = await (await owner.PutHarvestLotAsync(lot.Id(), Correction(lot, new HarvestFields { Cages = "9" }))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG06", saved.Text("rowVersion")), (stale.Text("code"), stale.Text("currentRowVersion")));
        Assert.Contains(host.Owner.Account.DisplayName, stale.Text("detail"));
    }

    // Lengths the columns enforce: the longest reason, and the longest codes on every copy of the code.
    [SqlServerFact]
    public async Task The_longest_reason_and_the_longest_codes_fit_every_column_on_a_correction_a_removal_and_the_next_lot()
    {
        var zoneCode = $"Z{Guid.NewGuid():N}{Guid.NewGuid():N}"[..50];
        var (host, owner, zoneId) = await StartAsync(zoneCode);
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId, $"R{Guid.NewGuid():N}{Guid.NewGuid():N}"[..50]);
        var manager = await host.StaffInZoneAsync(zoneId);

        // By a manager: the notification to the Owner also holds the code.
        var first = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var second = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var reason = new string('x', 990) + "Đếm sai số";

        await (await owner.PutHarvestLotAsync(second.Id(), Correction(second, new HarvestFields { Cages = "9" }, reason))).ReadAsync(HttpStatusCode.OK);
        var current = await owner.GetHarvestLotAsync(second.Id());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(second.Id(), $"  {reason}  ", current.Text("rowVersion"))).StatusCode);
        var third = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        Assert.Equal($"{first.Text("lotCode")}-3", third.Text("lotCode"));
        var row = await host.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == HarvestLotAudit.Removed));
        Assert.Equal(reason, row.Reason);
        Assert.Equal(second.Text("lotCode"), row.RecordCode);
        var tooLong = await (await owner.PutHarvestLotAsync(third.Id(), Correction(third, HarvestFields.OfLanes(1), reason + "x"))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Reason", tooLong.Text("field"));
    }

    // A removed code stays closed when the whole cycle goes: nothing but the audit rows remembers it.
    [SqlServerFact]
    public async Task A_code_stays_given_after_the_lot_its_session_and_its_cycle_are_all_gone()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId, lanes: [1]);
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.RemoveSeedingAsync(raft.Session.Id(), rowVersion: raft.Session.Text("rowVersion"))).StatusCode);
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{raft.CycleId}");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(raft.CycleId, cycle.Text("rowVersion"))).StatusCode);

        var newCycle = (await owner.StartCycleAsync(raft.RaftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(newCycle, raft.Lot.Session([1]));
        var next = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        Assert.Equal($"{lot.Text("lotCode")}-2", next.Text("lotCode"));
    }

    // The filtered index names one action: other actions of a lot, and of other records with the same code, write as often as they are done, on the real paths.
    [SqlServerFact]
    public async Task Every_other_audit_row_of_a_lot_and_of_a_record_with_the_same_code_still_writes()
    {
        var (host, owner, zoneId) = await StartAsync("KV81");
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId, "R1");
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        for (var cages = 1; cages <= 3; cages++)
        {
            var read = await owner.GetHarvestLotAsync(lot.Id());
            await (await owner.PutHarvestLotAsync(lot.Id(), Correction(read, new HarvestFields { Cages = cages.ToString() }))).ReadAsync(HttpStatusCode.OK);
        }

        using (var scope = host.Api.Services.CreateScope())
        {
            var status = scope.ServiceProvider.GetRequiredService<HarvestLotStatusService>();
            var read = await scope.ServiceProvider.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lot.Id(), default);
            Assert.Null(status.Close(read!, Today, 0m, null));
            Assert.True(status.Reopen(read!, Today, HarvestLotReopenCause.SalesReturnBackForSale, null));
            Assert.Null(status.Close(read!, Today, 0m, null));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        // A raft whose code spells the lot code: its own Created row holds the same text.
        var sameCode = await owner.CreateRaftAsync(zoneId, NewRaftBody(lot.Text("lotCode")));
        Assert.NotEqual(lot.Id(), sameCode.Id());
        await host.UseDbAsync(db => db.AuditEvents.AddRange(
            new OMTS.Domain.Entities.Platform.AuditEvent { Action = "Raft.Created", RecordType = "Raft", RecordCode = lot.Text("lotCode"), OccurredAt = DateTimeOffset.UtcNow },
            new OMTS.Domain.Entities.Platform.AuditEvent { Action = "Raft.Created", RecordType = "Raft", RecordCode = lot.Text("lotCode"), OccurredAt = DateTimeOffset.UtcNow }));

        var last = await owner.GetHarvestLotAsync(lot.Id());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(lot.Id(), "mistake", last.Text("rowVersion"))).StatusCode);
        var actions = await host.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == lot.Id()).Select(e => e.Action).ToListAsync());
        Assert.Equal(1, actions.Count(a => a == HarvestLotAudit.Created));
        Assert.Equal(3, actions.Count(a => a == HarvestLotAudit.Corrected));
        Assert.Equal(1, actions.Count(a => a == HarvestLotAudit.Removed));
        Assert.Equal(2, actions.Count(a => a == HarvestLotAudit.Closed));
    }

    // ACCOUNT-10 has no route yet; what it will read must read as before: by record, by zone and period, and the rows of a removed lot.
    [SqlServerFact]
    public async Task The_audit_rows_of_a_removed_lot_read_by_record_by_code_and_by_zone_as_a_search_will()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"))).StatusCode);
        var code = lot.Text("lotCode");

        var byRecord = await host.QueryAsync(db => db.AuditEvents.Where(e => e.RecordType == HarvestLotAudit.RecordType && e.RecordId == lot.Id()).OrderBy(e => e.OccurredAt).Select(e => e.Action).ToListAsync());
        var byCode = await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordCode == code));
        var byPrefix = await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordCode != null && e.RecordCode.Contains(code)));
        var byZone = await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.ZoneId == zoneId && e.OccurredAt > DateTimeOffset.UtcNow.AddHours(-1) && e.RecordType == HarvestLotAudit.RecordType));

        Assert.Equal([HarvestLotAudit.Created, HarvestLotAudit.Removed], byRecord);
        Assert.Equal((2, 2, 2), (byCode, byPrefix, byZone));
    }

    // SALE-05 of a Zone Manager goes through the list of zone ids, which only SQL Server turns into a query of its own.
    [SqlServerFact]
    public async Task A_manager_of_two_zones_reads_both_and_nothing_else_on_a_real_database_and_the_list_of_many_lots_stays_quick()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var secondZone = (await owner.CreateZoneAsync()).Id();
        var thirdZone = (await owner.CreateZoneAsync()).Id();
        var (one, two, three) = (await SeededAsync(owner, zoneId), await SeededAsync(owner, secondZone), await SeededAsync(owner, thirdZone));
        var lots = new[] { await owner.HarvestLotAsync(one.RaftId), await owner.HarvestLotAsync(two.RaftId), await owner.HarvestLotAsync(three.RaftId) };
        var manager = await host.StaffInZoneAsync(zoneId);
        await host.Api.GrantAsync(manager.Account, OMTS.Domain.Enums.StaffRole.ZoneManager, secondZone, host.Owner.Account.Id);

        var read = (await manager.Client.LotsForSaleAsync()).ForSaleCodes();

        Assert.Equal(new[] { lots[0].Text("lotCode"), lots[1].Text("lotCode") }.Order(), read.Order());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.GetAsync($"/api/harvest-lots/for-sale?zoneId={thirdZone}")).StatusCode);

        // The size of the unpaged answer: the auto close (H2) keeps this few in use; here it is 3000 lots open for sale at once.
        var (cycleId, raftId) = (one.CycleId, one.RaftId);
        await host.UseDbAsync(db =>
        {
            for (var i = 0; i < 3000; i++)
            {
                db.HarvestLots.Add(new HarvestLot
                {
                    LotCode = $"BULK-{i:D5}", RaftId = raftId, CycleId = cycleId, HarvestedAt = DateTimeOffset.UtcNow, HarvestDate = Today, CageCount = 8, KgPerCage = 50m, EstimatedKg = 400m,
                    RecordedByAccountId = host.Owner.Account.Id,
                });
            }
        });
        for (var run = 0; run < 3; run++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var answer = await owner.GetAsync("/api/harvest-lots/for-sale");
            var body = await answer.Content.ReadAsStringAsync();
            watch.Stop();
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            output.WriteLine($"for-sale of 3003 lots: {watch.ElapsedMilliseconds} ms, {body.Length} characters");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.ElapsedMilliseconds} ms");
        }

        var counted = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(3003, await owner.ForSaleCountAsync());
        output.WriteLine($"count: {counted.ElapsedMilliseconds} ms");
    }

    // BR-CYCLE-07 at the worst moment: the save fails after the reminder was prepared, so nobody is told and the marker stays empty; the send again tells once.
    [SqlServerFact]
    public async Task A_correction_finishing_the_last_lane_that_loses_at_its_save_tells_nobody_and_the_send_again_tells_once()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId, lanes: [1]);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1).FinishedLanes(1)));
        await hold.Reached;
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{raft.CycleId}");
        await owner.EditCycleAsync(raft.CycleId, new CycleBody(cycle.Date("startDate"), TargetYieldKg: 100m, RowVersion: cycle.Text("rowVersion")));
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Equal(0, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "CYCLE_CLOSE_REMINDER")));
        Assert.Null(await host.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == raft.CycleId).Select(c => c.CloseRemindedAt).SingleAsync()));
        Assert.DoesNotContain(host.Api.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Account.Id.ToString()), m => m.Subject == "Reminder to close a cultivation cycle");

        await (await manager.Client.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1).FinishedLanes(1)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "CYCLE_CLOSE_REMINDER" && n.RecipientAccountId == manager.Account.Id)));
    }

    [SqlServerFact]
    public async Task A_correction_held_at_its_save_loses_to_a_removal_with_409_and_the_lot_stays_gone()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1, 2) with { Photos = [Png] }));
        await hold.Reached;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"))).StatusCode);
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Equal(0, await host.QueryAsync(db => db.HarvestLots.CountAsync()));
        Assert.Equal(0, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == HarvestLotAudit.Corrected)));
        await AssertFilesMatchAsync(host);
    }

    [SqlServerFact]
    public async Task A_removal_held_at_its_save_loses_to_a_correction_with_409_and_the_lot_stands_corrected()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"));
        await hold.Reached;
        await (await owner.PutHarvestLotAsync(lot.Id(), Correction(lot, new HarvestFields { Lanes = ["1"], Cages = "9" }))).ReadAsync(HttpStatusCode.OK);
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Equal(9, await host.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot.Id()).Select(l => l.CageCount).SingleAsync()));
        await AssertFilesMatchAsync(host);
    }

    [SqlServerFact]
    public async Task A_second_removal_held_at_its_save_answers_409_and_the_audit_keeps_one_row()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"));
        await hold.Reached;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"))).StatusCode);
        hold.Release();

        await AssertAnsweredAsync(await pending);
        Assert.Equal(HttpStatusCode.Conflict, (await pending).StatusCode);
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == HarvestLotAudit.Removed)));
    }

    // BR-CYCLE-02: a session with a lane in a lot stays. Both orders at the worst moment: one of them must fail, or a lot would stand on a lane with no seed.
    [SqlServerFact]
    public async Task A_correction_adding_a_lane_held_at_its_save_loses_to_the_removal_of_the_session_of_that_lane()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId, lanes: [1]);
        var session = await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([2]));
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1, 2)));
        await hold.Reached;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.RemoveSeedingAsync(session.Id(), rowVersion: session.Text("rowVersion"))).StatusCode);
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Equal([1], (await owner.GetHarvestLotAsync(lot.Id())).Numbers("laneNumbers"));
    }

    [SqlServerFact]
    public async Task The_removal_of_a_session_held_at_its_save_loses_to_a_correction_adding_its_lane()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId, lanes: [1]);
        var session = await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([2]));
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var (api, held, hold) = await HeldAsync(host, host.Owner.Account.Username);
        await using var _api = api;

        var pending = held.RemoveSeedingAsync(session.Id(), rowVersion: session.Text("rowVersion"));
        await hold.Reached;
        await (await manager.Client.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1, 2)))).ReadAsync(HttpStatusCode.OK);
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Equal(2, await host.QueryAsync(db => db.SeedingRecords.CountAsync()));
    }

    // A correction of a session moving its lane past the date of the lot, against a correction adding that lane: the lot would precede the seed.
    [SqlServerFact]
    public async Task A_correction_adding_a_lane_held_at_its_save_loses_to_a_session_correction_that_moves_its_seed()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId, lanes: [1]);
        var session = await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([2]));
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1, 2)));
        await hold.Reached;
        (await owner.PutSeedingAsync(session.Id(), session.AsCorrection() with { SeedingDate = Today, LaneNumbers = [3] })).EnsureSuccessStatusCode();
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Equal([1], (await owner.GetHarvestLotAsync(lot.Id())).Numbers("laneNumbers"));
    }

    // Lot status writers do not touch the cycle row: the row version of the lot alone stands between them and a correction or a removal, in all four orders.
    [SqlServerFact]
    public async Task A_close_by_the_status_service_held_at_its_save_loses_to_a_correction_and_to_a_removal()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        foreach (var removeIt in new[] { false, true })
        {
            var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(removeIt ? 2 : 1));
            using var scope = host.Api.Services.CreateScope();
            var read = await scope.ServiceProvider.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lot.Id(), default);
            Assert.Null(scope.ServiceProvider.GetRequiredService<HarvestLotStatusService>().Close(read!, Today, 0m, null));

            var answer = removeIt
                ? await owner.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"))
                : await owner.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1) with { Cages = "9" }));
            Assert.True(answer.IsSuccessStatusCode, $"{(int)answer.StatusCode}");

            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
        }
    }

    [SqlServerFact]
    public async Task A_correction_or_a_removal_held_at_its_save_loses_to_a_close_by_the_status_service()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId);
        foreach (var removeIt in new[] { false, true })
        {
            var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(removeIt ? 2 : 1));
            var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
            await using var _api = api;

            var pending = removeIt
                ? held.PostRemoveLotAsync(lot.Id(), "mistake", lot.Text("rowVersion"))
                : held.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1) with { Cages = "9" }));
            await hold.Reached;
            using (var scope = host.Api.Services.CreateScope())
            {
                var read = await scope.ServiceProvider.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lot.Id(), default);
                Assert.Null(scope.ServiceProvider.GetRequiredService<HarvestLotStatusService>().Close(read!, Today, 0m, null));
                await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            }

            hold.Release();
            Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
            Assert.Equal(StatusIds.HarvestLot.Closed, await host.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot.Id()).Select(l => l.StatusId).SingleAsync()));
        }
    }

    // BR-HARVEST-01: a lane finished in a lot cannot be in a later one. The two orders at the worst moment: the cycle row makes one fail, and a send again is checked against the new states.
    [SqlServerFact]
    public async Task A_correction_finishing_a_lane_and_a_new_lot_on_that_lane_cannot_both_stand()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1).FinishedLanes(1)));
        await hold.Reached;
        var later = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Empty((await owner.GetHarvestLotAsync(lot.Id())).Numbers("finishedLaneNumbers"));
        var again = await (await manager.Client.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1).FinishedLanes(1)))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Finished lanes", again.Text("field"));
        Assert.Equal([1], later.Numbers("laneNumbers"));
    }

    [SqlServerFact]
    public async Task A_new_lot_held_at_its_save_loses_to_a_correction_finishing_its_lane_and_a_send_again_is_refused()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId);
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var (api, held, hold) = await HeldAsync(host, manager.Account.Username);
        await using var _api = api;

        var pending = held.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });
        await hold.Reached;
        await (await owner.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1).FinishedLanes(1)))).ReadAsync(HttpStatusCode.OK);
        hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Equal(1, await host.QueryAsync(db => db.HarvestLots.CountAsync()));
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1))).StatusCode);
        await AssertFilesMatchAsync(host);
    }

    // The audit index on the record path: however many times the code is lost, the answer stays 409 and the uploads go; never a 500.
    [SqlServerFact]
    public async Task A_lot_that_loses_the_code_on_every_attempt_answers_409_and_keeps_no_file()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId);
        var api = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<UnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new AlwaysDuplicateUnitOfWork(provider.GetRequiredService<UnitOfWork>()));
        }));
        await using var _api = api;
        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(host.Api.CreateClient(), manager.Account.Username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var answer = await client.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });

        Assert.Equal("MSG06", await answer.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Empty(host.StoredKeys(FileCategories.HarvestLotPhotos));
        Assert.Equal(0, await host.QueryAsync(db => db.HarvestLots.CountAsync()));
    }

    private sealed class AlwaysDuplicateUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new DuplicateRecordException(new InvalidOperationException("Test: the code was taken."));

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
    }

    // A database EF Core creates, and Azure SQL, read versions of the rows. One made by hand, or restored from one, has locking reads: the same parallel work there, where a read can be
    // the deadlock victim outside the save, which the API's exception handler maps to 409. It depends on timing, so a run may pass; see the report for how often it failed before that.
    [SqlServerFact]
    public async Task L2_everything_on_one_cycle_in_parallel_never_answers_500_with_locking_reads_too()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(host.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE";
            await alter.ExecuteNonQueryAsync();
        }

        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await RunEverythingInParallelAsync(host, owner, zoneId, Rounds * 4);
    }

    // In parallel, many rounds: every kind of save and read of one cycle together. The answers add up and no 500 comes back, the reads included.
    [SqlServerFact]
    public async Task Everything_on_one_cycle_in_parallel_never_answers_500()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        await RunEverythingInParallelAsync(host, owner, zoneId, Rounds * 4);
    }

    private static async Task RunEverythingInParallelAsync(FarmingSqlServerHost host, HttpClient owner, Guid zoneId, int rounds)
    {
        var manager = await host.StaffInZoneAsync(zoneId);
        var raft = await SeededAsync(owner, zoneId);
        for (var round = 0; round < rounds; round++)
        {
            var first = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });
            var second = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["2"], Photos = [Png] });
            var third = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(3));

            var answers = await Task.WhenAll(
                owner.PutHarvestLotAsync(first.Id(), Correction(first, HarvestFields.OfLanes(1, 4) with { Photos = [Png] })),
                owner.PostRemoveLotAsync(first.Id(), "mistake", first.Text("rowVersion")),
                manager.Client.PutHarvestLotAsync(second.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["2"], Photos = [Jpeg] }, "Wrong photo", second.Text("rowVersion"))),
                owner.PostRemoveLotAsync(third.Id(), "mistake", third.Text("rowVersion")),
                owner.PostRemoveLotAsync(third.Id(), "mistake", third.Text("rowVersion")),
                manager.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["5"], Photos = [Jpeg] }),
                owner.GetAsync($"/api/harvest-lots/{first.Id()}"),
                owner.GetAsync($"/api/rafts/{raft.RaftId}/harvest-lots"),
                owner.GetAsync("/api/harvest-lots/for-sale"),
                owner.GetAsync("/api/harvest-lots/for-sale/count"),
                owner.GetAsync($"/api/rafts/{raft.RaftId}/lanes/1/progress"));

            await AssertAnsweredAsync(answers);
            await AssertFilesMatchAsync(host);
        }

        var codes = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == HarvestLotAudit.Created).Select(e => e.RecordCode!).ToListAsync());
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    // A cancel in the same moment as the last record going away: 204 or MSG-GEN-06 or MSG06, never a broken key.
    [SqlServerFact]
    public async Task A_cancel_of_the_cycle_against_a_correction_a_removal_of_the_last_lot_and_its_session_never_answers_500()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId, lanes: [1]);
            var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
            var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{raft.CycleId}");

            var answers = await Task.WhenAll(
                owner.PutHarvestLotAsync(lot.Id(), Correction(lot, HarvestFields.OfLanes(1).FinishedLanes(1))),
                owner.CancelCycleAsync(raft.CycleId, cycle.Text("rowVersion")));
            await AssertAnsweredAsync(answers);

            var fresh = await owner.GetHarvestLotAsync(lot.Id());
            var session = await owner.GetSeedingAsync(raft.Session.Id());
            cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{raft.CycleId}");
            var removed = await Task.WhenAll(
                owner.PostRemoveLotAsync(lot.Id(), "mistake", fresh.Text("rowVersion")),
                owner.RemoveSeedingAsync(raft.Session.Id(), rowVersion: session.Text("rowVersion")),
                owner.CancelCycleAsync(raft.CycleId, cycle.Text("rowVersion")));
            await AssertAnsweredAsync(removed);
            if (await host.QueryAsync(db => db.HarvestLots.AnyAsync(l => l.Id == lot.Id())))
            {
                Assert.True(await host.QueryAsync(db => db.SeedingRecords.AnyAsync(s => s.Id == raft.Session.Id())), "A lot stands on a lane with no session.");
            }
        }
    }

    // Two rafts sharing a code, corrections on one and records on the other: the audit index and the row versions never surface as a 500.
    [SqlServerFact]
    public async Task Records_on_rafts_that_share_a_code_with_removals_and_corrections_in_parallel_answer_2xx_or_409_and_give_each_code_once()
    {
        var (host, owner, zoneId) = await StartAsync("KV82");
        await using var _ = host;
        for (var round = 0; round < Rounds; round++)
        {
            var plain = await SeededAsync(owner, zoneId, $"R{round}");
            var prefixed = await SeededAsync(owner, zoneId, $"KV82-R{round}");
            var top = await owner.HarvestLotAsync(plain.RaftId, HarvestFields.OfLanes(1));
            var other = await owner.HarvestLotAsync(prefixed.RaftId, HarvestFields.OfLanes(1));

            await AssertAnsweredAsync(await Task.WhenAll(
                owner.PostRemoveLotAsync(top.Id(), "mistake", top.Text("rowVersion")),
                owner.PostRemoveLotAsync(other.Id(), "mistake", other.Text("rowVersion")),
                owner.PostHarvestAsync(plain.RaftId, HarvestFields.OfLanes(2)),
                owner.PostHarvestAsync(prefixed.RaftId, HarvestFields.OfLanes(2)),
                owner.PostHarvestAsync(prefixed.RaftId, HarvestFields.OfLanes(3)),
                owner.PostHarvestAsync(plain.RaftId, HarvestFields.OfLanes(3))));
        }

        var given = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == HarvestLotAudit.Created).Select(e => e.RecordCode!).ToListAsync());
        Assert.Equal(given.Count, given.Distinct().Count());
        var inUse = await host.QueryAsync(db => db.HarvestLots.Select(l => l.LotCode).ToListAsync());
        var removed = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == HarvestLotAudit.Removed).Select(e => e.RecordCode!).ToListAsync());
        Assert.Empty(inUse.Intersect(removed));
    }
}
