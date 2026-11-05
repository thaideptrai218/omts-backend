using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// BR-WATER-05 and the PM decision of 03/11: the water alert goes to the Cooperative Owner and the Zone Managers of the
/// zone, except the Owner who did the action. MSG-WATER-03 names the zone, the raft, the time and each value outside.
/// </summary>
public class WaterAlertRecipientsTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Out_of_range_alerts_each_recipient_exactly_once_even_when_sent_twice()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var (_, otherZoneManager) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        var operationId = Guid.NewGuid();

        var first = await z.RecordAsync(9.1m, 20m, clientOperationId: operationId);
        var again = await z.RecordAsync(9.1m, 20m, clientOperationId: operationId);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(body.Id(), (await again.Content.ReadFromJsonAsync<JsonElement>()).Id());
        Assert.Equal("Active", body.Text("alertState"));
        Assert.Equal("pH 9.10 > 8.50", body.Text("violatedValues"));
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));

        var alerts = await api.AlertsOfAsync(body.Id());
        // The Owner recorded it: not told of their own action (PM decision 03/11); the zone's manager still is.
        Assert.DoesNotContain(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == manager.Id);
        Assert.DoesNotContain(alerts, n => n.RecipientAccountId == otherZoneManager.Id);
        Assert.All(alerts, n => Assert.True(n.IsMandatory));
        // MSG-WATER-03: zone, raft, time and each value outside with its allowed range.
        Assert.All(alerts, n =>
        {
            Assert.Equal("MSG-WATER-03", n.MessageCode);
            Assert.StartsWith($"Water alert: zone {z.Zone.Name}, raft {z.Raft.Code}, measured on ", n.Body);
            Assert.EndsWith(". pH 9.1 is too high (allowed 6.5–8.5). Please check the water.", n.Body);
        });
        var pushed = api.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Id.ToString());
        Assert.Single(pushed, m => m.Text.Contains("pH 9.1 is too high (allowed 6.5–8.5)."));
    }

    [Fact]
    public async Task Zone_manager_recording_alerts_the_owner_every_manager_of_the_zone_and_managers_at_all_zones_but_no_accountant()
    {
        var z = await api.ZoneAsync();
        var (recorderClient, recorder) = await api.StaffAsync(z.Zone);
        var (_, colleague) = await api.StaffAsync(z.Zone);
        var (_, accountant) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var allZones = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(allZones, StaffRole.ZoneManager, zone: null);

        var body = await z.RecordedAsync(9.2m, 20m, client: recorderClient);

        var alerts = await api.AlertsOfAsync(body.Id());
        Assert.Single(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        // A staff actor stays: a Zone Manager still gets the safety alert of their own reading.
        Assert.Single(alerts, n => n.RecipientAccountId == recorder.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == colleague.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == allZones.Id);
        Assert.DoesNotContain(alerts, n => n.RecipientAccountId == accountant.Id);
    }

    // The filter removes only the acting Owner: another Owner of the cooperative is told.
    [Fact]
    public async Task Owner_recording_gets_no_alert_but_the_other_owner_and_the_zone_manager_do()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var otherOwner = await api.AddAccountAsync(NewCode(), Password);

        var body = await z.RecordedAsync(9.2m, 20m);

        var alerts = await api.AlertsOfAsync(body.Id());
        Assert.DoesNotContain(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == otherOwner.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == manager.Id);
    }

    [Fact]
    public async Task Every_indicator_outside_is_named_in_one_alert_with_its_range()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);

        var body = await z.RecordedAsync(5.9m, 31.2m, client: manager, temperature: 36.5m);

        Assert.Equal("pH 5.90 < 6.50; Salinity 31.20 > 30.00; Temperature 36.50 > 35.00", body.Text("violatedValues"));
        Assert.Single(await api.AlertsOfAsync(body.Id()), n => n.RecipientAccountId == z.OwnerAccount.Id
            && n.Body!.EndsWith(". pH 5.9 is too low (allowed 6.5–8.5); Salinity 31.2 ‰ is too high (allowed 15–30 ‰); Temperature 36.5 °C is too high (allowed 10–35 °C). Please check the water.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_temperature_outside_alone_raises_the_alert()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);

        var body = await z.RecordedAsync(7m, 20m, client: manager, temperature: 9m);

        Assert.Equal("Temperature 9.00 < 10.00", body.Text("violatedValues"));
        Assert.NotEmpty(await api.AlertsOfAsync(body.Id()));
    }
}
