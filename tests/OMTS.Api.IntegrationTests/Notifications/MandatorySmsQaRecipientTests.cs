using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using FarmingKit = OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using LicenseFields = OMTS.Api.IntegrationTests.Farming.LicenseFields;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>QA of who gets each mandatory SMS (decisions 2 and 3 of OMTS-216; BR-WATER-05, BR-ACCOUNT-11, GFR-03), through the real use cases.</summary>
public class MandatorySmsQaRecipientTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private async Task<Account> ManagerAsync(params Domain.Entities.Farming.FarmingZone?[] zones)
    {
        var account = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        foreach (var zone in zones)
        {
            await api.GrantAsync(account, StaffRole.ZoneManager, zone);
        }

        return account;
    }

    private static async Task<Guid> NewZoneIdAsync(HttpClient owner) => (await FarmingKit.CreateZoneAsync(owner)).GetProperty("id").GetGuid();

    private static NotificationContent Withdrawn() => new(
        NotificationTypes.WaterAlertWithdrawn, "Title", new UserMessage("MSG-WATER-04", "The water alert was withdrawn."));

    [Fact]
    public async Task An_all_zones_manager_and_every_other_owner_get_one_each()
    {
        var z = await api.ZoneAsync();
        var allZones = await ManagerAsync([null]);
        var secondOwner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Single(api.SmsTo(allZones));
        Assert.Single(api.SmsTo(secondOwner));
        Assert.Single(api.SmsTo(z.OwnerAccount));
    }

    [Fact]
    public async Task An_accountant_here_who_manages_another_zone_gets_no_notification_and_no_sms()
    {
        var z = await api.ZoneAsync();
        var elsewhere = await api.AddZoneAsync(NewCode());
        var twoRoles = await ManagerAsync(elsewhere);
        await api.GrantAsync(twoRoles, StaffRole.Accountant, z.Zone);
        var allZonesAccountant = await ManagerAsync(elsewhere);
        await api.GrantAsync(allZonesAccountant, StaffRole.Accountant, null);

        await z.RecordedAsync(9.1m, 20m);

        Assert.Empty(api.SmsTo(twoRoles));
        Assert.Empty(api.SmsTo(allZonesAccountant));
        Assert.Equal(0, await api.NotificationCountAsync(twoRoles.Id, NotificationTypes.WaterAlert));
    }

    [Fact]
    public async Task A_recipient_holding_two_pairs_gets_one_sms()
    {
        var z = await api.ZoneAsync();
        var managerAndAccountant = await ManagerAsync(z.Zone);
        await api.GrantAsync(managerAndAccountant, StaffRole.Accountant, z.Zone);
        var hereAndEverywhere = await ManagerAsync(z.Zone, null);

        await z.RecordedAsync(9.1m, 20m);

        Assert.Single(api.SmsTo(managerAndAccountant));
        Assert.Single(api.SmsTo(hereAndEverywhere));
    }

    [Fact]
    public async Task An_owner_holding_a_pair_of_a_zone_gets_one_sms()
    {
        var z = await api.ZoneAsync();
        var ownerWithPair = await api.AddAccountAsync(NewCode(), NewPassword());
        await api.GrantAsync(ownerWithPair, StaffRole.ZoneManager, z.Zone);
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Single(api.SmsTo(ownerWithPair));
    }

    [Fact]
    public async Task The_owner_who_records_or_corrects_is_left_out_and_the_others_are_told()
    {
        var z = await api.ZoneAsync();
        var secondOwner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (_, manager) = await api.StaffAsync(z.Zone);
        var recorded = await z.RecordedAsync(9.1m, 20m);

        Assert.Empty(api.SmsTo(z.OwnerAccount));
        Assert.Single(api.SmsTo(secondOwner));
        Assert.Single(api.SmsTo(manager));

        var corrected = await z.Owner.CorrectAsync(recorded.Id(), 6.0m, 20m);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        Assert.Empty(api.SmsTo(z.OwnerAccount));
        Assert.Equal(2, api.SmsTo(secondOwner).Count);
    }

    // BR-WATER-05 tells the same people "only when the result changes", so a new reading on the same side is no second SMS.
    [Fact]
    public async Task A_correction_that_stays_on_the_same_side_sends_no_second_sms()
    {
        var z = await api.ZoneAsync();
        var manager = await ManagerAsync(z.Zone);
        var id = (await z.RecordedAsync(9.1m, 20m)).Id();

        var corrected = await z.Owner.CorrectAsync(id, 9.5m, 20m);

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Single(api.SmsTo(manager));
    }

    // ACCOUNT-07, BR-ACCOUNT-11: a mandatory type cannot be turned off, so a stray stored choice must not turn the SMS off either.
    [Fact]
    public async Task A_stored_choice_to_turn_the_push_off_does_not_turn_the_sms_off()
    {
        var z = await api.ZoneAsync();
        var manager = await ManagerAsync(z.Zone);
        await api.UpdateAsync<Account>(manager.Id, a => a.NotificationPreferences.Add(new NotificationPreference { Type = NotificationTypes.WaterAlert, Push = false }));

        await z.RecordedAsync(9.1m, 20m);

        Assert.Single(api.SmsTo(manager));
    }

    [Fact]
    public async Task Locked_unverified_and_empty_numbers_get_none_and_the_row_follows_the_status_only()
    {
        var z = await api.ZoneAsync();
        var lockedOwner = await api.AddAccountAsync(NewCode(), NewPassword(), configure: a => a.StatusId = StatusIds.Account.Locked);
        var lockedManager = await ManagerAsync(z.Zone);
        await api.UpdateAsync<Account>(lockedManager.Id, a => a.StatusId = StatusIds.Account.Locked);
        var unverifiedManager = await ManagerAsync(z.Zone);
        await api.UpdateAsync<Account>(unverifiedManager.Id, a => a.PhoneVerifiedAt = null);
        var blankManager = await ManagerAsync(z.Zone);
        await api.UpdateAsync<Account>(blankManager.Id, a => a.PhoneNumber = "   ");

        await z.RecordedAsync(9.1m, 20m);

        Assert.Empty(api.SmsTo(lockedOwner));
        Assert.Empty(api.SmsTo(lockedManager));
        Assert.Empty(api.SmsTo(unverifiedManager));
        Assert.Empty(api.SmsTo("   "));
        Assert.Equal(0, await api.NotificationCountAsync(lockedOwner.Id, NotificationTypes.WaterAlert));
        Assert.Equal(1, await api.NotificationCountAsync(unverifiedManager.Id, NotificationTypes.WaterAlert));
        Assert.Equal(1, await api.NotificationCountAsync(blankManager.Id, NotificationTypes.WaterAlert));
    }

    [Fact]
    public async Task A_revoked_pair_here_with_a_pair_elsewhere_gets_none()
    {
        var z = await api.ZoneAsync();
        var manager = await ManagerAsync(await api.AddZoneAsync(NewCode()));
        await api.GrantAsync(manager, StaffRole.ZoneManager, z.Zone, g => g.StatusId = StatusIds.AccessGrant.Revoked);

        await z.RecordedAsync(9.1m, 20m);

        Assert.Empty(api.SmsTo(manager));
    }

    // Passed by id, as a module could: the rule of the type decides, not the list of the module.
    [Fact]
    public async Task A_trader_an_accountant_or_staff_without_a_pair_passed_as_recipients_get_no_sms()
    {
        var trader = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Trader);
        var noPair = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        var accountant = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        await api.GrantAsync(accountant, StaffRole.Accountant, null);

        await api.NotifyAsync([trader.Id, noPair.Id, accountant.Id], Withdrawn());

        Assert.Empty(api.SmsTo(trader));
        Assert.Empty(api.SmsTo(noPair));
        Assert.Empty(api.SmsTo(accountant));
        Assert.Equal(1, await api.NotificationCountAsync(trader.Id, NotificationTypes.WaterAlertWithdrawn));
    }

    [Fact]
    public async Task A_repeated_recipient_id_sends_one()
    {
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());

        await api.NotifyAsync([owner.Id, owner.Id, owner.Id], Withdrawn());

        Assert.Single(api.SmsTo(owner));
    }

    [Fact]
    public async Task A_license_reminds_each_owner_once_and_no_manager_also_when_it_expires()
    {
        var owner = await FarmingKit.OwnerAsync(api);
        var secondOwner = await api.AddAccountAsync(NewCode(), NewPassword());
        var zoneA = await NewZoneIdAsync(owner.Client);
        var manager = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        await api.GrantAsync(manager, StaffRole.ZoneManager, zoneA);
        await FarmingKit.CreateLicenseAsync(owner.Client, new LicenseFields { ZoneId = zoneA, EffectiveDate = FarmingKit.Today.AddDays(-300), ExpiryDate = FarmingKit.Today.AddDays(5) });
        var job = api.Services.GetRequiredService<ZoneLicenseReminderJob>();

        Assert.Equal(1, await job.RunOnceAsync(FarmingKit.Today));
        Assert.Single(api.SmsTo(owner.Account));
        Assert.Single(api.SmsTo(secondOwner));
        Assert.Empty(api.SmsTo(manager));

        // The second notice of GFR-03, once it has expired: a new notification, so a new SMS.
        Assert.Equal(1, await job.RunOnceAsync(FarmingKit.Today.AddDays(6)));
        Assert.Equal(2, api.SmsTo(owner.Account).Count);
        Assert.Contains(api.SmsTo(owner.Account), m => m.Text.Contains("expired on"));
        Assert.Empty(api.SmsTo(manager));
    }
}
