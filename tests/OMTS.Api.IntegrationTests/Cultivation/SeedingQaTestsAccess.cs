using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

/// <summary>QA of the own-record rule (BR-CYCLE-02), GFR-02 and §I.4.4 on seeding, and of business dates around midnight in Vietnam.</summary>
public class SeedingQaTestsAccess(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<HttpClient> StaffWithPairsAsync(params (StaffRole Role, Guid? ZoneId)[] pairs)
    {
        var password = NewPassword();
        var staff = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        foreach (var (role, zoneId) in pairs)
        {
            await factory.GrantAsync(staff, role, zoneId is { } id ? await factory.ZoneEntityAsync(id) : null);
        }

        return await factory.LoginAsync(staff.Username, password);
    }

    // BR-CYCLE-02: an All zones manager has the same own-record rule as a manager of the zone.
    [Fact]
    public async Task An_all_zones_manager_corrects_only_what_they_recorded_and_the_flag_says_so()
    {
        var setup = await factory.CycleAsync();
        var lot = await setup.Owner.Client.SeedLotAsync();
        var (inZone, allZones) = (await factory.ManagerAsync(setup.ZoneId), await factory.StaffAsync(null));
        var ofInZone = await inZone.Client.SeedingRecordAsync(setup.CycleId, lot.Session([1]));
        var ofAllZones = await allZones.Client.SeedingRecordAsync(setup.CycleId, lot.Session([2]));

        Assert.False((await allZones.Client.GetSeedingAsync(ofInZone.Id())).Flag("canCorrect"));
        Assert.Equal("MSG-GEN-08", await (await allZones.Client.PutSeedingAsync(ofInZone.Id(), ofInZone.AsCorrection() with { Quantity = 1m })).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await allZones.Client.RemoveSeedingAsync(ofInZone.Id())).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await inZone.Client.RemoveSeedingAsync(ofAllZones.Id())).ErrorCodeAsync());

        Assert.True((await allZones.Client.GetSeedingAsync(ofAllZones.Id())).Flag("canCorrect"));
        await (await allZones.Client.PutSeedingAsync(ofAllZones.Id(), ofAllZones.AsCorrection() with { Quantity = 1m })).ReadAsync(HttpStatusCode.OK);
        Assert.True((await setup.Owner.Client.GetSeedingAsync(ofAllZones.Id())).Flag("canCorrect"));
        await (await setup.Owner.Client.RemoveSeedingAsync(ofAllZones.Id())).ReadAsync(HttpStatusCode.NoContent);
    }

    // GFR-02, BR-FARM-08: a revoke applies from the next action, for what the person recorded too; a new pair gives it back; the session keeps who recorded it.
    [Fact]
    public async Task A_revoked_pair_is_refused_on_every_seeding_route_and_a_new_pair_gives_the_session_back()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var zoneB = await factory.ZoneEntityAsync((await owner.CreateZoneAsync()).Id());
        await factory.GrantAsync(manager.Account, StaffRole.ZoneManager, zoneB);
        var record = await manager.Client.SeedingRecordAsync(setup.CycleId, lot.Session([1]));

        (await owner.PostAsJsonAsync($"/api/accounts/{manager.Account.Id}/grants/{manager.Grant!.Id}/revoke", new { reason = "Moved" })).EnsureSuccessStatusCode();

        foreach (var answer in new[]
        {
            await manager.Client.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/seeding-records"),
            await manager.Client.GetAsync($"/api/seeding-records/{record.Id()}"),
            await manager.Client.GetAsync($"/api/rafts/{setup.RaftId}/seed-lots"),
            await manager.Client.GetAsync($"/api/rafts/{setup.RaftId}/lanes/1/progress"),
            await manager.Client.CompletionPreviewAsync(setup.CycleId),
            await manager.Client.PostSeedingAsync(setup.CycleId, lot.Session([2])),
            await manager.Client.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = 1m }),
            await manager.Client.RemoveSeedingAsync(record.Id()),
            await manager.Client.PostSeedingCompleteAsync(setup.CycleId),
        })
        {
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        }

        var kept = await owner.GetSeedingAsync(record.Id());
        Assert.Equal(manager.Account.Id, kept.GetProperty("recordedByAccountId").GetGuid());
        Assert.False(string.IsNullOrEmpty(kept.Text("recordedByName")));

        await factory.GrantAsync(manager.Account, StaffRole.ZoneManager, await factory.ZoneEntityAsync(setup.ZoneId));
        Assert.True((await manager.Client.GetSeedingAsync(record.Id())).Flag("canCorrect"));
    }

    // BR-FARM-08: each role is limited to the zones of its pairs, and an Accountant pair opens no seeding route (§I.4.4).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_manager_elsewhere_who_is_accountant_here_is_refused_here_and_works_there(bool accountantOfAllZones)
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1]));
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var cycleB = await owner.StartCycleAsync(raftB, new CycleBody(Today.AddDays(-10)));
        var mixed = await StaffWithPairsAsync((StaffRole.ZoneManager, zoneB), (StaffRole.Accountant, accountantOfAllZones ? null : setup.ZoneId));

        foreach (var answer in new[]
        {
            await mixed.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/seeding-records"),
            await mixed.GetAsync($"/api/rafts/{setup.RaftId}/seed-lots"),
            await mixed.PostSeedingAsync(setup.CycleId, lot.Session([2])),
            await mixed.PutSeedingAsync(record.Id(), record.AsCorrection()),
            await mixed.PostSeedingCompleteAsync(setup.CycleId),
        })
        {
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        }

        await mixed.RecordSeedingAsync(cycleB.Id(), lot.Session([1]));
    }

    // GFR-02: a refused action shows MSG-GEN-08 and nothing of the record.
    [Fact]
    public async Task A_refusal_says_nothing_of_the_session_and_changes_nothing()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1]));
        var outside = await factory.ManagerAsync((await owner.CreateZoneAsync()).Id());

        foreach (var answer in new[]
        {
            await outside.Client.GetAsync($"/api/seeding-records/{record.Id()}"),
            await outside.Client.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = 99m }),
            await outside.Client.RemoveSeedingAsync(record.Id()),
        })
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.DoesNotContain(record.Text("raftCode"), text);
            Assert.DoesNotContain(lot.Code, text);
        }

        Assert.Equal(10m, (await owner.GetSeedingAsync(record.Id())).Money("quantity"));
    }

    // An unknown id answers the same way to all, and a record of another zone is not told apart from a refusal by its content.
    [Fact]
    public async Task A_zone_manager_may_be_refused_on_the_session_of_a_discontinued_zone_only_for_writes()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var lot = await setup.Owner.Client.SeedLotAsync();
        var record = await manager.Client.SeedingRecordAsync(setup.CycleId, lot.Session());
        await factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);

        // BR-FARM-08: the pairs of a discontinued zone stay Effective, so the history is read.
        await (await manager.Client.GetAsync($"/api/seeding-records/{record.Id()}")).ReadAsync(HttpStatusCode.OK);
        await (await manager.Client.GetAsync($"/api/rafts/{setup.RaftId}/lanes/1/progress")).ReadAsync(HttpStatusCode.OK);
        await (await manager.Client.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/seeding-records")).ReadAsync(HttpStatusCode.OK);

        var refused = await (await manager.Client.CompletionPreviewAsync(setup.CycleId)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
    }

    // CYCLE-01.2, BR-CYCLE-01: the cycle view gives the same answer as the route, and giving back the last session gives the cancel back only when no log holds it.
    [Fact]
    public async Task The_cancel_stays_blocked_by_a_log_after_the_last_session_is_removed()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session());
        await owner.RecordLogAsync(setup.CycleId);

        await (await owner.RemoveSeedingAsync(record.Id())).ReadAsync(HttpStatusCode.NoContent);

        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");
        Assert.False(cycle.Flag("canCancel"));
        Assert.Equal("MSG-GEN-06", await (await owner.CancelCycleAsync(setup.CycleId, cycle.Text("rowVersion"))).ErrorCodeAsync());
    }
}

