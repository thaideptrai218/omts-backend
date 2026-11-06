using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// Each branch of WATER-02.2 and BR-WATER-05 for a correction that keeps the measurement where it is among the others: the
/// latest and an older one. A correction that moves it: <see cref="WaterCorrectionMovedTimeNoticeTests"/>; a void:
/// <see cref="WaterVoidNoticeTests"/>; who receives them: <see cref="WaterCorrectionRecipientsTests"/>.
/// </summary>
public class WaterCorrectionNotifyMatrixTests(AuthApiFactory api) : WaterNoticeTestBase(api), IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(9.1, 20, 9.1, 31)] // A value more is outside.
    [InlineData(9.1, 31, 9.1, 20)] // A value fewer, some still outside.
    [InlineData(9.1, 20, 7, 31)] // Another value instead.
    [InlineData(7, 20, 9.1, 20)] // Nothing was outside.
    public async Task Latest_with_other_values_outside_gets_a_new_alert_marked_as_a_correction(double ph, double salinity, double newPh, double newSalinity)
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, (decimal)ph, (decimal)salinity, Latest);
        var alertsBefore = await ManagerAlertsAsync(id, manager);

        var body = await CorrectedAsync(z, id, (decimal)newPh, (decimal)newSalinity, Latest);

        var alerts = await ManagerAlertsAsync(id, manager);
        Assert.Equal(alertsBefore.Count + 1, alerts.Count);
        Assert.Single(alerts, n => n.Body!.StartsWith($"Water alert (corrected measurement): zone {z.Zone.Name}, raft {z.Raft.Code}, measured on {BusinessClock.Format(Latest)}.", StringComparison.Ordinal));
        Assert.Empty(await Api.WithdrawalsOfAsync(id));
        Assert.Equal(JsonValueKind.String, body.GetProperty("alertedAt").ValueKind);
    }

    [Theory]
    [InlineData(9.1, 9.3)] // Another reading of the same value, still too high.
    [InlineData(6.0, 6.2)] // Another reading of the same value, still too low.
    public async Task Latest_with_the_same_values_outside_on_the_same_side_sends_nothing_and_keeps_the_time_of_the_alert(double ph, double newPh)
    {
        var (z, manager) = await ZoneAsync();
        var measurement = await z.RecordedAsync((decimal)ph, 20m, measuredAt: Latest);
        var alertsBefore = await ManagerAlertsAsync(measurement.Id(), manager);

        var body = await CorrectedAsync(z, measurement.Id(), (decimal)newPh, 20m, Latest);

        Assert.Equal(alertsBefore.Count, (await ManagerAlertsAsync(measurement.Id(), manager)).Count);
        Assert.Empty(await Api.WithdrawalsOfAsync(measurement.Id()));
        Assert.Equal(measurement.GetProperty("alertedAt").GetDateTimeOffset(), body.GetProperty("alertedAt").GetDateTimeOffset());
        Assert.Contains(FormattableString.Invariant($"{newPh:0.00}"), body.Text("violatedValues"));
        Assert.Equal("Active", body.Text("alertState"));
    }

    [Fact]
    public async Task Latest_with_no_value_outside_any_more_withdraws_the_alert_with_msg_water_04()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9.1m, 20m, Latest);
        var alertsBefore = await ManagerAlertsAsync(id, manager);

        var body = await CorrectedAsync(z, id, 8m, 20m, Latest, reason: "Đọc nhầm số");

        Assert.Equal(alertsBefore.Count, (await ManagerAlertsAsync(id, manager)).Count);
        var withdrawn = Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.Equal("MSG-WATER-04", withdrawn.MessageCode);
        Assert.True(withdrawn.IsMandatory);
        Assert.Equal(
            $"The water alert of zone {z.Zone.Name} (measured on {BusinessClock.Format(Latest)}) was withdrawn: {z.OwnerAccount.DisplayName} corrected the measurement. Reason: Đọc nhầm số.",
            withdrawn.Body);
        Assert.Equal("None", body.Text("alertState"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("alertedAt").ValueKind);
    }

    [Fact]
    public async Task Latest_with_nothing_outside_before_or_after_sends_nothing()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 7m, 20m, Latest);

        await CorrectedAsync(z, id, 7.5m, 21m, Latest);

        Assert.Empty(await ManagerAlertsAsync(id, manager));
        Assert.Empty(await Api.WithdrawalsOfAsync(id));
    }

    [Fact]
    public async Task A_new_alert_names_the_raft_and_the_time_of_the_correction()
    {
        var (z, manager) = await ZoneAsync();
        var other = await Api.RaftAsync(z.Zone);
        var id = await RecordAsync(z, 7m, 20m, Latest);

        await CorrectedAsync(z, id, 9m, 20m, MovedLater, raftId: other.Id);

        var alert = Assert.Single(await ManagerAlertsAsync(id, manager));
        Assert.StartsWith($"Water alert (corrected measurement): zone {z.Zone.Name}, raft {other.Code}, measured on {BusinessClock.Format(MovedLater)}.", alert.Body);
    }

    [Theory]
    [InlineData(7, 9.1)]
    [InlineData(9.1, 7)]
    [InlineData(9.1, 7.1)]
    public async Task An_older_measurement_sends_nothing_whatever_changes(double ph, double newPh)
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, (decimal)ph, 20m, Older);
        await RecordAsync(z, 7m, 20m, Latest);
        var alertsBefore = await ManagerAlertsAsync(id, manager);

        await CorrectedAsync(z, id, (decimal)newPh, 20m, Older);

        Assert.Equal(alertsBefore.Count, (await ManagerAlertsAsync(id, manager)).Count);
        Assert.Empty(await Api.WithdrawalsOfAsync(id));
    }

    [Fact]
    public async Task A_measurement_saved_earlier_at_the_same_time_counts_as_older()
    {
        var (z, manager) = await ZoneAsync();
        var first = await RecordAsync(z, 7m, 20m, Latest);
        await RecordAsync(z, 7m, 20m, Latest);

        await CorrectedAsync(z, first, 9m, 20m, Latest);

        Assert.Empty(await ManagerAlertsAsync(first, manager));
    }

    [Fact]
    public async Task A_voided_later_one_does_not_count_so_the_one_before_it_is_the_latest()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9m, 20m, Older);
        var voidedLater = await RecordAsync(z, 7m, 20m, Latest);
        await z.Owner.VoidAsync(voidedLater);

        await CorrectedAsync(z, id, 7m, 20m, Older);

        Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
    }
}
