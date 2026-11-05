using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of OMTS-217 PR 0.8, GFR-03: the exact day boundaries of licenses and certifications with their parameters, a
/// replacement reminded on its own, a license that is not effective yet, locked Owners, the mandatory type.
/// </summary>
public class ExpiryReminderQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private ZoneLicenseReminderJob Job => factory.Services.GetRequiredService<ZoneLicenseReminderJob>();

    // BR-FARM-06: Expiring soon at 30 days or fewer, the expiry day included; Expired only the day after.
    [Fact]
    public async Task License_notices_start_at_30_days_not_31_and_expired_starts_the_day_after_day_0()
    {
        var today = Today;
        var owner = await factory.OwnerAsync();
        var at30 = await LicenseAsync(owner, today, 30);
        var at31 = await LicenseAsync(owner, today, 31);
        var at0 = await LicenseAsync(owner, today, 0);
        var pastOne = await LicenseAsync(owner, today, -1);

        await Job.RunOnceAsync(today);

        await AssertOneNoticeAsync(owner, at30, "MSG-FARM-01", 30);
        Assert.Empty(await NoticesAsync(owner, at31));
        await AssertOneNoticeAsync(owner, at0, "MSG-FARM-01", 0);
        await AssertOneNoticeAsync(owner, pastOne, "MSG-FARM-02", null);

        // Next day: the 31-day license enters the window at 30 days and day 0 becomes Expired; the others get nothing new.
        await Job.RunOnceAsync(today.AddDays(1));

        var entered = Assert.Single(await NoticesAsync(owner, at31));
        Assert.Equal(30, Params(entered).GetProperty("days").GetInt32());
        var dayZero = await NoticesAsync(owner, at0);
        Assert.Equal(["MSG-FARM-01", "MSG-FARM-02"], dayZero.Select(n => n.MessageCode).Order());
        Assert.False(Params(dayZero.Single(n => n.MessageCode == "MSG-FARM-02")).TryGetProperty("days", out _));
        Assert.Single(await NoticesAsync(owner, at30));
        Assert.Single(await NoticesAsync(owner, pastOne));
    }

    [Fact]
    public async Task Certification_notices_use_the_same_boundaries_and_the_shape_of_their_type()
    {
        var today = Today;
        var owner = await factory.OwnerAsync();
        var at30 = (await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = today.AddDays(30) })).Id();
        var at31 = (await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = today.AddDays(31) })).Id();
        var at0 = (await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = today })).Id();
        var pastOne = (await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = today.AddDays(-1) })).Id();

        await Job.RunOnceAsync(today);

        var first = Assert.Single(await NoticesAsync(owner, at30));
        Assert.Equal(("MSG-FARM-01", NotificationTypes.CooperativeCertificationExpiry), (first.MessageCode, first.Type));
        Assert.Equal(
            ["certification_name", "certification_number", "days", "expiry_date"],
            Params(first).EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(30, Params(first).GetProperty("days").GetInt32());
        Assert.Empty(await NoticesAsync(owner, at31));
        Assert.Equal(0, Params(Assert.Single(await NoticesAsync(owner, at0))).GetProperty("days").GetInt32());
        var expired = Assert.Single(await NoticesAsync(owner, pastOne));
        Assert.Equal("MSG-FARM-02", expired.MessageCode);
        Assert.False(Params(expired).TryGetProperty("days", out _));

        await Job.RunOnceAsync(today.AddDays(1));

        Assert.Single(await NoticesAsync(owner, at31));
        Assert.Equal(["MSG-FARM-01", "MSG-FARM-02"], (await NoticesAsync(owner, at0)).Select(n => n.MessageCode).Order());
    }

    // An edit that keeps the expiry date is not a new date: nothing is sent again.
    [Fact]
    public async Task Editing_a_certification_without_a_new_expiry_date_sends_nothing_again()
    {
        var today = Today;
        var owner = await factory.OwnerAsync();
        var certification = await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = today.AddDays(10) });
        await Job.RunOnceAsync(today);

        var version = await factory.IssueCertificationVersionAsync(certification.Id());
        await (await owner.Client.PutAsync($"{CooperativeCertificationRequests.Certifications}/{certification.Id()}", new CertificationFields
        {
            Name = "Renamed", Number = certification.Text("number"), ExpiryDate = today.AddDays(10), File = null, RowVersion = version,
        }.ToForm())).ReadAsync(HttpStatusCode.OK);
        await Job.RunOnceAsync(today);

        Assert.Single(await NoticesAsync(owner, certification.Id()));
    }

    // FARM-03.2: the replaced license is no longer reminded; its replacement has its own two notices.
    [Fact]
    public async Task A_replacement_gets_its_own_notices_and_the_replaced_license_gets_no_more()
    {
        var today = Today;
        var owner = await factory.OwnerAsync();
        var old = await LicenseAsync(owner, today, 10);
        await Job.RunOnceAsync(today);
        var replacement = (await (await owner.Client.PostAsync($"/api/zone-licenses/{old}/replace", new LicenseFields
        {
            EffectiveDate = today, ExpiryDate = today.AddDays(20),
        }.ToForm())).ReadAsync(HttpStatusCode.Created)).Id();

        await Job.RunOnceAsync(today);
        await Job.RunOnceAsync(today.AddDays(11));
        await Job.RunOnceAsync(today.AddDays(21));

        Assert.Single(await NoticesAsync(owner, old));
        Assert.Equal(["MSG-FARM-01", "MSG-FARM-02"], (await NoticesAsync(owner, replacement)).Select(n => n.MessageCode).Order());
    }

    // A license starting later does not take the notices of the current one: each is told on its own state, the later one
    // only once it is effective.
    [Fact]
    public async Task A_license_not_yet_effective_is_told_from_its_effective_date_and_the_current_one_on_its_own()
    {
        var today = Today;
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var current = (await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id(), EffectiveDate = today.AddYears(-1), ExpiryDate = today.AddDays(10) })).Id();
        var later = (await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id(), EffectiveDate = today.AddDays(5), ExpiryDate = today.AddDays(25) })).Id();

        await Job.RunOnceAsync(today);
        Assert.Equal(10, Params(Assert.Single(await NoticesAsync(owner, current))).GetProperty("days").GetInt32());
        Assert.Empty(await NoticesAsync(owner, later));

        await Job.RunOnceAsync(today.AddDays(5));

        Assert.Single(await NoticesAsync(owner, current));
        Assert.Equal(20, Params(Assert.Single(await NoticesAsync(owner, later))).GetProperty("days").GetInt32());
    }

    // A locked Owner is left out; the other Owner still gets it, once.
    [Fact]
    public async Task A_locked_owner_is_left_out_while_an_active_owner_is_told()
    {
        using var api = new AuthApiFactory();
        var today = Today;
        var active = await api.OwnerAsync();
        var locked = await api.OwnerAsync();
        var zone = await active.Client.CreateZoneAsync();
        var license = await active.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id(), EffectiveDate = today.AddYears(-1), ExpiryDate = today.AddDays(9) });
        await api.UpdateAsync<Account>(locked.Account.Id, a => a.StatusId = StatusIds.Account.Locked);

        var job = api.Services.GetRequiredService<ZoneLicenseReminderJob>();
        Assert.Equal(1, await job.RunOnceAsync(today));
        Assert.Equal(0, await job.RunOnceAsync(today));

        var notices = await api.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == license.Id()).ToListAsync());
        Assert.Equal(active.Account.Id, Assert.Single(notices).RecipientAccountId);
    }

    // GFR-03 is once per state, so a notice nobody received must stay due: with no active Owner nothing may be consumed.
    [Fact]
    public async Task With_no_active_owner_the_notice_stays_due_until_an_owner_can_receive_it()
    {
        using var api = new AuthApiFactory();
        var today = Today;
        var owner = await api.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id(), EffectiveDate = today.AddYears(-1), ExpiryDate = today.AddDays(9) });
        var certification = await owner.Client.CreateCertificationAsync(new CertificationFields { ExpiryDate = today.AddDays(9) });
        await api.UpdateAsync<Account>(owner.Account.Id, a => a.StatusId = StatusIds.Account.Locked);
        var job = api.Services.GetRequiredService<ZoneLicenseReminderJob>();

        Assert.Equal(0, await job.RunOnceAsync(today));
        Assert.Empty(await api.QueryAsync(db => db.Notifications.ToListAsync()));

        await api.UpdateAsync<Account>(owner.Account.Id, a => a.StatusId = StatusIds.Account.Active);
        await job.RunOnceAsync(today.AddDays(1));

        var told = await api.QueryAsync(db => db.Notifications.Select(n => n.RelatedRecordId).ToListAsync());
        Assert.Contains(license.Id(), told.Cast<Guid>());
        Assert.Contains(certification.Id(), told.Cast<Guid>());
    }

    // The record code of a nameless-number certification is cut at 100 characters: a cut through an emoji must not break the list.
    [Fact]
    public async Task A_certification_name_cut_inside_an_emoji_keeps_the_notification_list_readable()
    {
        using var api = new AuthApiFactory();
        var today = Today;
        var owner = await api.OwnerAsync();
        var name = new string('a', 99) + "\U0001F41A" + new string('b', 50);
        var certification = await owner.Client.CreateCertificationAsync(new CertificationFields { Name = name, Number = null, ExpiryDate = today.AddDays(9) });

        Assert.Equal(1, await api.Services.GetRequiredService<ZoneLicenseReminderJob>().RunOnceAsync(today));

        var item = Assert.Single((await owner.Client.NotificationPageAsync()).Items());
        Assert.Equal(certification.Id(), item.GetProperty("relatedRecordId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.OpenNotificationAsync(item.GetProperty("id").GetGuid())).StatusCode);
    }

    // BR-ACCOUNT-11: the new type is mandatory for the Owner and is not offered to anyone else.
    [Fact]
    public async Task The_certification_type_cannot_be_turned_off_and_is_offered_to_the_owner_only()
    {
        var owner = await factory.OwnerAsync();
        var offered = await owner.Client.OfferedTypesAsync();
        Assert.Equal((true, true), offered[NotificationTypes.CooperativeCertificationExpiry]);
        Assert.Equal("MSG-GEN-01", await (await owner.Client.SavePushAsync((NotificationTypes.CooperativeCertificationExpiry, false))).ErrorCodeAsync());

        foreach (var role in new[] { StaffRole.ZoneManager, StaffRole.Accountant })
        {
            var staff = await factory.StaffAsync(null, role);
            Assert.DoesNotContain(NotificationTypes.CooperativeCertificationExpiry, (await staff.Client.OfferedTypesAsync()).Keys);
            Assert.Equal("MSG-GEN-01", await (await staff.Client.SavePushAsync((NotificationTypes.CooperativeCertificationExpiry, true))).ErrorCodeAsync());
        }
    }

    private static async Task<Guid> LicenseAsync(Actor owner, DateOnly today, int daysLeft)
    {
        var zone = await owner.Client.CreateZoneAsync();
        return (await owner.Client.CreateLicenseAsync(new LicenseFields
        {
            ZoneId = zone.Id(), EffectiveDate = today.AddYears(-2), ExpiryDate = today.AddDays(daysLeft),
        })).Id();
    }

    private async Task AssertOneNoticeAsync(Actor owner, Guid licenseId, string code, int? days)
    {
        var notice = Assert.Single(await NoticesAsync(owner, licenseId));
        Assert.Equal((code, NotificationTypes.ZoneLicenseExpiry, true), (notice.MessageCode, notice.Type, notice.IsMandatory));
        var names = Params(notice).EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(days is null
            ? ["document_number", "document_type", "expiry_date", "zone_codes"]
            : (IEnumerable<string>)["days", "document_number", "document_type", "expiry_date", "zone_codes"], names);
        if (days is { } expected)
        {
            Assert.Equal(expected, Params(notice).GetProperty("days").GetInt32());
        }
    }

    private Task<List<Notification>> NoticesAsync(Actor owner, Guid recordId) => factory.QueryAsync(db => db.Notifications
        .Where(n => n.RecipientAccountId == owner.Account.Id && n.RelatedRecordId == recordId).ToListAsync());

    private static JsonElement Params(Notification notice) => JsonDocument.Parse(notice.MessageParams!).RootElement;
}
