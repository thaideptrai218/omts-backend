using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-03 reminder job (formerly JOB-06): recipients, one notice per stage, idempotent runs, re-arming on a new expiry date.</summary>
public class ZoneLicenseReminderJobTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private ZoneLicenseReminderJob Job => factory.Services.GetRequiredService<ZoneLicenseReminderJob>();

    // Done when: one MSG-FARM-01 per recipient after two runs on the same day, one MSG-FARM-02 once expired, none for a replaced license.
    [Fact]
    public async Task Expiring_license_notifies_each_recipient_once_and_once_more_after_expiry()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var zoneB = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var manager = (await factory.StaffAccountAsync(zoneA)).Account;
        var everywhere = (await factory.StaffAccountAsync(null)).Account;
        // The Accountant role holds no CULTIVATION_CYCLES right (§I.4.4 CYCLE-01…06 No).
        var accountant = (await factory.StaffAccountAsync(zoneA, StaffRole.Accountant)).Account;
        var otherZone = (await factory.StaffAccountAsync(zoneB)).Account;
        var revoked = (await factory.StaffAccountAsync(zoneA, configureGrant: g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; })).Account;
        var locked = (await factory.StaffAccountAsync(zoneA)).Account;
        await factory.UpdateAsync<OMTS.Domain.Entities.Identity.Account>(locked.Id, a => a.StatusId = StatusIds.Account.Locked);

        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneA.Id], EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(30) });
        var notDueYet = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneA.Id], EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(31) });
        var noExpiry = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneA.Id], DocumentType = "FOOD_SAFETY_COMMITMENT", EffectiveDate = Today.AddDays(-300) });
        var replaced = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneA.Id], EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(5) });
        await (await owner.Client.PostAsync($"/api/zone-licenses/{replaced.Id()}/replace", new LicenseFields
        {
            EffectiveDate = Today, ExpiryDate = Today.AddYears(5),
        }.ToForm())).ReadAsync(HttpStatusCode.Created);

        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today);

        var expected = (await ActiveOwnersAsync()).Append(manager.Id).Append(everywhere.Id).Order().ToList();
        var first = await NoticesAsync(license.Id());
        Assert.Equal(expected, first.Select(n => n.RecipientAccountId).Order());
        Assert.DoesNotContain(first, n => new[] { accountant.Id, otherZone.Id, revoked.Id, locked.Id }.Contains(n.RecipientAccountId));
        var zoneCode = zoneA.Code;
        Assert.All(first, n =>
        {
            Assert.Equal(
                $"Marine aquaculture license {license.Text("documentNumber")} ({zoneCode}) expires on {Date(Today.AddDays(30))}, in 30 days. Please renew it and record the new one.",
                n.Body);
            Assert.Equal("MSG-FARM-01", n.MessageCode);
            Assert.Equal(("ZoneComplianceDocument", license.Text("documentNumber")), (n.RelatedRecordType, n.RelatedRecordCode));
            // A mandatory type (BR-ACCOUNT-11).
            Assert.True(n.IsMandatory);
        });
        Assert.Empty(await NoticesAsync(notDueYet.Id()));
        Assert.Empty(await NoticesAsync(noExpiry.Id()));
        Assert.Empty(await NoticesAsync(replaced.Id()));

        // Expired: exactly one more notice per recipient, however often the job runs.
        await Job.RunOnceAsync(Today.AddDays(31));
        await Job.RunOnceAsync(Today.AddDays(31));
        await Job.RunOnceAsync(Today.AddDays(32));
        var all = await NoticesAsync(license.Id());
        Assert.All(all.GroupBy(n => n.RecipientAccountId), g => Assert.Equal(2, g.Count()));
        Assert.Equal(expected.Count * 2, all.Count);
        Assert.Equal(expected.Count, all.Count(n => n.MessageCode == "MSG-FARM-02"
            && n.Body!.EndsWith($"expired on {Date(Today.AddDays(30))}. Please renew it and record the new one.", StringComparison.Ordinal)));
        Assert.Empty(await NoticesAsync(replaced.Id()));
    }

    [Fact]
    public async Task License_found_expired_sends_a_single_notice_and_names_every_covered_zone()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var zoneB = await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id());
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields
        {
            DocumentType = "SEA_AREA_ALLOCATION", ZoneIds = [zoneB.Id, zoneA.Id], EffectiveDate = Today.AddDays(-400), ExpiryDate = Today.AddDays(-5),
        });

        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today.AddDays(1));

        // Staff of other tests holding All zones grants may be recipients too: every owner gets one notice, nobody two.
        var notices = await NoticesAsync(license.Id());
        Assert.Subset(notices.Select(n => n.RecipientAccountId).ToHashSet(), (await ActiveOwnersAsync()).ToHashSet());
        Assert.All(notices.GroupBy(n => n.RecipientAccountId), g => Assert.Single(g));
        var zones = string.Join(", ", new[] { zoneA.Code, zoneB.Code }.Order(StringComparer.Ordinal));
        Assert.All(notices, n => Assert.Equal(
            $"Sea area allocation decision {license.Text("documentNumber")} ({zones}) expired on {Date(Today.AddDays(-5))}. Please renew it and record the new one.", n.Body));
        var saved = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()));
        Assert.NotNull(saved.ExpiredReminderSentAt);
        Assert.NotNull(saved.ExpiringReminderSentAt);
    }

    [Fact]
    public async Task A_new_expiry_date_re_arms_the_reminder_and_other_edits_do_not()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var fields = new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(10) };
        var license = await owner.Client.CreateLicenseAsync(fields);

        await Job.RunOnceAsync(Today);
        var recipients = (await NoticesAsync(license.Id())).Select(n => n.RecipientAccountId).ToList();
        Assert.Contains(owner.Account.Id, recipients);
        Assert.Equal(recipients.Count, recipients.Distinct().Count());

        var holder = await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { Holder = "HTX", File = null, RowVersion = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id()) }).ToForm()))
            .ReadAsync(HttpStatusCode.OK);
        await Job.RunOnceAsync(Today);
        Assert.Equal(recipients.Count, (await NoticesAsync(license.Id())).Count);

        await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ExpiryDate = Today.AddDays(20), File = null, RowVersion = holder.Text("rowVersion") }).ToForm()))
            .ReadAsync(HttpStatusCode.OK);
        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today);
        var notices = await NoticesAsync(license.Id());
        Assert.All(notices.GroupBy(n => n.RecipientAccountId), g => Assert.Equal(2, g.Count()));
        Assert.Equal(recipients.Count * 2, notices.Count);
        Assert.Contains(notices, n => n.Body!.Contains($"expires on {Date(Today.AddDays(20))}, in 20 days.", StringComparison.Ordinal));
    }

    private Task<List<Guid>> ActiveOwnersAsync() => factory.QueryAsync(db => db.Accounts
        .Where(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active).Select(a => a.Id).ToListAsync());

    private Task<List<Notification>> NoticesAsync(Guid licenseId) => factory.QueryAsync(db => db.Notifications
        .Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry && n.RelatedRecordId == licenseId).ToListAsync());

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
