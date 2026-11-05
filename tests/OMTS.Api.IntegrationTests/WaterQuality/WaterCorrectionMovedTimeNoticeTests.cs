using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-02.2 for a correction that moves the measurement time. "Latest" is judged at step 6, so with the corrected time;
/// only for a void does the SRS say "before" (open question 16 of the gap report, for the PM).
/// </summary>
public class WaterCorrectionMovedTimeNoticeTests(AuthApiFactory api) : WaterNoticeTestBase(api), IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task An_older_one_moved_to_become_the_latest_with_a_value_outside_alerts()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 7m, 20m, Older);
        await RecordAsync(z, 7m, 20m, Latest);

        await CorrectedAsync(z, id, 9m, 20m, MovedLater);

        var alert = Assert.Single(await ManagerAlertsAsync(id, manager));
        Assert.StartsWith("Water alert (corrected measurement): ", alert.Body);
        Assert.Contains($"measured on {BusinessClock.Format(MovedLater)}.", alert.Body);
    }

    [Fact]
    public async Task An_older_one_moved_to_become_the_latest_with_no_value_outside_any_more_withdraws_naming_the_time_of_its_alert()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9m, 20m, Older);
        await RecordAsync(z, 7m, 20m, Latest);

        await CorrectedAsync(z, id, 7m, 20m, MovedLater);

        var withdrawn = Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.Contains($"(measured on {BusinessClock.Format(Older)})", withdrawn.Body);
    }

    [Fact]
    public async Task An_older_one_moved_to_become_the_latest_with_the_same_values_outside_sends_nothing()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9m, 20m, Older);
        await RecordAsync(z, 7m, 20m, Latest);
        var alertsBefore = await ManagerAlertsAsync(id, manager);

        await CorrectedAsync(z, id, 9m, 20m, MovedLater);

        Assert.Equal(alertsBefore.Count, (await ManagerAlertsAsync(id, manager)).Count);
        Assert.Empty(await Api.WithdrawalsOfAsync(id));
    }

    [Theory]
    [InlineData(9, 7)] // No value outside any more.
    [InlineData(7, 9)] // A value outside now.
    [InlineData(9, 9.4)] // The same value outside.
    public async Task The_latest_moved_before_another_one_stops_being_the_latest_and_sends_nothing(double ph, double newPh)
    {
        var (z, manager) = await ZoneAsync();
        await RecordAsync(z, 7m, 20m, Older);
        var id = await RecordAsync(z, (decimal)ph, 20m, Latest);
        var alertsBefore = await ManagerAlertsAsync(id, manager);

        await CorrectedAsync(z, id, (decimal)newPh, 20m, MovedEarlier);

        Assert.Equal(alertsBefore.Count, (await ManagerAlertsAsync(id, manager)).Count);
        Assert.Empty(await Api.WithdrawalsOfAsync(id));
    }

    [Fact]
    public async Task The_latest_moved_later_stays_the_latest_and_follows_the_matrix()
    {
        var (z, manager) = await ZoneAsync();
        await RecordAsync(z, 7m, 20m, Older);
        var id = await RecordAsync(z, 9m, 20m, Latest);

        await CorrectedAsync(z, id, 7m, 20m, MovedLater);

        var withdrawn = Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.Contains($"(measured on {BusinessClock.Format(Latest)})", withdrawn.Body);
    }
}
