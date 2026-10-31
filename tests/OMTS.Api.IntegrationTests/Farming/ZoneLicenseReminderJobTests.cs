using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// GFR-03 for zone licenses: the Cooperative Owner only, a mandatory type, one notice per state, only the newest license of
/// a zone, idempotent runs, re-arming on a new expiry date.
/// </summary>
public class ZoneLicenseReminderJobTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private ZoneLicenseReminderJob Job => factory.Services.GetRequiredService<ZoneLicenseReminderJob>();

    [Fact]
    public async Task Expiring_license_notifies_only_the_owners_once_and_once_more_after_expiry()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var zoneB = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var zoneC = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        // Zone Managers no longer get license reminders (the open item of PR 0.5), whatever their pairs.
        var staff = new[]
        {
            (await factory.StaffAccountAsync(zoneA)).Account.Id,
            (await factory.StaffAccountAsync(null)).Account.Id,
            (await factory.StaffAccountAsync(zoneA, StaffRole.Accountant)).Account.Id,
        };

        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zoneA.Id, EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(30) });
        // An older license of the same zone: only the newest license of a zone is reminded.
        var older = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zoneA.Id, EffectiveDate = Today.AddDays(-900), ExpiryDate = Today.AddDays(-10) });
        var notDueYet = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zoneB.Id, EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(31) });
        var replaced = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zoneC.Id, EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(5) });
        await (await owner.Client.PostAsync($"/api/zone-licenses/{replaced.Id()}/replace", new LicenseFields
        {
            EffectiveDate = Today, ExpiryDate = Today.AddYears(5),
        }.ToForm())).ReadAsync(HttpStatusCode.Created);

        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today);

        var owners = (await ActiveOwnersAsync()).Order().ToList();
        var first = await NoticesAsync(license.Id());
        Assert.Equal(owners, first.Select(n => n.RecipientAccountId).Order());
        Assert.DoesNotContain(first, n => staff.Contains(n.RecipientAccountId));
        Assert.All(first, n =>
        {
            Assert.Equal(
                $"Marine aquaculture license {license.Text("documentNumber")} ({zoneA.Code}) expires on {Date(Today.AddDays(30))}, in 30 days. Please renew it and record the new one.",
                n.Body);
            Assert.Equal(("MSG-FARM-01", "Zone license expiring soon"), (n.MessageCode, n.Title));
            Assert.Equal(("ZoneComplianceDocument", license.Text("documentNumber")), (n.RelatedRecordType, n.RelatedRecordCode));
            // A mandatory type (BR-ACCOUNT-11).
            Assert.True(n.IsMandatory);
        });
        Assert.Empty(await NoticesAsync(older.Id()));
        Assert.Empty(await NoticesAsync(notDueYet.Id()));
        Assert.Empty(await NoticesAsync(replaced.Id()));

        // Expired: exactly one more notice per Owner, however often the job runs.
        await Job.RunOnceAsync(Today.AddDays(31));
        await Job.RunOnceAsync(Today.AddDays(31));
        await Job.RunOnceAsync(Today.AddDays(32));
        var all = await NoticesAsync(license.Id());
        Assert.All(all.GroupBy(n => n.RecipientAccountId), g => Assert.Equal(2, g.Count()));
        Assert.Equal(owners.Count * 2, all.Count);
        Assert.Equal(owners.Count, all.Count(n => n.MessageCode == "MSG-FARM-02" && n.Title == "Zone license expired"
            && n.Body!.EndsWith($"expired on {Date(Today.AddDays(30))}. Please renew it and record the new one.", StringComparison.Ordinal)));
        Assert.Empty(await NoticesAsync(older.Id()));
        Assert.Empty(await NoticesAsync(replaced.Id()));
    }

    // A record entered when it is already Expired gets the Expired notice only, at the next check (GFR-03).
    [Fact]
    public async Task License_found_expired_sends_a_single_expired_notice()
    {
        var owner = await factory.OwnerAsync();
        var zone = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields
        {
            DocumentType = "SEA_AREA_ALLOCATION", ZoneId = zone.Id, EffectiveDate = Today.AddDays(-400), ExpiryDate = Today.AddDays(-5),
        });

        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today.AddDays(1));

        var notices = await NoticesAsync(license.Id());
        Assert.Equal((await ActiveOwnersAsync()).Order(), notices.Select(n => n.RecipientAccountId).Order());
        Assert.All(notices, n => Assert.Equal(
            $"Sea area allocation decision {license.Text("documentNumber")} ({zone.Code}) expired on {Date(Today.AddDays(-5))}. Please renew it and record the new one.", n.Body));
        var saved = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()));
        Assert.NotNull(saved.ExpiredReminderSentAt);
        Assert.NotNull(saved.ExpiringReminderSentAt);
    }

    [Fact]
    public async Task A_new_expiry_date_re_arms_the_reminder_and_other_edits_do_not()
    {
        var owner = await factory.OwnerAsync();
        var zone = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var fields = new LicenseFields { ZoneId = zone.Id, DocumentType = null, EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(10) };
        var license = await owner.Client.CreateLicenseAsync(fields);

        await Job.RunOnceAsync(Today);
        var recipients = (await NoticesAsync(license.Id())).Select(n => n.RecipientAccountId).ToList();
        Assert.Contains(owner.Account.Id, recipients);
        Assert.Equal(recipients.Count, recipients.Distinct().Count());

        await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with
        {
            Holder = "HTX", File = null, RowVersion = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id()),
        }).ToForm())).ReadAsync(HttpStatusCode.OK);
        await Job.RunOnceAsync(Today);
        Assert.Equal(recipients.Count, (await NoticesAsync(license.Id())).Count);

        await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with
        {
            ExpiryDate = Today.AddDays(20), File = null, RowVersion = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id()),
        }).ToForm())).ReadAsync(HttpStatusCode.OK);
        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today);
        var notices = await NoticesAsync(license.Id());
        Assert.All(notices.GroupBy(n => n.RecipientAccountId), g => Assert.Equal(2, g.Count()));
        Assert.Equal(recipients.Count * 2, notices.Count);
        // A license without a type is named "Zone license" (the type is optional since PR 0.8).
        Assert.Contains(notices, n => n.Body ==
            $"Zone license {license.Text("documentNumber")} ({zone.Code}) expires on {Date(Today.AddDays(20))}, in 20 days. Please renew it and record the new one.");
    }

    // Mandatory: pushed although the Owner's stored choice for the type is off (BR-ACCOUNT-11), with the code and parameters.
    [Fact]
    public async Task The_reminder_is_pushed_even_when_the_owner_turned_its_push_off()
    {
        var owner = await factory.OwnerAsync();
        await factory.UpdateAsync<Account>(owner.Account.Id,
            a => a.NotificationPreferences.Add(new NotificationPreference { Type = NotificationTypes.ZoneLicenseExpiry, Push = false }));
        var zone = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id, EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(7) });

        await Job.RunOnceAsync(Today);

        var pushed = Assert.Single(factory.Services.GetRequiredService<FakePushSender>().Inbox.List(owner.Account.Id.ToString()),
            p => p.MessageCode == "MSG-FARM-01" && p.MessageParams!.Value.GetProperty("document_number").GetString() == license.Text("documentNumber"));
        Assert.StartsWith("ZONE_LICENSE_EXPIRY:", pushed.Text);
        Assert.Equal([zone.Code], pushed.MessageParams!.Value.GetProperty("zone_codes").EnumerateArray().Select(z => z.GetString()));
        Assert.Equal(7, pushed.MessageParams!.Value.GetProperty("days").GetInt32());
    }

    private Task<List<Guid>> ActiveOwnersAsync() => factory.QueryAsync(db => db.Accounts
        .Where(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active).Select(a => a.Id).ToListAsync());

    private Task<List<Notification>> NoticesAsync(Guid licenseId) => factory.QueryAsync(db => db.Notifications
        .Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry && n.RelatedRecordId == licenseId).ToListAsync());

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
