using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// GFR-03 (PR 0.8): the licenses of a zone that is not In Use are skipped and a resumed zone is reminded again without a
/// notice sent twice; the cooperative certifications are checked by the same daily job; a catch-up at start sends nothing
/// twice.
/// </summary>
public class ExpiryReminderZoneStatusAndCertificationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private ZoneLicenseReminderJob Job => factory.Services.GetRequiredService<ZoneLicenseReminderJob>();

    // FARM-02.2 step 4 and FARM-02.3 step 4: discontinued → not reminded; resumed → the notice of the current state, once.
    [Fact]
    public async Task A_discontinued_zone_is_skipped_and_after_resume_only_the_notice_not_sent_yet_goes_out()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(10) });
        await Job.RunOnceAsync(Today);
        var owners = (await ActiveOwnersAsync()).Count;
        Assert.Equal(owners, (await NoticesAsync(license.Id())).Count(n => n.MessageCode == "MSG-FARM-01"));

        var discontinued = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        await Job.RunOnceAsync(Today.AddDays(11));
        await Job.RunOnceAsync(Today.AddDays(12));
        Assert.Equal(owners, (await NoticesAsync(license.Id())).Count);

        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { rowVersion = discontinued.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await Job.RunOnceAsync(Today.AddDays(12));
        await Job.RunOnceAsync(Today.AddDays(12));
        await Job.RunOnceAsync(Today.AddDays(13));

        var notices = await NoticesAsync(license.Id());
        Assert.Equal(owners * 2, notices.Count);
        Assert.Equal(owners, notices.Count(n => n.MessageCode == "MSG-FARM-02"));
    }

    [Fact]
    public async Task A_zone_resumed_in_the_same_state_gets_nothing_new_and_one_discontinued_before_any_notice_gets_it_at_the_next_check()
    {
        var owner = await factory.OwnerAsync();
        var told = await owner.Client.CreateZoneAsync();
        var toldLicense = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = told.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) });
        var untold = await owner.Client.CreateZoneAsync();
        var untoldLicense = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = untold.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(5) });
        // Discontinued before the check: nothing was ever sent for its license.
        var untoldStopped = await DiscontinueAsync(owner.Client, untold);

        await Job.RunOnceAsync(Today);
        var toldStopped = await DiscontinueAsync(owner.Client, told);
        Assert.Empty(await NoticesAsync(untoldLicense.Id()));

        await ResumeAsync(owner.Client, told, toldStopped);
        await ResumeAsync(owner.Client, untold, untoldStopped);
        await Job.RunOnceAsync(Today);

        var owners = (await ActiveOwnersAsync()).Count;
        Assert.Equal(owners, (await NoticesAsync(toldLicense.Id())).Count);
        var late = await NoticesAsync(untoldLicense.Id());
        Assert.Equal(owners, late.Count);
        Assert.All(late, n => Assert.Contains("in 5 days.", n.Body!, StringComparison.Ordinal));
    }

    // GFR-03 covers the cooperative certifications: the Owner only, mandatory, once per state, re-armed by a new expiry date.
    [Fact]
    public async Task Certifications_are_reminded_to_the_owner_once_per_state()
    {
        var owner = await factory.OwnerAsync();
        await factory.UpdateAsync<Account>(owner.Account.Id,
            a => a.NotificationPreferences.Add(new NotificationPreference { Type = NotificationTypes.CooperativeCertificationExpiry, Push = false }));
        var manager = (await factory.StaffAccountAsync(await factory.ZoneEntityAsync((await owner.Client.CreateZoneAsync()).Id()))).Account.Id;
        var everywhere = (await factory.StaffAccountAsync(null, StaffRole.Accountant)).Account.Id;
        var expiring = await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = Today.AddDays(15) });
        var expired = await owner.Client.CreateCertificationAsync(new CertificationFields { Number = null, ExpiryDate = Today.AddDays(-3) });
        var far = await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = Today.AddDays(31) });
        var never = await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = null });

        await Job.RunOnceAsync(Today);
        await Job.RunOnceAsync(Today);

        var owners = (await ActiveOwnersAsync()).Order().ToList();
        var first = await CertificationNoticesAsync(expiring.Id());
        Assert.Equal(owners, first.Select(n => n.RecipientAccountId).Order());
        Assert.DoesNotContain(first, n => n.RecipientAccountId == manager || n.RecipientAccountId == everywhere);
        Assert.All(first, n =>
        {
            Assert.Equal(("MSG-FARM-01", "Certification expiring soon", true), (n.MessageCode, n.Title, n.IsMandatory));
            Assert.Equal(
                $"{expiring.Text("name")} ({expiring.Text("number")}) expires on {Date(Today.AddDays(15))}, in 15 days. Please renew it and record the new one.",
                n.Body);
            Assert.Equal(("CooperativeCertification", expiring.Text("number")), (n.RelatedRecordType, n.RelatedRecordCode));
        });
        var onlyExpired = Assert.Single(await CertificationNoticesAsync(expired.Id()), n => n.RecipientAccountId == owner.Account.Id);
        Assert.Equal(("MSG-FARM-02", $"{expired.Text("name")} expired on {Date(Today.AddDays(-3))}. Please renew it and record the new one."), (onlyExpired.MessageCode, onlyExpired.Body));
        Assert.Empty(await CertificationNoticesAsync(far.Id()));
        Assert.Empty(await CertificationNoticesAsync(never.Id()));

        // Mandatory: pushed although the stored choice is off, with atomic parameters.
        var pushed = Assert.Single(factory.Services.GetRequiredService<FakePushSender>().Inbox.List(owner.Account.Id.ToString()),
            p => p.Text.StartsWith("COOPERATIVE_CERTIFICATION_EXPIRY:", StringComparison.Ordinal) && p.MessageCode == "MSG-FARM-01"
                && p.MessageParams!.Value.GetProperty("certification_number").GetString() == expiring.Text("number"));
        Assert.Equal(
            (expiring.Text("name"), Today.AddDays(15).ToString("yyyy-MM-dd"), 15),
            (pushed.MessageParams!.Value.GetProperty("certification_name").GetString(), pushed.MessageParams!.Value.GetProperty("expiry_date").GetString(),
                pushed.MessageParams!.Value.GetProperty("days").GetInt32()));

        // Expired: one more notice; a new expiry date re-arms the reminders.
        await Job.RunOnceAsync(Today.AddDays(16));
        await Job.RunOnceAsync(Today.AddDays(17));
        Assert.Equal(owners.Count, (await CertificationNoticesAsync(expiring.Id())).Count(n => n.MessageCode == "MSG-FARM-02"));
        var version = await factory.IssueCertificationVersionAsync(expiring.Id());
        await (await owner.Client.PutAsync($"{CooperativeCertificationRequests.Certifications}/{expiring.Id()}", new CertificationFields
        {
            Name = expiring.Text("name"), Number = expiring.Text("number"), ExpiryDate = Today.AddDays(25), File = null, RowVersion = version,
        }.ToForm())).ReadAsync(HttpStatusCode.OK);
        await Job.RunOnceAsync(Today);
        Assert.Equal(owners.Count * 3, (await CertificationNoticesAsync(expiring.Id())).Count);
    }

    // The notification opens the certification (Owner only); once it is removed, MSG-ACCOUNT-22.
    [Fact]
    public async Task A_certification_reminder_opens_for_the_owner_and_answers_msg_account_22_once_removed()
    {
        var owner = await factory.OwnerAsync();
        var certification = await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = Today.AddDays(3) });
        await Job.RunOnceAsync(Today);
        var id = (await owner.Client.NotificationPageAsync()).Items()
            .Single(i => i.GetProperty("relatedRecordId").GetGuid() == certification.Id()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await owner.Client.OpenNotificationAsync(id)).StatusCode);
        var version = await factory.IssueCertificationVersionAsync(certification.Id());
        await (await owner.Client.DeleteAsync($"{CooperativeCertificationRequests.Certifications}/{certification.Id()}?rowVersion={Uri.EscapeDataString(version)}"))
            .ReadAsync(HttpStatusCode.OK);

        var removed = await owner.Client.OpenNotificationAsync(id);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        var problem = await removed.ProblemAsync();
        Assert.Equal(("MSG-ACCOUNT-22", $"Certification {certification.Text("number")} no longer exists."), (problem.GetProperty("code").GetString(), problem.GetProperty("detail").GetString()));
    }

    // A host restarted after 07:00 catches up at each start: the second start sends nothing new.
    [Fact]
    public async Task Catch_up_at_each_start_sends_each_notice_once()
    {
        var owner = await factory.OwnerAsync();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields
        {
            ZoneId = (await owner.Client.CreateZoneAsync()).Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(2),
        });
        var certification = await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = Today.AddDays(-1) });

        for (var start = 0; start < 2; start++)
        {
            var clock = new ManualClock(new DateTimeOffset(Today.ToDateTime(new TimeOnly(9, 0)), BusinessClock.UtcOffset));
            var hosted = new ZoneLicenseReminderHostedService(
                Job, Options.Create(new ZoneLicenseReminderOptions()), clock, NullLogger<ZoneLicenseReminderHostedService>.Instance);
            await hosted.StartAsync(CancellationToken.None);
            try
            {
                // The catch-up run is over once the job waits for tomorrow's 07:00.
                await clock.WaitForTimerAsync();
                Assert.Equal(new DateTimeOffset(Today.AddDays(1).ToDateTime(new TimeOnly(7, 0)), BusinessClock.UtcOffset), clock.NextDueAt);
            }
            finally
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }

        var owners = (await ActiveOwnersAsync()).Count;
        Assert.Equal(owners, (await NoticesAsync(license.Id())).Count);
        Assert.Equal(owners, (await CertificationNoticesAsync(certification.Id())).Count);
    }

    private static async Task<System.Text.Json.JsonElement> DiscontinueAsync(HttpClient owner, System.Text.Json.JsonElement zone) =>
        await (await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

    private static async Task ResumeAsync(HttpClient owner, System.Text.Json.JsonElement zone, System.Text.Json.JsonElement discontinued) =>
        await (await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { rowVersion = discontinued.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

    private Task<List<Guid>> ActiveOwnersAsync() => factory.QueryAsync(db => db.Accounts
        .Where(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active).Select(a => a.Id).ToListAsync());

    private Task<List<Notification>> NoticesAsync(Guid licenseId) => factory.QueryAsync(db => db.Notifications
        .Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry && n.RelatedRecordId == licenseId).ToListAsync());

    private Task<List<Notification>> CertificationNoticesAsync(Guid certificationId) => factory.QueryAsync(db => db.Notifications
        .Where(n => n.Type == NotificationTypes.CooperativeCertificationExpiry && n.RelatedRecordId == certificationId).ToListAsync());

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
