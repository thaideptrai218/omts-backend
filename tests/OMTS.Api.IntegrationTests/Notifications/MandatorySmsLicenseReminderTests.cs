using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>The SMS of the zone license reminder (GFR-03): a job, so no actor, and the same notice is found again by the next run.</summary>
public class MandatorySmsLicenseReminderTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_license_reminder_goes_to_the_owner_once_though_a_second_run_finds_it_again()
    {
        var owner = await api.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var manager = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        await api.GrantAsync(manager, StaffRole.ZoneManager, zoneId);
        await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zoneId, EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(10) });
        var job = api.Services.GetRequiredService<ZoneLicenseReminderJob>();

        Assert.Equal(1, await job.RunOnceAsync(Today));
        Assert.Equal(0, await job.RunOnceAsync(Today));

        Assert.Contains("in 10 days", Assert.Single(api.SmsTo(owner.Account)).Text);
        // The expiry reminder of a license is for the Owner only: the notification and the SMS (GFR-03).
        Assert.Equal(0, await api.NotificationCountAsync(manager.Id, NotificationTypes.ZoneLicenseExpiry));
        Assert.Empty(api.SmsTo(manager));
    }
}
