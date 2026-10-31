using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using FarmingKit = OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using LicenseFields = OMTS.Api.IntegrationTests.Farming.LicenseFields;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>
/// QA of OMTS-216 PR 0.5 on the contents (BR-ACCOUNT-12 "keeps the content it was sent with"): the real WATER-01/02 and
/// FARM-03 flows store the SRS message code with every placeholder filled, and the related record opens or answers
/// MSG-ACCOUNT-22 once removed.
/// </summary>
public class NotificationsV2ContentQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string TestPassword = NewPassword();

    // MSG-WATER-03 (BR-WATER-05): zone, raft, time and each value outside; "(corrected measurement)" after a correction.
    [Fact]
    public async Task A_water_alert_and_its_correction_store_msg_water_03_with_every_placeholder()
    {
        var z = await factory.ZoneAsync();
        var measuredAt = Minute(DateTimeOffset.UtcNow.AddMinutes(-30));
        var time = BusinessClock.Format(measuredAt);
        var first = (await z.RecordedAsync(9.1m, 20m, measuredAt: measuredAt)).GetProperty("id").GetGuid();
        var corrected = await z.Owner.CorrectAsync(first, 9.4m, 31m, recordVersion: 1, measuredAt: measuredAt);
        Assert.True(corrected.StatusCode == HttpStatusCode.Created, await corrected.Content.ReadAsStringAsync());
        var second = (await corrected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var items = (await z.Owner.NotificationPageAsync()).Items();
        var alert = items.Single(i => i.GetProperty("relatedRecordId").GetGuid() == first);
        var correction = items.Single(i => i.GetProperty("relatedRecordId").GetGuid() == second);

        AssertAlert(alert, "", "pH 9.1 is too high (allowed 6.5–8.5)");
        Assert.Equal($"Water alert: zone {z.Zone.Name}, raft –, measured on {time}. pH 9.1 is too high (allowed 6.5–8.5). Please check the water.", alert.GetProperty("body").GetString());
        AssertAlert(correction, "(corrected measurement)", "pH 9.4 is too high (allowed 6.5–8.5); Salinity 31 ‰ is too high (allowed 15–30 ‰)");
        Assert.StartsWith($"Water alert (corrected measurement): zone {z.Zone.Name}, raft –, measured on {time}.", correction.GetProperty("body").GetString());

        // The first alert still opens: its measurement exists, now as an earlier version (shown with its current status).
        var open = await z.Owner.OpenNotificationAsync(alert.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(z.Zone.Id, (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("zoneId").GetGuid());

        // ACCOUNT-08.0.E1: a removed measurement has no code to name.
        await factory.UseDbAsync(db => db.WaterMeasurements.Remove(db.WaterMeasurements.Single(m => m.Id == second)));
        var removed = await z.Owner.OpenNotificationAsync(correction.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        var problem = await removed.ProblemAsync();
        Assert.Equal(("MSG-ACCOUNT-22", "Water measurement no longer exists."), (problem.GetProperty("code").GetString(), problem.GetProperty("detail").GetString()));

        void AssertAlert(JsonElement item, string correctionNote, string valuesOutside)
        {
            Assert.Equal(("WATER_ALERT", "MSG-WATER-03", true), (item.GetProperty("type").GetString(), item.GetProperty("messageCode").GetString(), item.GetProperty("mandatory").GetBoolean()));
            Assert.Equal(("WaterMeasurement", JsonValueKind.Null), (item.GetProperty("relatedRecordType").GetString(), item.GetProperty("relatedRecordCode").ValueKind));
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["correction_note"] = correctionNote, ["zone_name"] = z.Zone.Name, ["raft_code"] = "–", ["measurement_time"] = time,
                    ["values_outside"] = valuesOutside,
                },
                Params(item));
        }
    }

    // MSG-FARM-01 then MSG-FARM-02 (GFR-03) with {document} = license number and zone; the license opens for the Owner
    // only (Owner-only FARM-03) and answers MSG-ACCOUNT-22 with its number once removed.
    [Fact]
    public async Task License_reminders_store_msg_farm_01_and_02_and_open_for_the_owner_only()
    {
        var owner = await FarmingKit.OwnerAsync(factory);
        var zoneId = (await FarmingKit.CreateZoneAsync(owner.Client)).GetProperty("id").GetGuid();
        var zone = await FarmingKit.ZoneEntityAsync(factory, zoneId);
        var today = FarmingKit.Today;
        var license = await FarmingKit.CreateLicenseAsync(owner.Client, new LicenseFields { ZoneIds = [zoneId], EffectiveDate = today.AddDays(-300), ExpiryDate = today.AddDays(30) });
        var (licenseId, number) = (license.GetProperty("id").GetGuid(), license.GetProperty("documentNumber").GetString()!);
        var job = factory.Services.GetRequiredService<ZoneLicenseReminderJob>();
        var document = $"Marine aquaculture license {number} ({zone.Name})";
        var expiry = today.AddDays(30).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        await job.RunOnceAsync(today);
        await job.RunOnceAsync(today.AddDays(31));

        var items = (await owner.Client.NotificationPageAsync()).Items().Where(i => i.GetProperty("relatedRecordId").GetGuid() == licenseId).ToList();
        var expiring = items.Single(i => i.GetProperty("messageCode").GetString() == "MSG-FARM-01");
        var expired = items.Single(i => i.GetProperty("messageCode").GetString() == "MSG-FARM-02");
        Assert.Equal(new Dictionary<string, string> { ["document"] = document, ["expiry_date"] = expiry, ["days"] = "30" }, Params(expiring));
        Assert.Equal(new Dictionary<string, string> { ["document"] = document, ["expiry_date"] = expiry }, Params(expired));
        Assert.All(items, i => Assert.Equal(
            ("ZONE_LICENSE_EXPIRY", true, "ZoneComplianceDocument", number),
            (i.GetProperty("type").GetString(), i.GetProperty("mandatory").GetBoolean(), i.GetProperty("relatedRecordType").GetString(), i.GetProperty("relatedRecordCode").GetString())));

        var opened = await owner.Client.OpenNotificationAsync(expired.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("zoneId").ValueKind);

        // A Zone Manager of the zone holding the same notification may not open the license (GFR-02).
        var manager = await factory.AddAccountAsync($"u{NewCode()}", TestPassword, SubjectType.Staff);
        await factory.GrantAsync(manager, StaffRole.ZoneManager, zone);
        var managerClient = await factory.LoginAsync(manager.Username, TestPassword);
        await factory.NotifyAsync([manager.Id], new NotificationContent(
            NotificationTypes.ZoneLicenseExpiry, "Zone license expired", UserMessages.LicenseExpired(document, expiry),
            new RelatedRecord(RelatedRecordTypes.ZoneLicense, licenseId, number)));
        var managerOpen = await managerClient.OpenNotificationAsync((await managerClient.NotificationPageAsync()).NotificationIds().Single());
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (managerOpen.StatusCode, await managerOpen.ErrorCodeAsync()));

        await factory.UseDbAsync(db => db.ZoneComplianceDocuments.Remove(db.ZoneComplianceDocuments.Single(d => d.Id == licenseId)));
        var removed = await owner.Client.OpenNotificationAsync(expired.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Equal($"Zone license {number} no longer exists.", (await removed.ProblemAsync()).GetProperty("detail").GetString());
    }

    private static Dictionary<string, string> Params(JsonElement item) =>
        item.GetProperty("messageParams").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    private static DateTimeOffset Minute(DateTimeOffset value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, value.Offset);
}
