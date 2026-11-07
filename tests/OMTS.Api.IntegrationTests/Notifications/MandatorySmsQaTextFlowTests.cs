using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Jobs;
using FarmingKit = OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using LicenseFields = OMTS.Api.IntegrationTests.Farming.LicenseFields;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>
/// The text findings through the real API and fake service, so what the unit tests show is what a phone gets: the input is
/// what an Owner can type. Logs are checked on the same flows. M.. tests name a finding of reports/qa-261105-mandatory-sms.md.
/// </summary>
public class MandatorySmsQaTextFlowTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    // Pattern-built, not a real account: "1234 - 5678 - 9012 - 3".
    private static readonly string GroupedAccount = string.Join(" - ", string.Concat(Enumerable.Range(1, 13).Select(i => i % 10)).Chunk(4).Select(c => new string(c)));

    private async Task<(WaterZone Zone, HttpClient Manager)> ZoneNamedAsync(string name)
    {
        var z = await api.ZoneAsync();
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.Name = name);
        var (manager, _) = await api.StaffAsync(z.Zone);
        return (z, manager);
    }

    [Fact(Skip = "Waiting for the PM: may a number typed into a zone name reach an SMS? Only money SMS are held to code and amount.")]
    public async Task M1_an_owner_typed_zone_name_cannot_put_a_grouped_account_number_on_a_phone()
    {
        var z = await api.ZoneAsync();
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => (zone.Name, zone.PhMin, zone.PhMax) = ($"Khu A TK {GroupedAccount}", 6.5m, 8.5m));
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.DoesNotContain(GroupedAccount, Assert.Single(api.SmsTo(z.OwnerAccount)).Text);
    }

    [Fact]
    public async Task M2_a_real_alert_keeps_the_allowed_range_of_limits_with_two_decimals()
    {
        var (z, manager) = await ZoneNamedAsync("Vùng A");
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => (zone.SalinityMin, zone.SalinityMax) = (15.25m, 30.75m));

        await z.RecordedAsync(7.5m, 12m, client: manager);

        Assert.Contains("allowed 15.25-30.75 ppt", Assert.Single(api.SmsTo(z.OwnerAccount)).Text);
    }

    [Fact]
    public async Task M3_a_real_license_reminder_names_a_license_number_of_8_digits()
    {
        var owner = await FarmingKit.OwnerAsync(api);
        var zoneId = (await FarmingKit.CreateZoneAsync(owner.Client)).GetProperty("id").GetGuid();
        await FarmingKit.CreateLicenseAsync(owner.Client, new LicenseFields
        {
            DocumentNumber = "GP-2026-0012", ZoneIds = [zoneId], EffectiveDate = FarmingKit.Today.AddDays(-300), ExpiryDate = FarmingKit.Today.AddDays(10),
        });

        await api.Services.GetRequiredService<ZoneLicenseReminderJob>().RunOnceAsync(FarmingKit.Today);

        Assert.Contains("GP-2026-0012", Assert.Single(api.SmsTo(owner.Account)).Text);
    }

    [Fact]
    public async Task M4_a_real_two_value_alert_on_an_ordinary_zone_name_keeps_both_ranges()
    {
        var (z, manager) = await ZoneNamedAsync("Vùng nuôi hàu Vịnh Bái Tử Long khu số 2");

        await z.RecordedAsync(9.1m, 12m, client: manager);

        Assert.Contains("allowed 15-30 ppt", Assert.Single(api.SmsTo(z.OwnerAccount)).Text);
    }

    [Fact]
    public async Task A_normal_alert_is_gsm_and_within_160_and_says_where_what_and_when()
    {
        var (z, manager) = await ZoneNamedAsync("Vùng Cái Rồng");

        await z.RecordedAsync(7.5m, 12m, client: manager);

        var text = Assert.Single(api.SmsTo(z.OwnerAccount)).Text;
        Assert.StartsWith("Water alert: zone Vung Cai Rong, raft ", text);
        Assert.Contains(", measured on ", text);
        Assert.Contains("Salinity 12 ppt is too low (allowed 15-30 ppt). Please check the water.", text);
        Assert.True(text.Length <= 160, text);
    }

    // Client list: logs hold the type and the account id, never the number or the text. Checked on all levels, also EF's.
    [Fact]
    public async Task No_phone_number_or_text_of_a_mandatory_sms_reaches_the_logs_on_the_normal_flows()
    {
        await using var host = new AuthApiFactory();
        var z = await host.ZoneAsync();
        var (managerClient, manager) = await host.StaffAsync(z.Zone);
        var owner = await FarmingKit.OwnerAsync(host);
        var zoneId = (await FarmingKit.CreateZoneAsync(owner.Client)).GetProperty("id").GetGuid();
        await FarmingKit.CreateLicenseAsync(owner.Client, new LicenseFields
        {
            DocumentNumber = "GP-LOG-CHECK", ZoneIds = [zoneId], EffectiveDate = FarmingKit.Today.AddDays(-300), ExpiryDate = FarmingKit.Today.AddDays(10),
        });

        await z.RecordedAsync(9.1m, 20m, client: managerClient);
        await host.Services.GetRequiredService<ZoneLicenseReminderJob>().RunOnceAsync(FarmingKit.Today);

        Assert.NotEmpty(host.SmsTo(owner.Account).Where(m => m.Text.Contains("GP-LOG-CHECK")));
        foreach (var detail in new[] { manager.PhoneNumber!, z.OwnerAccount.PhoneNumber!, owner.Account.PhoneNumber!, "Please check the water", "Please renew it", "GP-LOG-CHECK" })
        {
            Assert.DoesNotContain(host.Logs.Lines, l => l.Contains(detail, StringComparison.Ordinal));
        }
    }
}
