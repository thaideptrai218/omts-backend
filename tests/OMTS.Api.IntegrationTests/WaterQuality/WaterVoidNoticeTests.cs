using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Application.Common;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-02.2 for a void: judged before the void, and a voided measurement counts as having no value outside, so only a
/// void of the latest active measurement with a value outside sends MSG-WATER-04.
/// </summary>
public class WaterVoidNoticeTests(AuthApiFactory api) : WaterNoticeTestBase(api), IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Voiding_the_latest_with_a_value_outside_withdraws_the_alert_with_msg_water_04()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9m, 20m, Latest);
        var alertsBefore = await ManagerAlertsAsync(id, manager);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(id, reason: "Đo nhầm vùng")).StatusCode);

        Assert.Equal(alertsBefore.Count, (await ManagerAlertsAsync(id, manager)).Count);
        var withdrawn = Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.Equal(
            $"The water alert of zone {z.Zone.Name} (measured on {BusinessClock.Format(Latest)}) was withdrawn: {z.OwnerAccount.DisplayName} voided the measurement. Reason: Đo nhầm vùng.",
            withdrawn.Body);
    }

    [Fact]
    public async Task Voiding_the_latest_tells_nothing_about_the_earlier_one_that_becomes_the_latest()
    {
        var (z, manager) = await ZoneAsync();
        var earlier = await RecordAsync(z, 9m, 20m, Older);
        var id = await RecordAsync(z, 9m, 31m, Latest);
        var earlierAlerts = await ManagerAlertsAsync(earlier, manager);

        await z.Owner.VoidAsync(id);

        Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.Equal(earlierAlerts.Count, (await ManagerAlertsAsync(earlier, manager)).Count);
        Assert.Empty(await Api.WithdrawalsOfAsync(earlier));
    }

    [Fact]
    public async Task Voiding_the_latest_with_nothing_outside_sends_nothing()
    {
        var (z, _) = await ZoneAsync();
        var id = await RecordAsync(z, 7m, 20m, Latest);

        await z.Owner.VoidAsync(id);

        Assert.Empty(await Api.AlertsOfAsync(id));
        Assert.Empty(await Api.WithdrawalsOfAsync(id));
    }

    [Fact]
    public async Task Voiding_an_older_one_sends_nothing_even_with_a_value_outside()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9m, 20m, Older);
        await RecordAsync(z, 7m, 20m, Latest);
        var alertsBefore = await ManagerAlertsAsync(id, manager);

        await z.Owner.VoidAsync(id);

        Assert.Equal(alertsBefore.Count, (await ManagerAlertsAsync(id, manager)).Count);
        Assert.Empty(await Api.WithdrawalsOfAsync(id));
    }

    // The notification of a voided measurement opens it: it stays in the history, marked Voided with its reason.
    [Fact]
    public async Task A_withdrawal_opens_the_voided_measurement_for_a_manager_of_the_zone()
    {
        var z = await Api.ZoneAsync();
        var (manager, account) = await Api.StaffAsync(z.Zone);
        var id = await RecordAsync(z, 9m, 20m, Latest);
        await z.Owner.VoidAsync(id, reason: "Đo nhầm vùng");
        var withdrawn = Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == account.Id);

        var opened = await manager.OpenNotificationAsync(withdrawn.Id);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(id, (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recordId").GetGuid());
    }
}