/// <summary>QA of CYCLE-02 around midnight in Vietnam: 16:59 and 17:01 UTC are different business days, on one UTC day.</summary>
public class SeedingQaTestsMidnight
{
    private static DateTimeOffset MidnightInVietnam(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7));

    private static async Task<(CyclesQaClockApiFactory Api, HttpClient Owner, Guid CycleId, SeedLotMade Lot)> ArrangeAsync()
    {
        var api = new CyclesQaClockApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        var raft = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var cycle = await owner.StartCycleAsync(raft, new CycleBody(Today.AddDays(-10)));
        return (api, owner, cycle.Id(), await owner.SeedLotAsync());
    }

    [Fact]
    public async Task A_minute_after_midnight_the_new_day_can_be_seeded_though_the_utc_date_is_the_old_one()
    {
        var (api, owner, cycleId, lot) = await ArrangeAsync();
        using var host = api;
        var day = Today;
        api.Clock.At = MidnightInVietnam(day).AddMinutes(1);
        Assert.Equal(day.AddDays(-1), DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime));

        Assert.Equal(day, (await owner.SeedingRecordAsync(cycleId, lot.Session([1], day))).Date("seedingDate"));
        Assert.Equal(["Seeding date"], await (await owner.PostSeedingAsync(cycleId, lot.Session([2], day.AddDays(1)))).InvalidFieldsAsync());

        Assert.Equal(day, (await owner.MarkSeedingCompleteAsync(cycleId)).GetProperty("cycle").Date("seedingCompletedOn"));
    }

    [Fact]
    public async Task A_minute_before_midnight_the_new_day_cannot_be_seeded_and_the_old_day_can()
    {
        var (api, owner, cycleId, lot) = await ArrangeAsync();
        using var host = api;
        var day = Today;
        api.Clock.At = MidnightInVietnam(day).AddMinutes(-1);

        Assert.Equal(["Seeding date"], await (await owner.PostSeedingAsync(cycleId, lot.Session([1], day))).InvalidFieldsAsync());
        var record = await owner.SeedingRecordAsync(cycleId, lot.Session([1], day.AddDays(-1)));
        Assert.Equal(["Seeding date"], await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { SeedingDate = day })).InvalidFieldsAsync());

        // The lane state is that of the same business day: the session counts, and a re-hang on it is allowed.
        Assert.Equal("Growing", (await owner.SeedingListAsync(cycleId)).StateOf(1));
    }

    // The lot received today (local) on a UTC day still yesterday: the same day may be seeded, a day before may not.
    [Fact]
    public async Task A_lot_received_today_just_after_midnight_seeds_today_and_not_before()
    {
        var (api, owner, cycleId, _) = await ArrangeAsync();
        using var host = api;
        var day = Today;
        api.Clock.At = MidnightInVietnam(day).AddMinutes(1);
        var lot = await owner.SeedLotAsync(received: day);

        Assert.Equal(["Seeding date"], await (await owner.PostSeedingAsync(cycleId, lot.Session([1], day.AddDays(-1)))).InvalidFieldsAsync());
        await owner.RecordSeedingAsync(cycleId, lot.Session([1], day));
    }
}
