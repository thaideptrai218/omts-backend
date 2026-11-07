using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// BR-WATER-05 and the PM decision of 03/11 on the notifications of WATER-02: they go to the Cooperative Owner and the
/// Zone Managers of the zone, except the Owner who did the action; a Zone Manager who acted still gets them.
/// </summary>
public class WaterCorrectionRecipientsTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task The_owner_who_acted_gets_nothing_the_other_owner_and_the_zone_managers_do()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var allZones = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(allZones, StaffRole.ZoneManager, zone: null);
        var otherOwner = await api.AddAccountAsync(NewCode(), Password);
        var (_, accountant) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var (_, otherZoneManager) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        var toWithdraw = await z.RecordedAsync(9m, 20m);
        var toAlert = await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await z.Owner.CorrectAsync(toAlert.Id(), 9m, 20m, measuredAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await z.Owner.VoidAsync(toAlert.Id());
        await z.Owner.CorrectAsync(toWithdraw.Id(), 7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddMinutes(-2));

        foreach (var notices in new[] { await api.AlertsOfAsync(toAlert.Id()), await api.WithdrawalsOfAsync(toAlert.Id()), await api.WithdrawalsOfAsync(toWithdraw.Id()) })
        {
            Assert.DoesNotContain(notices, n => n.RecipientAccountId == z.OwnerAccount.Id);
            Assert.Single(notices, n => n.RecipientAccountId == otherOwner.Id);
            Assert.Single(notices, n => n.RecipientAccountId == manager.Id);
            Assert.Single(notices, n => n.RecipientAccountId == allZones.Id);
            Assert.DoesNotContain(notices, n => n.RecipientAccountId == accountant.Id);
            Assert.DoesNotContain(notices, n => n.RecipientAccountId == otherZoneManager.Id);
        }
    }

    [Fact]
    public async Task A_zone_manager_who_acted_gets_it_with_the_owner_and_the_colleague()
    {
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);
        var (_, colleague) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(9m, 20m, client: managerClient);

        await managerClient.VoidAsync(saved.Id(), reason: "Đo nhầm");

        var withdrawn = await api.WithdrawalsOfAsync(saved.Id());
        Assert.Single(withdrawn, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(withdrawn, n => n.RecipientAccountId == manager.Id);
        Assert.Single(withdrawn, n => n.RecipientAccountId == colleague.Id);
        Assert.All(withdrawn, n => Assert.Contains($"{manager.DisplayName} voided the measurement. Reason: Đo nhầm.", n.Body));
    }

    // Default for a PM question: MSG-WATER-04 is mandatory, as the alert is. Turning its push off does not stop it.
    [Fact]
    public async Task The_withdrawal_is_mandatory_and_pushed_once_with_its_atomic_parameters()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        await api.UseDbAsync(db => db.Accounts.Single(a => a.Id == manager.Id).NotificationPreferences.Add(
            new NotificationPreference { Type = NotificationTypes.WaterAlertWithdrawn, Push = false }));
        var saved = await z.RecordedAsync(9m, 20m);

        await z.Owner.VoidAsync(saved.Id(), reason: "Đo nhầm");

        var withdrawn = Assert.Single(await api.WithdrawalsOfAsync(saved.Id()), n => n.RecipientAccountId == manager.Id);
        Assert.True(withdrawn.IsMandatory);
        Assert.Contains("\"corrected_or_voided\":\"voided\"", withdrawn.MessageParams);
        Assert.Contains("\"reason\":\"Đo nhầm\"", withdrawn.MessageParams);
        Assert.Contains($"\"user_name\":\"{z.OwnerAccount.DisplayName}\"", withdrawn.MessageParams);
        var pushed = api.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Id.ToString());
        Assert.Single(pushed, m => m.Text.Contains("was withdrawn"));
    }
}
