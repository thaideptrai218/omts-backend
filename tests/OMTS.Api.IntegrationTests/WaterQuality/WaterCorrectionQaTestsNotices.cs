using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of WATER-02.2 and BR-WATER-05 against the SRS text, branch by branch. A test named Fnn fails on purpose: see the QA report.</summary>
public class WaterCorrectionQaTestsNotices(AuthApiFactory api) : WaterNoticeTestBase(api), IClassFixture<AuthApiFactory>
{
    // F-01: BR-WATER-05 "when the values outside are different"; POST-2 "changed the result". The standing alert says pH is too
    // low and the Zone Manager acts on that; after the correction pH is too high and nobody is told.
    [Fact]
    public async Task F01_the_same_value_moved_to_the_other_side_of_its_range_sends_a_new_alert()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 6m, 20m, Latest);
        var before = (await ManagerAlertsAsync(id, manager)).Count;

        var body = await CorrectedAsync(z, id, 9m, 20m, Latest);

        Assert.Equal("pH 9.00 > 8.50", body.Text("violatedValues"));
        Assert.Equal(before + 1, (await ManagerAlertsAsync(id, manager)).Count);
    }

    // F-02: BR-WATER-05 "after a correction ... of the latest active measurement": it was the latest, its alert was sent, and the
    // correction takes the alert of the zone away. The earlier alert stands with no withdrawal.
    [Fact]
    public async Task F02_the_latest_with_a_standing_alert_corrected_in_range_and_moved_back_withdraws_the_alert()
    {
        var (z, manager) = await ZoneAsync();
        await RecordAsync(z, 7m, 20m, Older);
        var id = await RecordAsync(z, 9m, 20m, Latest);

        await CorrectedAsync(z, id, 7m, 20m, MovedEarlier);

        Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
    }

    [Fact]
    public async Task A_new_alert_names_only_the_values_still_outside_with_the_ranges_of_the_measurement()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9.1m, 31m, Latest);

        await CorrectedAsync(z, id, 9.1m, 20m, Latest);

        var alert = (await ManagerAlertsAsync(id, manager)).Single(n => n.Body!.Contains("corrected measurement"));
        Assert.Contains("pH", alert.Body);
        Assert.DoesNotContain("Salinity", alert.Body);
        Assert.Contains("\"corrected\":true", alert.MessageParams);
        Assert.Contains("6.5", alert.Body);
        Assert.Contains("8.5", alert.Body);
    }

    [Fact]
    public async Task A_temperature_back_in_range_withdraws_and_a_temperature_outside_alerts()
    {
        var (z, manager) = await ZoneAsync();
        var id = (await z.RecordedAsync(7m, 20m, measuredAt: Latest, temperature: 40m)).Id();

        var inRange = await z.Owner.CorrectAsync(id, 7m, 20m, measuredAt: Latest, temperature: 25m, raftId: z.Raft.Id);
        Assert.Equal(HttpStatusCode.OK, inRange.StatusCode);
        Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);

        var outside = await z.Owner.CorrectAsync(id, 7m, 20m, measuredAt: Latest, temperature: 36m, raftId: z.Raft.Id);
        Assert.Equal(HttpStatusCode.OK, outside.StatusCode);
        Assert.Contains(await ManagerAlertsAsync(id, manager), n => n.Body!.Contains("corrected measurement") && n.Body.Contains("Temperature"));
    }

    [Fact]
    public async Task The_bounds_are_inclusive_and_a_chain_of_corrections_sends_one_notice_per_change_of_result()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9m, 20m, Latest);
        var created = (await ManagerAlertsAsync(id, manager)).Count;

        await CorrectedAsync(z, id, 8.5m, 20m, Latest); // On the bound: inside, withdraw.
        await CorrectedAsync(z, id, 8.51m, 20m, Latest); // Outside: alert.
        await CorrectedAsync(z, id, 8.6m, 20m, Latest); // Same value outside: nothing.
        await CorrectedAsync(z, id, 8.6m, 31m, Latest); // One more outside: alert.
        await CorrectedAsync(z, id, 8.4m, 31m, Latest); // Another one instead: alert.
        await CorrectedAsync(z, id, 8.4m, 30m, Latest); // On the bound: inside, withdraw.
        await CorrectedAsync(z, id, 8.3m, 29m, Latest); // Inside to inside: nothing.
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(id)).StatusCode); // Void with nothing outside: nothing.

        Assert.Equal(created + 3, (await ManagerAlertsAsync(id, manager)).Count);
        Assert.Equal(2, (await Api.WithdrawalsOfAsync(id)).Count(n => n.RecipientAccountId == manager.Id));
    }

    [Fact]
    public async Task Voiding_the_only_measurement_with_a_value_outside_sends_one_withdrawal()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9m, 31m, Latest);

        await z.Owner.VoidAsync(id, reason: "  Đo nhầm vùng  ");

        var withdrawn = Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.EndsWith("voided the measurement. Reason: Đo nhầm vùng.", withdrawn.Body);
        Assert.Contains("+07:00", withdrawn.MessageParams);
        Assert.Contains("\"corrected_or_voided\":\"voided\"", withdrawn.MessageParams);
    }

    // BR-WATER-04: "latest" is the measurement time, as an instant, whatever offset the client sent.
    [Fact]
    public async Task Latest_compares_instants_not_the_clock_time_of_the_offset_sent()
    {
        var (z, manager) = await ZoneAsync();
        var older = await RecordAsync(z, 7m, 20m, Older);
        await RecordAsync(z, 7m, 20m, Latest);
        var wallClockEarlierInstantLater = MovedLater.ToOffset(TimeSpan.FromHours(-11));

        var body = await CorrectedAsync(z, older, 9m, 20m, wallClockEarlierInstantLater);

        Assert.Single(await ManagerAlertsAsync(older, manager));
        Assert.Equal(MovedLater, body.GetProperty("measuredAt").GetDateTimeOffset());

        var (z2, manager2) = await ZoneAsync();
        var older2 = await RecordAsync(z2, 7m, 20m, Older);
        await RecordAsync(z2, 7m, 20m, Latest);
        await CorrectedAsync(z2, older2, 9m, 20m, Older.AddMinutes(1).ToOffset(TimeSpan.FromHours(14)));
        Assert.Empty(await ManagerAlertsAsync(older2, manager2));
    }

    // Not a defect: the SRS says what a correction of an older one and a void of a later one do. Kept to pin down what the client
    // list §7 means by "alertedAt can be null with alertState Active".
    [Fact]
    public async Task An_older_one_corrected_outside_is_active_without_an_alert_also_when_a_void_makes_it_the_latest()
    {
        var (z, manager) = await ZoneAsync();
        var older = await RecordAsync(z, 7m, 20m, Older);
        var latest = await RecordAsync(z, 7m, 20m, Latest);

        var corrected = await CorrectedAsync(z, older, 9m, 20m, Older);
        Assert.Equal(("Active", JsonValueKind.Null), (corrected.Text("alertState"), corrected.GetProperty("alertedAt").ValueKind));

        await z.Owner.VoidAsync(latest);

        Assert.Empty(await ManagerAlertsAsync(older, manager));
        var detail = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{older}");
        Assert.Equal(("Active", JsonValueKind.Null), (detail.Text("alertState"), detail.GetProperty("alertedAt").ValueKind));
        var row = await Api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == older));
        Assert.True(row.ThresholdExceeded);
    }

    // Note N-1 of the report: the literal rule sends MSG-WATER-04 for an alert no notification ever announced.
    [Fact]
    public async Task N1_a_withdrawal_can_name_an_alert_that_was_never_sent()
    {
        var (z, manager) = await ZoneAsync();
        var older = await RecordAsync(z, 7m, 20m, Older);
        var latest = await RecordAsync(z, 7m, 20m, Latest);
        await CorrectedAsync(z, older, 9m, 20m, Older);
        await z.Owner.VoidAsync(latest);

        await CorrectedAsync(z, older, 7m, 20m, Older, reason: "Sai số");

        Assert.Empty(await ManagerAlertsAsync(older, manager));
        var withdrawn = Assert.Single(await Api.WithdrawalsOfAsync(older), n => n.RecipientAccountId == manager.Id);
        Assert.Contains($"(measured on {BusinessClock.Format(Older)})", withdrawn.Body);
    }
}
