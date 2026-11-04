using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>Who gets the SMS of each mandatory type, through the real use cases and the fake SMS service.</summary>
public class MandatorySmsRecipientTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static NotificationContent Withdrawn(Guid zoneId) => new(
        NotificationTypes.WaterAlertWithdrawn,
        "Water alert withdrawn",
        new UserMessage("MSG-WATER-04", "The water alert of zone Vùng A was withdrawn."),
        new RelatedRecord(RelatedRecordTypes.FarmingZone, zoneId));

    [Fact]
    public async Task A_zone_managers_alert_reaches_the_owner_and_the_managers_of_the_zone_once_each()
    {
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);
        var (_, otherManager) = await api.StaffAsync(z.Zone);
        var (_, managerElsewhere) = await api.StaffAsync(await api.AddZoneAsync(NewCode()));
        var (_, accountant) = await api.StaffAsync(z.Zone, StaffRole.Accountant);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        var toOwner = Assert.Single(api.SmsTo(z.OwnerAccount));
        // The code of a test zone is random hex, sometimes long digits that the rule masks: the name is not asserted.
        Assert.StartsWith("Water alert: zone Zone ", toOwner.Text);
        Assert.Contains("pH 9.1 is too high", toOwner.Text);
        // A Zone Manager still gets the safety alert of their own reading (decision 3 is about the Owner).
        Assert.Single(api.SmsTo(manager));
        Assert.Single(api.SmsTo(otherManager));
        Assert.Empty(api.SmsTo(managerElsewhere));
        Assert.Empty(api.SmsTo(accountant));
    }

    [Fact]
    public async Task The_owner_who_records_the_measurement_gets_no_sms_and_the_others_do()
    {
        var z = await api.ZoneAsync();
        var otherOwner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (_, manager) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m);

        Assert.Empty(api.SmsTo(z.OwnerAccount));
        Assert.Single(api.SmsTo(otherOwner));
        Assert.Single(api.SmsTo(manager));
    }

    [Fact]
    public async Task A_correction_that_still_alerts_says_so_in_the_sms()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var id = (await z.RecordedAsync(9.1m, 20m)).Id();

        var corrected = await z.Owner.CorrectAsync(id, 9.5m, 20m, recordVersion: 1);

        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        var messages = api.SmsTo(manager);
        Assert.Equal(2, messages.Count);
        Assert.StartsWith("Water alert (corrected measurement): zone", messages[0].Text);
    }

    [Fact]
    public async Task A_measurement_within_the_thresholds_sends_nothing()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(7.5m, 20m);

        Assert.Empty(api.SmsTo(manager));
    }

    // A device that did not get the answer sends the same operation again: no second notification, so no second SMS.
    [Fact]
    public async Task A_re_sent_operation_sends_no_second_sms()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var operation = Guid.NewGuid();

        var first = await z.RecordAsync(9.1m, 20m, clientOperationId: operation);
        var again = await z.RecordAsync(9.1m, 20m, clientOperationId: operation);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, again.StatusCode));
        Assert.Single(api.SmsTo(manager));
    }

    [Fact]
    public async Task A_withdrawal_reaches_the_owner_and_the_managers_of_the_zone()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);

        await api.NotifyAsync([z.OwnerAccount.Id, manager.Id], Withdrawn(z.Zone.Id));

        Assert.Equal("The water alert of zone Vung A was withdrawn.", Assert.Single(api.SmsTo(z.OwnerAccount)).Text);
        Assert.Single(api.SmsTo(manager));
    }

    [Fact]
    public async Task A_reminder_of_a_normal_or_a_list_only_type_sends_no_sms()
    {
        var z = await api.ZoneAsync();

        await api.NotifyAsync([z.OwnerAccount.Id], NotificationsV2QaKit.OverdueReminder(z.Zone));
        await api.NotifyAsync([z.OwnerAccount.Id], new NotificationContent(
            NotificationTypes.StaffAccountLockReminder, "Reminder", new UserMessage("MSG-LABOR-02", "Lock the account.")));

        Assert.Equal(1, await api.NotificationCountAsync(z.OwnerAccount.Id, NotificationTypes.StaffAccountLockReminder));
        Assert.Empty(api.SmsTo(z.OwnerAccount));
    }

    [Fact]
    public async Task A_locked_or_revoked_account_gets_no_sms()
    {
        var z = await api.ZoneAsync();
        var locked = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff, a => a.StatusId = OMTS.Domain.Constants.StatusIds.Account.Locked);
        await api.GrantAsync(locked, StaffRole.ZoneManager, z.Zone);
        var revoked = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        await api.GrantAsync(revoked, StaffRole.ZoneManager, z.Zone, g => g.StatusId = OMTS.Domain.Constants.StatusIds.AccessGrant.Revoked);

        // Passed by id on purpose: the rows and the SMS check the account themselves, whatever a module sends.
        await api.NotifyAsync([locked.Id, revoked.Id], Withdrawn(z.Zone.Id));

        Assert.Empty(api.SmsTo(locked));
        Assert.Empty(api.SmsTo(revoked));
    }

    [Fact]
    public async Task The_sms_is_visible_in_the_dev_inbox()
    {
        var z = await api.ZoneAsync();
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        var inbox = await api.CreateClient().GetFromJsonAsync<JsonElement>($"/api/dev/sms?phoneNumber={z.OwnerAccount.PhoneNumber}");
        Assert.StartsWith("Water alert", inbox[0].GetProperty("text").GetString());
    }
}
