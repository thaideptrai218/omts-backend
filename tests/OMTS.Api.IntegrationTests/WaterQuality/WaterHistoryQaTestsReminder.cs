using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.WaterQuality.WaterOverdueTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of the GFR-04 boundaries on pinned dates; each test has its own database, since the job scans every zone.</summary>
public class WaterHistoryQaTestsReminder
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    private static DateOnly D(string date) => DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-01-31", "2026-02-28", "2026-03-01")]
    [InlineData("2026-02-28", "2026-03-28", "2026-03-29")]
    [InlineData("2028-02-29", "2028-03-29", "2028-03-30")]
    [InlineData("2026-12-31", "2027-01-31", "2027-02-01")]
    public async Task One_calendar_month_is_on_time_and_the_next_day_is_due_also_at_month_ends(string start, string onTime, string due)
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(D(start));

        Assert.Equal(0, await api.RemindOnAsync(D(onTime)));
        Assert.Equal(1, await api.RemindOnAsync(D(due)));
    }

    [Fact]
    public async Task The_repeat_at_month_ends_is_one_reminder_a_month_and_never_two_in_a_short_month()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(D("2025-12-01"));

        Assert.Equal(1, await api.RemindOnAsync(D("2026-01-31")));
        Assert.Equal(0, await api.RemindOnAsync(D("2026-02-27")));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-02-28")));
        Assert.Equal(0, await api.RemindOnAsync(D("2026-03-27")));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-03-28")));
    }

    // 22/09 16:59:59 UTC is 22/09 23:59:59 in Vietnam; 22/09 17:00 UTC is 23/09 00:00.
    [Fact]
    public async Task The_measurement_counts_by_its_vietnam_date_on_either_side_of_midnight()
    {
        await using var api = new WaterApiFactory();
        var before = await api.ZoneAsync();
        var after = await api.ZoneAsync();
        await before.OpenCycleAsync(D("2026-01-01"));
        await after.OpenCycleAsync(D("2026-01-01"));
        await AddAtAsync(before, new DateTimeOffset(2026, 9, 22, 16, 59, 59, TimeSpan.Zero));
        await AddAtAsync(after, new DateTimeOffset(2026, 9, 22, 17, 0, 0, TimeSpan.Zero));

        Assert.Equal(0, await api.RemindOnAsync(D("2026-10-22")));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-10-23")));
        Assert.All(await api.RemindersOfAsync(before.Zone), n => Assert.Contains($"since {Named(D("2026-09-22"))}.", n.Body));
        Assert.Empty(await api.RemindersOfAsync(after.Zone));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-10-24")));
        var afterMidnight = await api.RemindersOfAsync(after.Zone);
        Assert.NotEmpty(afterMidnight);
        Assert.All(afterMidnight, n => Assert.Contains($"since {Named(D("2026-09-23"))}.", n.Body));
    }

    // Tests the OpenedAt port only: the merge with OMTS-220 has a StartDate with no time, and this test goes with OpenedAt.
    [Fact]
    public async Task The_cycle_start_counts_by_its_vietnam_date()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await api.UseDbAsync(db => db.CultivationCycles.Add(new CultivationCycle { RaftId = z.Raft.Id, OpenedAt = new DateTimeOffset(2026, 5, 15, 17, 0, 0, TimeSpan.Zero) }));

        Assert.Equal(0, await api.RemindOnAsync(D("2026-06-16")));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-06-17")));
        Assert.Contains($"since {Named(D("2026-05-16"))}.", Assert.Single(await api.RemindersOfAsync(z.Zone)).Body);
    }

    // A Closed cycle is left out of "the earliest Open cycle": with a later Open one on another raft, the later one counts.
    [Fact]
    public async Task An_older_closed_cycle_does_not_move_the_base_of_the_open_one()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var open = D("2026-05-15");
        await api.UseDbAsync(db => db.CultivationCycles.Add(ClosedCycle(z.Raft.Id, D("2025-01-01"))));
        await z.OpenCycleAsync(open, await api.RaftAsync(z.Zone));

        Assert.Equal(0, await api.RemindOnAsync(open.AddMonths(1)));
        Assert.Equal(1, await api.RemindOnAsync(open.AddMonths(1).AddDays(1)));
    }

    // FARM-04: a raft with an Open cycle cannot be discontinued, so a Discontinued raft with one takes a change outside the API.
    [Fact]
    public async Task A_raft_with_an_open_cycle_cannot_be_discontinued_and_one_changed_in_the_database_still_counts()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(Today.AddMonths(-3));
        var raft = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{z.Raft.Id}");

        var refused = await z.Owner.PostAsJsonAsync($"/api/rafts/{z.Raft.Id}/discontinue", new { rowVersion = raft.Text("rowVersion") });

        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        await api.UpdateAsync<Raft>(z.Raft.Id, r => r.StatusId = StatusIds.Usage.Discontinued);
        Assert.Equal(1, await api.RemindOnAsync(Today));
    }

    [Fact]
    public async Task A_zone_discontinued_then_resumed_is_checked_again_and_keeps_its_last_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(D("2026-01-10"));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-02-11")));

        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);
        Assert.Equal(0, await api.RemindOnAsync(D("2026-03-11")));
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.InUse);

        Assert.Equal(1, await api.RemindOnAsync(D("2026-03-12")));
        Assert.Equal(0, await api.RemindOnAsync(D("2026-03-13")));
    }

    [Fact]
    public async Task A_void_of_the_only_measurement_puts_the_base_back_on_the_cycle_start()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var start = Today.AddMonths(-3);
        await z.OpenCycleAsync(start);
        var only = await z.RecordedAsync(7m, 20m);
        Assert.Equal(0, await api.RemindOnAsync(Today));

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(only.Id())).StatusCode);

        Assert.Equal(1, await api.RemindOnAsync(Today));
        Assert.All(await api.RemindersOfAsync(z.Zone), n => Assert.Contains($"since {Named(start)}.", n.Body));
    }

    // PM 03/11 dropped the re-arm: a measurement recorded after a reminder but measured before it does not bring the next one forward.
    [Fact]
    public async Task A_backdated_measurement_after_a_reminder_waits_a_month_from_that_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(D("2026-01-01"));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-02-20")));
        await z.AddMeasurementAsync(D("2026-02-10"));

        Assert.Equal(0, await api.RemindOnAsync(D("2026-03-11")));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-03-20")));
    }

    [Fact]
    public async Task One_account_with_a_zone_pair_and_an_all_zones_pair_gets_one_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        await api.GrantAsync(manager, StaffRole.ZoneManager, zone: null);
        await z.OpenCycleAsync(Today.AddMonths(-2));

        Assert.Equal(1, await api.RemindOnAsync(Today));

        Assert.Equal(manager.Id, Assert.Single(await api.RemindersOfAsync(z.Zone)).RecipientAccountId);
    }

    [Fact]
    public async Task A_zone_manager_granted_after_an_owner_reminder_gets_the_next_one_and_the_owner_does_not()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(D("2026-01-10"));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-02-11")));
        var (_, manager) = await api.StaffAsync(z.Zone);

        Assert.Equal(0, await api.RemindOnAsync(D("2026-02-12")));
        Assert.Equal(1, await api.RemindOnAsync(D("2026-03-11")));

        var recipients = (await api.RemindersOfAsync(z.Zone)).Select(n => n.RecipientAccountId).ToList();
        Assert.Equal(1, recipients.Count(id => id == manager.Id));
        Assert.Equal(1, recipients.Count(id => id == z.OwnerAccount.Id));
    }

    // GFR-04: tapping opens WATER-01 with the zone chosen, for the Zone Manager and for the Owner when the zone has none.
    [Fact]
    public async Task The_reminder_opens_on_its_zone_for_the_zone_manager_and_the_fallback_owner()
    {
        await using var api = new WaterApiFactory();
        var managed = await api.ZoneAsync();
        var unmanaged = await api.ZoneAsync();
        var (manager, account) = await api.StaffAsync(managed.Zone);
        await managed.OpenCycleAsync(Today.AddMonths(-2));
        await unmanaged.OpenCycleAsync(Today.AddMonths(-2));
        Assert.Equal(2, await api.RemindOnAsync(Today));
        var reminderOfManager = Assert.Single(await api.RemindersOfAsync(managed.Zone)).Id;
        var reminderOfOwner = (await api.RemindersOfAsync(unmanaged.Zone)).Single(n => n.RecipientAccountId == unmanaged.OwnerAccount.Id).Id;

        var openedByManager = await manager.GetFromJsonAsync<JsonElement>($"/api/me/notifications/{reminderOfManager}/open");
        var openedByOwner = await unmanaged.Owner.GetFromJsonAsync<JsonElement>($"/api/me/notifications/{reminderOfOwner}/open");

        Assert.Equal(managed.Zone.Id, openedByManager.GetProperty("zoneId").GetGuid());
        Assert.Equal(unmanaged.Zone.Id, openedByOwner.GetProperty("zoneId").GetGuid());
        Assert.Equal(account.Id, (await api.RemindersOfAsync(managed.Zone)).Single().RecipientAccountId);
    }

    // Finding L-1: GFR-04 reads the reminders "already sent from the notifications". The zone marker is set even when nobody
    // was notified, here no Zone Manager and the only Owner locked: nothing was sent, yet the zone waits a whole month.
    [Fact]
    public async Task QaL1_a_run_that_notified_nobody_does_not_hold_back_the_next_days_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(D("2026-01-10"));
        await api.UpdateAsync<Account>(z.OwnerAccount.Id, a => a.StatusId = StatusIds.Account.Locked);

        await api.RemindOnAsync(D("2026-03-01"));
        Assert.Empty(await api.RemindersOfAsync(z.Zone));
        await api.UpdateAsync<Account>(z.OwnerAccount.Id, a => a.StatusId = StatusIds.Account.Active);

        Assert.Equal(1, await api.RemindOnAsync(D("2026-03-02")));
    }

    private static Task AddAtAsync(WaterZone z, DateTimeOffset measuredAt) =>
        z.Api.UseDbAsync(db => db.WaterMeasurements.Add(new WaterMeasurement
        {
            ZoneId = z.Zone.Id,
            RaftId = z.Raft.Id,
            MeasuredAt = measuredAt,
            Ph = 7.5m,
            Salinity = 20m,
            Temperature = 25m,
            PhotoUrl = Guid.NewGuid().ToString("N"),
            ClientOperationId = Guid.NewGuid(),
        }));
}
