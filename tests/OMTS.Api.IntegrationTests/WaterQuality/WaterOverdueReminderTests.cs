using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.WaterQuality.WaterOverdueTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>GFR-04 and the last sentence of AC-04: which zones, which recipients and what the reminder says, on pinned dates.</summary>
public class WaterOverdueReminderTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    // AC-04: "The Zone Managers of a zone with an Open cycle and no active measurement for more than one month receive a
    // reminder to measure the water (MSG-WATER-06)."
    [Fact]
    public async Task The_zone_managers_of_a_zone_with_an_open_cycle_and_no_measurement_for_over_a_month_are_reminded()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var (_, accountant) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var (_, otherZone) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        var allZones = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(allZones, StaffRole.ZoneManager, zone: null);
        var start = Today.AddMonths(-2);
        await z.OpenCycleAsync(start);

        Assert.Equal(1, await api.RemindOnAsync(Today));

        var reminders = await api.RemindersOfAsync(z.Zone);
        Assert.Equal(new[] { manager.Id, allZones.Id }.Order(), reminders.Select(n => n.RecipientAccountId).Order());
        Assert.DoesNotContain(reminders, n => n.RecipientAccountId == z.OwnerAccount.Id || n.RecipientAccountId == accountant.Id || n.RecipientAccountId == otherZone.Id);
        // A normal type (BR-ACCOUNT-11). The zone id takes the tap to WATER-01 with the zone chosen.
        Assert.All(reminders, n =>
        {
            Assert.Equal($"Zone {z.Zone.Name} has had no water measurement since {Named(start)}. Please measure the water and record the result in OMTS.", n.Body);
            Assert.Equal(("MSG-WATER-06", false), (n.MessageCode, n.IsMandatory));
            Assert.Equal(("FarmingZone", z.Zone.Id), (n.RelatedRecordType!, n.RelatedRecordId!.Value));
            Assert.Contains("\"last_date\":\"2026-04-15\"", n.MessageParams);
        });
    }

    // GFR-04: "if the zone has no Zone Manager, it sends the reminder to the Cooperative Owner".
    [Fact]
    public async Task A_zone_without_a_zone_manager_reminds_every_owner_the_job_has_no_actor_to_leave_out()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var otherOwner = await api.AddAccountAsync(NewCode(), Password);
        await api.StaffAsync(z.Zone, StaffRole.Accountant);
        await api.StaffAsync((await api.ZoneAsync()).Zone);
        await z.OpenCycleAsync(Today.AddMonths(-2));

        Assert.Equal(1, await api.RemindOnAsync(Today));

        var recipients = (await api.RemindersOfAsync(z.Zone)).Select(n => n.RecipientAccountId).ToList();
        Assert.Contains(z.OwnerAccount.Id, recipients);
        Assert.Contains(otherOwner.Id, recipients);
        Assert.Equal(recipients.Count, await api.QueryAsync(db => db.Accounts.CountAsync(a => a.SubjectType == SubjectType.CooperativeOwner)));
    }

    [Fact]
    public async Task A_zone_manager_who_cannot_get_it_does_not_count_so_the_owner_is_reminded()
    {
        await using var api = new WaterApiFactory();
        var locked = await api.ZoneAsync();
        var revoked = await api.ZoneAsync();
        var (_, lockedManager) = await api.StaffAsync(locked.Zone);
        await api.UpdateAsync<Account>(lockedManager.Id, a => a.StatusId = StatusIds.Account.Locked);
        var (_, formerManager) = await api.StaffAsync(revoked.Zone);
        await api.UseDbAsync(db =>
        {
            foreach (var grant in db.AccessGrants.Where(g => g.AccountId == formerManager.Id))
            {
                grant.StatusId = StatusIds.AccessGrant.Revoked;
            }
        });
        await locked.OpenCycleAsync(Today.AddMonths(-2));
        await revoked.OpenCycleAsync(Today.AddMonths(-2));

        Assert.Equal(2, await api.RemindOnAsync(Today));

        foreach (var z in new[] { locked, revoked })
        {
            var recipients = (await api.RemindersOfAsync(z.Zone)).Select(n => n.RecipientAccountId).ToList();
            Assert.Contains(z.OwnerAccount.Id, recipients);
            Assert.DoesNotContain(lockedManager.Id, recipients);
            Assert.DoesNotContain(formerManager.Id, recipients);
        }
    }

    [Fact]
    public async Task No_open_cycle_no_reminder_however_old_the_last_measurement_is()
    {
        await using var api = new WaterApiFactory();
        var neverMeasured = await api.ZoneAsync();
        var measuredLongAgo = await api.ZoneAsync();
        await measuredLongAgo.AddMeasurementAsync(Today.AddMonths(-6));
        var closedCycle = await api.ZoneAsync();
        await closedCycle.AddMeasurementAsync(Today.AddMonths(-6));
        await api.UseDbAsync(db => db.CultivationCycles.Add(ClosedCycle(closedCycle.Raft.Id, Today.AddMonths(-8))));

        Assert.Equal(0, await api.RemindOnAsync(Today));

        Assert.False(await api.QueryAsync(db => db.Notifications.AnyAsync()));
    }

    [Fact]
    public async Task A_discontinued_zone_with_an_open_cycle_is_not_reminded()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(Today.AddMonths(-3));
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        Assert.Equal(0, await api.RemindOnAsync(Today));
    }

    // The measurement is older than the cycle: the month counts from the cycle, and the reminder names its start.
    [Fact]
    public async Task The_base_is_the_start_of_the_cycle_when_it_is_later_than_the_latest_measurement()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.AddMeasurementAsync(Today.AddMonths(-5));
        var start = new DateOnly(2026, 5, 15);
        await z.OpenCycleAsync(start);

        Assert.Equal(0, await api.RemindOnAsync(start.AddMonths(1)));
        Assert.Equal(1, await api.RemindOnAsync(start.AddMonths(1).AddDays(1)));

        Assert.All(await api.RemindersOfAsync(z.Zone), n => Assert.Contains($"since {Named(start)}.", n.Body));
    }

    [Fact]
    public async Task The_base_is_the_latest_measurement_when_it_is_later_than_the_cycle_start()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(Today.AddMonths(-5));
        var measured = new DateOnly(2026, 5, 15);
        await z.AddMeasurementAsync(measured.AddDays(-20));
        await z.AddMeasurementAsync(measured);

        Assert.Equal(0, await api.RemindOnAsync(measured.AddMonths(1)));
        Assert.Equal(1, await api.RemindOnAsync(measured.AddMonths(1).AddDays(1)));

        Assert.All(await api.RemindersOfAsync(z.Zone), n => Assert.Contains($"since {Named(measured)}.", n.Body));
    }

    // Two rafts: the earliest Open cycle of the zone counts; a voided measurement never does.
    [Fact]
    public async Task The_earliest_open_cycle_of_the_zone_counts_and_a_voided_measurement_does_not()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var earliest = Today.AddMonths(-3);
        await z.OpenCycleAsync(Today.AddDays(-3));
        await z.OpenCycleAsync(earliest, await api.RaftAsync(z.Zone));
        await z.AddMeasurementAsync(Today.AddDays(-2), StatusIds.WaterMeasurement.Voided);

        Assert.Equal(1, await api.RemindOnAsync(Today));

        Assert.All(await api.RemindersOfAsync(z.Zone), n => Assert.Contains($"since {Named(earliest)}.", n.Body));
    }
}
